namespace ErpNet.FP.Core.Drivers.RoTremol
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using ErpNet.FP.Core.Configuration;
    using Serilog;

    /// <summary>
    /// Tremol fiscal devices with Romanian firmware (S25, M20, M23, A19 Plus, FP15 ... "-RO").
    /// <para>
    /// Same ZFP frame as the Bulgarian Tremol driver (STX LEN NBL CMD DATA CS CS ETX, ping 09h),
    /// but the commands follow the Romanian protocol
    /// (Tremol "Communication Protocol" RO, version 2601151250, ZFP_SDK_RO_2601151250):
    /// </para>
    /// <list type="bullet">
    /// <item>30h Open receipt: OperNum;OperPass[4];0;0;PrintType — no unique sale number.</item>
    /// <item>31h Sale: NamePLU[36] = name[30] + 60h + unit[3] + 2 spaces; VAT classes A..E, F = "Alte taxe".</item>
    /// <item>35h Payment: Type;0;Amount;1.</item>
    /// <item>60h serial number only; 61h VAT number;FM number;VAT payer.</item>
    /// <item>No QR-code command: receipt number from 71h, amount from 33h subtotal, time from 68h.</item>
    /// <item>Texts are Windows-1252; Romanian diacritics are transliterated.</item>
    /// </list>
    /// </summary>
    public class RoTremolZfpFiscalPrinter : BgZfpFiscalPrinter
    {
        public const string OptionItemUnit = "itemUnit";
        public const string OptionUsnJournal = "usnJournal";
        public const string OptionUsnJournalPath = "usnJournalPath";

        protected const byte
            RoCommandReadSerialNumber = 0x60,
            RoCommandReadVatAndFmNumbers = 0x61,
            RoCommandReadPaymentTypes = 0x64,
            RoCommandReadLastAndTotalReceiptNum = 0x71;

        protected const int NamePartLength = 30;
        protected const int UnitPartLength = 3;
        protected const string DefaultItemUnit = "buc";

        private static readonly Regex RoUniqueSaleNumberPattern =
            new Regex("^[A-Za-z0-9._:/-]{1,64}$", RegexOptions.Compiled);

        private UsnJournal? usnJournal;
        private bool closeAttempted;
        private readonly Queue<string> pendingItemUnits = new Queue<string>();

        public RoTremolZfpFiscalPrinter(
            IChannel channel,
            ServiceOptions serviceOptions,
            IDictionary<string, string>? options = null)
        : base(channel, serviceOptions, options)
        {
            // The Romanian firmware uses code page 1252, not 1251 (Cyrillic).
            PrinterEncoding = CodePagesEncodingProvider.Instance.GetEncoding(1252)
                ?? throw new NotSupportedException("Encoding 1252 is missing. Register CodePagesEncodingProvider!");
        }

        /// <summary>Payment number of the "CARD" payment, read from the device at connect (64h).</summary>
        public string CardPaymentNumber { get; set; } = "1";

        public override IDictionary<string, string>? GetDefaultOptions()
        {
            return new Dictionary<string, string>
            {
                ["Operator.ID"] = "1",
                ["Operator.Password"] = "0000",
            };
        }

        protected string? PrinterOption(string key)
        {
            return string.IsNullOrEmpty(Info.SerialNumber)
                ? null
                : ServiceOptions.GetPrinterOption(Info.SerialNumber, key);
        }

        // ---------------------------------------------------------------------------------
        // Mappings
        // ---------------------------------------------------------------------------------

        public override string GetTaxGroupText(TaxGroup taxGroup)
        {
            return taxGroup switch
            {
                TaxGroup.TaxGroup1 => "A",
                TaxGroup.TaxGroup2 => "B",
                TaxGroup.TaxGroup3 => "C",
                TaxGroup.TaxGroup4 => "D",
                TaxGroup.TaxGroup5 => "E",
                TaxGroup.TaxGroup6 => "F",   // Alte taxe
                _ => throw new StandardizedStatusMessageException($"Tax group {taxGroup} unsupported (1..5 = A..E, 6 = Alte taxe)", "E411"),
            };
        }

        public override IDictionary<PaymentType, string> GetPaymentTypeMappings()
        {
            // Payment 0 is always cash. The card payment number is read from the device names (64h);
            // other payment types must be mapped explicitly in appsettings.json.
            var paymentTypeMappings = new Dictionary<PaymentType, string>
            {
                { PaymentType.Cash, "0" },
                { PaymentType.Card, CardPaymentNumber },
            };
            ServiceOptions.RemapPaymentTypes(Info.SerialNumber, paymentTypeMappings);
            return paymentTypeMappings;
        }

        // ---------------------------------------------------------------------------------
        // Status bytes (Romanian table, section 1.2.1 and 2.2.1)
        // ---------------------------------------------------------------------------------

        protected static readonly (string?, string)[] RoFiscalDeviceErrors = new (string?, string)[]{
            (null, string.Empty),
            ("E301", "Out of paper, printer failure"),
            ("E403", "Registers overflow"),
            ("E103", "Clock failure or incorrect date & time"),
            ("E404", "Opened fiscal receipt"),
            ("E406", "Payment residue account"),
            ("E404", "Opened non-fiscal receipt"),
            ("E405", "Registered payment but receipt is not closed"),
            ("E299", "Fiscal memory failure"),
            ("E408", "Incorrect password"),
            ("E105", "Missing external display"),
            ("E502", "24 hours block - missing Z report"),
            ("E304", "Overheated printer thermal head"),
            ("E305", "Interrupt power supply in fiscal receipt"),
            ("E206", "Overflow EJ"),
            ("E405", "Insufficient conditions"),
        };

        protected static readonly (string?, string)[] RoCommandErrors = new (string?, string)[] {
            (null, string.Empty),
            ("E402", "Invalid command"),
            ("E404", "Illegal command"),
            ("E405", "Z daily report is not zero"),
            ("E401", "Syntax error"),
            ("E403", "Input registers overflow"),
            ("E405", "Zero input registers"),
            ("E405", "Unavailable transaction for correction"),
            ("E405", "Insufficient amount on hand"),
            (null, string.Empty),                                         // 39h not used
            ("E404", "No access"),
            ("E599", "Certificates are not loaded"),
            ("E404", "Illegal command, server communications are active"),
        };

        protected static readonly (string?, string, StatusMessageType)[] RoStatusBits = new (string?, string, StatusMessageType)[] {
            // ST0
            ("E204", "FM Read only", StatusMessageType.Error),
            ("E305", "Power down in opened fiscal receipt", StatusMessageType.Error),
            ("E304", "Printer not ready or overheated", StatusMessageType.Error),
            ("E103", "Incorrect time", StatusMessageType.Error),
            ("E103", "Incorrect date", StatusMessageType.Error),
            ("E104", "RAM reset", StatusMessageType.Error),
            ("E103", "Date and time hardware error", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),
            // ST1
            ("E301", "Printer not ready or no paper", StatusMessageType.Error),
            ("E403", "Reports registers overflow", StatusMessageType.Error),
            ("E502", "Blocking after 24 hours", StatusMessageType.Error),
            ("W502", "Non-zero daily report", StatusMessageType.Warning),
            ("W502", "Non-zero article report", StatusMessageType.Warning),
            ("W502", "Non-zero operator report", StatusMessageType.Warning),
            (null, "Non-printed copy", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            // ST2
            (null, "Opened Non-fiscal Receipt", StatusMessageType.Info),
            (null, "Opened Fiscal Receipt", StatusMessageType.Info),
            (null, "Standard Cash Receipt", StatusMessageType.Info),
            (null, "VAT included in the receipt", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            ("W202", "EJ near full", StatusMessageType.Warning),
            ("E206", "EJ full", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),
            // ST3
            ("E205", "No FM module", StatusMessageType.Error),
            ("E299", "FM error", StatusMessageType.Error),
            ("E201", "FM full", StatusMessageType.Error),
            ("W201", "FM near full", StatusMessageType.Warning),
            (null, "Decimal point (1=fract, 0=whole)", StatusMessageType.Info),
            (null, "FM fiscalized", StatusMessageType.Info),
            (null, "FM produced", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            // ST4
            (null, "Printer: automatic cutting", StatusMessageType.Info),
            (null, "External Display Management", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            ("E105", "Missing external display", StatusMessageType.Error),
            (null, "Drawer: automatic opening", StatusMessageType.Info),
            (null, "Customer logo included in the receipt", StatusMessageType.Info),
            (null, "Service jumper", StatusMessageType.Info),
            (null, string.Empty, StatusMessageType.Reserved),
            // ST5
            ("E599", "No Sec.IC", StatusMessageType.Error),
            ("E599", "No certificates", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            ("E207", "No SD card response", StatusMessageType.Error),
            ("E207", "Wrong SD card", StatusMessageType.Error),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            // ST6
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            (null, string.Empty, StatusMessageType.Reserved),
            ("W301", "Near paper end", StatusMessageType.Warning),
            (null, string.Empty, StatusMessageType.Reserved),
            ("W504", "SIM is not activated", StatusMessageType.Warning),
            (null, string.Empty, StatusMessageType.Reserved),
        };

        protected override DeviceStatus ParseCommandStatus(byte[] status)
        {
            var deviceStatus = new DeviceStatus();
            AddFromTable(deviceStatus, RoFiscalDeviceErrors, status[0]);
            AddFromTable(deviceStatus, RoCommandErrors, status[1]);
            return deviceStatus;
        }

        private static void AddFromTable(DeviceStatus deviceStatus, (string?, string)[] table, byte value)
        {
            var index = value - 0x30;
            if (index == 0) return;
            if (index < 0 || index >= table.Length)
            {
                deviceStatus.AddError("E499", $"Unknown device error 0x{value:X2}");
                return;
            }
            var (code, text) = table[index];
            if (code != null && text.Length > 0)
            {
                deviceStatus.AddError(code, text);
            }
        }

        protected override DeviceStatus ParseDeviceStatus(byte[] status)
        {
            var deviceStatus = new DeviceStatus();
            for (var i = 0; i < status.Length && i < 7; i++)
            {
                for (var bit = 0; bit < 8; bit++)
                {
                    if ((status[i] & (1 << bit)) == 0) continue;
                    var (code, text, type) = RoStatusBits[i * 8 + bit];
                    if (type == StatusMessageType.Reserved) continue;
                    deviceStatus.AddMessage(new StatusMessage { Type = type, Code = code, Text = text });
                }
            }
            return deviceStatus;
        }

        // ---------------------------------------------------------------------------------
        // Device information
        // ---------------------------------------------------------------------------------

        public (string Serial, string Version, string VatNumber, string FmNumber, DeviceStatus Status) ReadIdentity()
        {
            var (serial, _) = Request(RoCommandReadSerialNumber);
            var (version, status) = Request(CommandVersion);
            var (vatFm, _) = Request(RoCommandReadVatAndFmNumbers);
            var fields = vatFm.Split(';');
            return (
                serial.Trim(),
                version.Trim(),
                fields.Length > 0 ? fields[0].Trim() : string.Empty,
                fields.Length > 1 ? fields[1].Trim() : string.Empty,
                status);
        }

        public override (string, DeviceStatus) GetRawDeviceInfo()
        {
            var (serial, version, vat, fm, status) = ReadIdentity();
            return ($"{version};{serial};{fm};{vat}", status);
        }

        public override (string, DeviceStatus) GetTaxIdentificationNumber()
        {
            var (response, status) = Request(RoCommandReadVatAndFmNumbers);
            var fields = response.Split(';');
            return (fields.Length > 0 ? fields[0].Trim() : string.Empty, status);
        }

        public override (string, DeviceStatus) GetFiscalMemorySerialNumber()
        {
            var (response, status) = Request(RoCommandReadVatAndFmNumbers);
            var fields = response.Split(';');
            var fm = fields.Length > 1 ? fields[1].Trim() : string.Empty;
            if (string.IsNullOrEmpty(fm)) fm = Info.FiscalMemorySerialNumber;
            return (fm, status.Ok ? status : new DeviceStatus());
        }

        /// <summary>Reads the payment names (64h) and returns the number of the first one containing "CARD".</summary>
        public string? DetectCardPaymentNumber()
        {
            var (response, status) = Request(RoCommandReadPaymentTypes);
            if (!status.Ok) return null;
            var names = response.Split(';');
            Log.Information($"{Info.SerialNumber}: payment types: {string.Join(" | ", names.Take(10).Select((n, i) => $"{i}={n.Trim()}"))}");
            for (var i = 1; i < Math.Min(names.Length, 10); i++)
            {
                if (names[i].ToUpperInvariant().Contains("CARD")) return i.ToString(CultureInfo.InvariantCulture);
            }
            return null;
        }

        private static readonly string[] DateTimeFormats = {
            "dd-MM-yyyy HH:mm", "dd-MM-yyyy HH:mm:ss", "dd-MM-yy HH:mm:ss", "dd-MM-yy HH:mm"
        };

        public override (DateTime?, DeviceStatus) GetDateTime()
        {
            var (response, status) = Request(CommandGetDateTime);
            if (!status.Ok)
            {
                status.AddInfo("Error occured while reading current date and time");
                return (null, status);
            }
            if (DateTime.TryParseExact(response.Trim(), DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dateTime))
            {
                return (dateTime, status);
            }
            status.AddInfo("Error occured while parsing current date and time");
            status.AddError("E409", $"Wrong format of date and time: '{response}'");
            return (null, status);
        }

        // ---------------------------------------------------------------------------------
        // Text helpers
        // ---------------------------------------------------------------------------------

        /// <summary>Code page 1252 has no ă, ș, ț: transliterate them so the receipt stays readable.</summary>
        public static string ToDeviceText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                sb.Append(c switch
                {
                    'ă' => 'a', 'Ă' => 'A',
                    'â' => 'a', 'Â' => 'A',
                    'î' => 'i', 'Î' => 'I',
                    'ș' or 'ş' => 's', 'Ș' or 'Ş' => 'S',
                    'ț' or 'ţ' => 't', 'Ț' or 'Ţ' => 'T',
                    ';' => ',',   // field separator in the protocol
                    '\r' or '\n' or '\t' => ' ',
                    _ => c
                });
            }
            return sb.ToString();
        }

        protected string DefaultUnit()
        {
            var unit = PrinterOption(OptionItemUnit);
            return string.IsNullOrWhiteSpace(unit) ? DefaultItemUnit : unit!.Trim();
        }

        /// <summary>NamePLU[36] = name padded to 30 + '`' (60h) + unit padded to 3 + 2 spaces.</summary>
        public static string BuildNamePlu(string name, string unit)
        {
            var namePart = ToDeviceText(name).WithMaxLength(NamePartLength).PadRight(NamePartLength);
            var unitPart = ToDeviceText(unit).WithMaxLength(UnitPartLength).PadRight(UnitPartLength);
            return namePart + "`" + unitPart + "  ";
        }

        protected static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        protected static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        protected string OperatorId(string? operatorId) =>
            string.IsNullOrEmpty(operatorId) ? Options.ValueOrDefault("Operator.ID", "1") : operatorId!;

        protected string OperatorPassword(string? operatorId, string? operatorPassword) =>
            string.IsNullOrEmpty(operatorId) || string.IsNullOrEmpty(operatorPassword)
                ? Options.ValueOrDefault("Operator.Password", "0000")
                : operatorPassword!;

        // ---------------------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------------------

        public override (string, DeviceStatus) OpenReceipt(string uniqueSaleNumber, string operatorId, string operatorPassword)
        {
            // <OperNum[1..2]>;<OperPass[4]>;0;0;<FiscalReceiptPrintType[1]>  — '2' postponed printing
            return Request(CommandOpenReceipt, string.Join(";",
                OperatorId(operatorId),
                OperatorPassword(operatorId, operatorPassword),
                "0",
                "0",
                "2"));
        }

        public override (string, DeviceStatus) AddItem(
            int department,
            string itemText,
            decimal unitPrice,
            TaxGroup taxGroup,
            decimal quantity = 0,
            decimal priceModifierValue = 0,
            PriceModifierType priceModifierType = PriceModifierType.None)
        {
            var unit = pendingItemUnits.Count > 0 ? pendingItemUnits.Dequeue() : DefaultUnit();
            var data = new FrameBuilder(PrinterEncoding)
                .Append(BuildNamePlu(itemText, unit))
                .Append(';')
                .Append(GetTaxGroupText(taxGroup))
                .Append(';')
                .Append(Amount(unitPrice));

            if (quantity != 0m)
            {
                data.Append('*').Append(Quantity(quantity));
            }

            switch (priceModifierType)
            {
                case PriceModifierType.DiscountPercent:
                    data.Append(',').Append(Amount(-priceModifierValue));
                    break;
                case PriceModifierType.SurchargePercent:
                    data.Append(',').Append(Amount(priceModifierValue));
                    break;
                case PriceModifierType.DiscountAmount:
                    data.Append(':').Append(Amount(-priceModifierValue));
                    break;
                case PriceModifierType.SurchargeAmount:
                    data.Append(':').Append(Amount(priceModifierValue));
                    break;
            }

            if (department > 0)
            {
                // '&' + DepNum (Dep01 = 81h ... Dep19 = 93h), raw byte
                data.Append('&').Append((byte)(department + 0x80));
            }

            return Request(CommandSellCorrection, data.ToArray());
        }

        public override (string, DeviceStatus) AddComment(string text)
        {
            return Request(CommandFreeText, ToDeviceText(text).WithMaxLength(Info.CommentTextMaxLength));
        }

        public override (string, DeviceStatus) SubtotalChangeAmount(decimal amount)
        {
            // <OptionPrinting>;<OptionDisplay>:<DiscAddV> — only valid when all sales are in one VAT class.
            return Request(CommandSubtotal, $"1;0:{Amount(amount)}");
        }

        public (decimal?, DeviceStatus) ReadSubtotal()
        {
            var (response, status) = Request(CommandSubtotal, "0;0");
            if (status.Ok && decimal.TryParse(response.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var subtotal))
            {
                return (subtotal, status);
            }
            return (null, status);
        }

        public override (string, DeviceStatus) AddPayment(decimal amount, PaymentType paymentType)
        {
            // <PaymentType[1]>;0;<Amount>;1
            return Request(CommandPayment, string.Join(";",
                GetPaymentTypeText(paymentType),
                "0",
                Amount(amount),
                "1"));
        }

        public (string, DeviceStatus) PayExactSumInCash()
        {
            return Request(CommandPayment, "0;0;\";1");
        }

        public override (string, DeviceStatus) CloseReceipt()
        {
            closeAttempted = true;
            return base.CloseReceipt();
        }

        public override (string, DeviceStatus) MoneyTransfer(TransferAmount transferAmount)
        {
            // <OperNum>;<OperPass[4]>;0;<Amount> — negative amount = paid out
            return Request(CommandNoFiscalRAorPOAmount, string.Join(";",
                OperatorId(transferAmount.Operator),
                OperatorPassword(transferAmount.Operator, transferAmount.OperatorPassword),
                "0",
                Amount(transferAmount.Amount)));
        }

        public override DeviceStatusWithCashAmount Cash(Credentials credentials)
        {
            var (response, status) = Request(CommandReadDailyAvailableAmounts, "0");
            var statusEx = new DeviceStatusWithCashAmount(status);
            var fields = response.Split(';');
            if (fields.Length < 2 || !decimal.TryParse(fields[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                statusEx.AddInfo("Error occured while reading cash amount");
                statusEx.AddError("E409", "Invalid format");
            }
            else
            {
                statusEx.Amount = amount;
            }
            return statusEx;
        }

        public override (string, DeviceStatus) OpenReversalReceipt(
            ReversalReason reason, string receiptNumber, DateTime receiptDateTime,
            string fiscalMemorySerialNumber, string uniqueSaleNumber, string operatorId, string operatorPassword)
        {
            var status = new DeviceStatus();
            status.AddError("E413", "Reversal receipts are not implemented for ro.zk.zfp");
            return (string.Empty, status);
        }

        public override (ReceiptInfo, DeviceStatus) PrintReversalReceipt(ReversalReceipt reversalReceipt)
        {
            var (_, status) = OpenReversalReceipt(default, string.Empty, default, string.Empty, string.Empty, string.Empty, string.Empty);
            return (new ReceiptInfo(), status);
        }

        // ---------------------------------------------------------------------------------
        // Validation and receipt flow
        // ---------------------------------------------------------------------------------

        public override DeviceStatus ValidateReceipt(Receipt receipt)
        {
            // The base validator enforces the Bulgarian USN format; check the Romanian rule instead.
            var usn = receipt.UniqueSaleNumber;
            receipt.UniqueSaleNumber = string.Empty;
            DeviceStatus status;
            try
            {
                status = base.ValidateReceipt(receipt);
            }
            finally
            {
                receipt.UniqueSaleNumber = usn;
            }
            if (status.Ok && !string.IsNullOrEmpty(usn) && !RoUniqueSaleNumberPattern.IsMatch(usn))
            {
                status.AddError("E405", "Invalid format of UniqueSaleNumber (1-64 chars: letters, digits, . _ : / -)");
            }
            return status;
        }

        protected UsnJournal Journal => usnJournal ??= new UsnJournal(
            string.IsNullOrEmpty(PrinterOption(OptionUsnJournalPath))
                ? UsnJournal.DefaultPathFor(Info.SerialNumber)
                : PrinterOption(OptionUsnJournalPath)!);

        public override (ReceiptInfo, DeviceStatus) PrintReceipt(Receipt receipt)
        {
            var usn = receipt.UniqueSaleNumber;
            var journalEnabled = !string.IsNullOrEmpty(usn)
                && !string.Equals(PrinterOption(OptionUsnJournal), "false", StringComparison.OrdinalIgnoreCase);
            if (!journalEnabled)
            {
                return PrintReceiptOnDevice(receipt);
            }

            var existing = Journal.Find(usn);
            if (existing != null)
            {
                var status = new DeviceStatus();
                if (existing.State == UsnJournal.EntryState.Printed)
                {
                    status.AddInfo($"Receipt with uniqueSaleNumber {usn} was already printed; returning the stored result, not printing again");
                    return (new ReceiptInfo
                    {
                        ReceiptNumber = existing.ReceiptNumber,
                        ReceiptDateTime = existing.ReceiptDateTime,
                        ReceiptAmount = existing.ReceiptAmount,
                        FiscalMemorySerialNumber = existing.FiscalMemorySerialNumber
                    }, status);
                }
                status.AddError("E409",
                    $"Receipt with uniqueSaleNumber {usn} was interrupted on {existing.CreatedAt:yyyy-MM-dd HH:mm:ss} " +
                    "and may already be printed. Check the last receipt on the device, then resend with a new uniqueSaleNumber.");
                return (new ReceiptInfo(), status);
            }

            Journal.MarkPending(usn);
            var (receiptInfo, deviceStatus) = PrintReceiptOnDevice(receipt);
            if (deviceStatus.Ok)
            {
                Journal.MarkPrinted(usn, receiptInfo);
            }
            else if (!closeAttempted)
            {
                Journal.Remove(usn);
            }
            else
            {
                deviceStatus.AddInfo($"Receipt with uniqueSaleNumber {usn} may be printed; it will not be printed again automatically");
            }
            return (receiptInfo, deviceStatus);
        }

        protected virtual (ReceiptInfo, DeviceStatus) PrintReceiptOnDevice(Receipt receipt)
        {
            closeAttempted = false;
            pendingItemUnits.Clear();
            if (receipt.Items != null)
            {
                foreach (var item in receipt.Items.Where(i => i.Type == ItemType.Sale))
                {
                    pendingItemUnits.Enqueue(string.IsNullOrWhiteSpace(item.Unit) ? DefaultUnit() : item.Unit!.Trim());
                }
            }

            try
            {
                // An unfinished receipt from an earlier failure would block the new one.
                AbortReceipt();

                var (_, status) = OpenReceipt(receipt.UniqueSaleNumber, receipt.Operator, receipt.OperatorPassword);
                if (!status.Ok)
                {
                    status.AddInfo("Error occured while opening new fiscal receipt");
                    AbortReceipt();
                    return (new ReceiptInfo(), status);
                }

                (ReceiptInfo, DeviceStatus) Fail(DeviceStatus s, string info)
                {
                    s.AddInfo(info);
                    if (!closeAttempted) AbortReceipt();
                    return (new ReceiptInfo(), s);
                }

                var itemNumber = 0;
                foreach (var item in receipt.Items ?? new List<Item>())
                {
                    itemNumber++;
                    try
                    {
                        status = item.Type switch
                        {
                            ItemType.Comment => AddComment(item.Text).Item2,
                            ItemType.Sale => AddItem(item.Department, item.Text, item.UnitPrice, item.TaxGroup,
                                item.Quantity, item.PriceModifierValue, item.PriceModifierType).Item2,
                            ItemType.SurchargeAmount => SubtotalChangeAmount(item.Amount).Item2,
                            ItemType.DiscountAmount => SubtotalChangeAmount(-item.Amount).Item2,
                            _ => new DeviceStatus(),
                        };
                    }
                    catch (StandardizedStatusMessageException e)
                    {
                        status = new DeviceStatus();
                        status.AddError(e.Code, e.Message);
                    }
                    if (!status.Ok) return Fail(status, $"Error occurred in Item {itemNumber}");
                }

                var (subtotal, subtotalStatus) = ReadSubtotal();
                if (!subtotalStatus.Ok) return Fail(subtotalStatus, "Error occurred while reading the subtotal");

                var payments = OrderPaymentsForDevice(receipt.Payments);
                if (payments.Count == 0)
                {
                    (_, status) = PayExactSumInCash();
                    if (!status.Ok) return Fail(status, "Error occurred while paying the receipt in cash");
                }
                else
                {
                    var paymentNumber = 0;
                    foreach (var payment in payments)
                    {
                        paymentNumber++;
                        try
                        {
                            (_, status) = AddPayment(payment.Amount, payment.PaymentType);
                        }
                        catch (StandardizedStatusMessageException e)
                        {
                            status = new DeviceStatus();
                            status.AddError(e.Code, e.Message);
                        }
                        if (!status.Ok) return Fail(status, $"Error occurred in Payment {paymentNumber}");
                    }
                }

                itemNumber = 0;
                foreach (var item in receipt.Items ?? new List<Item>())
                {
                    itemNumber++;
                    if (item.Type != ItemType.FooterComment) continue;
                    (_, status) = AddComment(item.Text);
                    if (!status.Ok) return Fail(status, $"Error occurred in Item {itemNumber}");
                }

                (_, status) = CloseReceipt();
                if (!status.Ok) return Fail(status, "Error occurred while closing the receipt");

                return ReadLastReceiptInfo(subtotal ?? 0m);
            }
            finally
            {
                pendingItemUnits.Clear();
            }
        }

        /// <summary>Non-cash payments first, cash last; change and zero amounts are not sent.</summary>
        protected static IList<Payment> OrderPaymentsForDevice(IEnumerable<Payment>? payments)
        {
            var list = (payments ?? Enumerable.Empty<Payment>())
                .Where(p => p.PaymentType != PaymentType.Change && p.Amount != 0m)
                .ToList();
            return list.Where(p => p.PaymentType != PaymentType.Cash)
                .Concat(list.Where(p => p.PaymentType == PaymentType.Cash))
                .ToList();
        }

        protected (ReceiptInfo, DeviceStatus) ReadLastReceiptInfo(decimal amount)
        {
            var info = new ReceiptInfo { ReceiptAmount = amount };

            // The receipt is closed at this point: read failures are reported as info, not as errors,
            // otherwise the client would retry and print the receipt a second time.
            var status = new DeviceStatus();

            var (numbers, numbersStatus) = Request(RoCommandReadLastAndTotalReceiptNum);
            var fields = numbers.Split(';');
            if (numbersStatus.Ok && fields.Length > 0 && fields[0].Trim().Length > 0)
            {
                info.ReceiptNumber = fields[0].Trim();
            }
            else
            {
                status.AddInfo($"Receipt printed, but its number could not be read ('{numbers}')");
            }

            var (dateTime, _) = GetDateTime();
            info.ReceiptDateTime = dateTime ?? DateTime.Now;

            var (fm, _) = GetFiscalMemorySerialNumber();
            info.FiscalMemorySerialNumber = string.IsNullOrEmpty(fm) ? Info.SerialNumber : fm;

            return (info, status);
        }

        public override DeviceStatusWithDateTime Reset(Credentials credentials)
        {
            AbortReceipt();
            return CheckStatus();
        }
    }
}
