namespace ErpNet.FP.Core.Drivers.RoDatecs
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using ErpNet.FP.Core.Configuration;
    using ErpNet.FP.Core.Drivers.BgDatecs;
    using Serilog;

    /// <summary>
    /// Datecs fiscal devices with Romanian firmware (DP-25, DP-25MX, DP-150MX, WP-50MX,
    /// WP-500MX, FP-700MX, FMP-350MX ...) speaking the Datecs "X" ISL dialect.
    /// <para>
    /// Same frame format as the Bulgarian X dialect (bg.dt.x.isl), but several commands differ.
    /// The differences below come from the Romanian DP-25 fix by ridvandev
    /// (github.com/ridvandev/ErpNet.FP, commit 2ed544b, upstream issue #159),
    /// re-implemented as a separate driver so the Bulgarian drivers keep working unchanged:
    /// </para>
    /// <list type="bullet">
    /// <item>48 Open receipt: no unique sale number field (Romania has no УНП); one extra trailing field.</item>
    /// <item>49 Sale: unit of measure is sent (mandatory on Romanian receipts), default "buc.".</item>
    /// <item>53 Total: {PaidMode}&lt;SEP&gt;{Amount}&lt;SEP&gt; without the Bulgarian {Type} field.</item>
    /// <item>76 Receipt status: the amount is in a different column.</item>
    /// <item>90 Device info: serial number may be longer than 8 chars, fiscal memory number may be missing.</item>
    /// <item>Default operator password is 0001 (Romanian factory default) instead of 0000.</item>
    /// </list>
    /// Every value that could not be confirmed on all Romanian firmwares can be overridden per
    /// device in appsettings.json → PrintersProperties → {serial} → PrinterOptions (see README-RO.md).
    /// </summary>
    public class RoDatecsXIslFiscalPrinter : BgDatecsXIslFiscalPrinter
    {
        // Printer option keys (appsettings.json → PrintersProperties → {serial} → PrinterOptions)
        public const string OptionItemUnit = "itemUnit";
        public const string OptionReceiptAmountField = "receiptAmountField";
        public const string OptionUsnJournal = "usnJournal";
        public const string OptionUsnJournalPath = "usnJournalPath";

        protected const string DefaultItemUnit = "buc.";
        protected const int DefaultReceiptAmountField = 7;   // ridvandev 2ed544b; Bulgarian X uses 4

        // The client's own sale identifier. Romania has no fiscal USN, so any reasonable id is accepted
        // and used only for idempotency in the bridge; it is never sent to the device.
        private static readonly Regex RoUniqueSaleNumberPattern =
            new Regex("^[A-Za-z0-9._:/-]{1,64}$", RegexOptions.Compiled);

        private readonly Queue<string> pendingItemUnits = new Queue<string>();
        private decimal? expectedReceiptAmount;
        private UsnJournal? usnJournal;
        private bool closeAttempted;

        public RoDatecsXIslFiscalPrinter(
            IChannel channel,
            ServiceOptions serviceOptions,
            IDictionary<string, string>? options = null)
        : base(channel, serviceOptions, options) { }

        public override IDictionary<string, string>? GetDefaultOptions()
        {
            return new Dictionary<string, string>
            {
                ["Operator.ID"] = "1",
                ["Operator.Password"] = "0001",

                ["Administrator.ID"] = "20",
                ["Administrator.Password"] = "9999"
            };
        }

        protected string? PrinterOption(string key)
        {
            return string.IsNullOrEmpty(Info.SerialNumber)
                ? null
                : ServiceOptions.GetPrinterOption(Info.SerialNumber, key);
        }

        protected bool UsnJournalEnabled =>
            !string.Equals(PrinterOption(OptionUsnJournal), "false", StringComparison.OrdinalIgnoreCase);

        protected UsnJournal Journal
        {
            get
            {
                if (usnJournal == null)
                {
                    var path = PrinterOption(OptionUsnJournalPath);
                    usnJournal = new UsnJournal(string.IsNullOrEmpty(path)
                        ? UsnJournal.DefaultPathFor(Info.SerialNumber)
                        : path);
                }
                return usnJournal;
            }
        }

        // ---------------------------------------------------------------------------------
        // Validation
        // ---------------------------------------------------------------------------------

        public override DeviceStatus ValidateReceipt(Receipt receipt)
        {
            // The Bulgarian validator enforces the УНП format (XX123456-XXXX-1234567).
            // Validate everything else with it, and check the USN with the Romanian rule.
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

        // ---------------------------------------------------------------------------------
        // Receipt flow
        // ---------------------------------------------------------------------------------

        public override (ReceiptInfo, DeviceStatus) PrintReceipt(Receipt receipt)
        {
            var usn = receipt.UniqueSaleNumber;
            if (string.IsNullOrEmpty(usn) || !UsnJournalEnabled)
            {
                return base.PrintReceipt(receipt);
            }

            var existing = Journal.Find(usn);
            if (existing != null)
            {
                var status = new DeviceStatus();
                if (existing.State == UsnJournal.EntryState.Printed)
                {
                    // Idempotent retry: the receipt is already fiscalized, return the same answer.
                    status.AddInfo($"Receipt with uniqueSaleNumber {usn} was already printed; returning the stored result, not printing again");
                    Log.Information($"{Info.SerialNumber}: duplicate USN {usn}, receipt {existing.ReceiptNumber} not reprinted");
                    return (new ReceiptInfo
                    {
                        ReceiptNumber = existing.ReceiptNumber,
                        ReceiptDateTime = existing.ReceiptDateTime,
                        ReceiptAmount = existing.ReceiptAmount,
                        FiscalMemorySerialNumber = existing.FiscalMemorySerialNumber
                    }, status);
                }

                // Pending: an earlier attempt never got a final answer from the device.
                // It may be in the fiscal memory already, so a human has to check the last receipt.
                status.AddError("E409",
                    $"Receipt with uniqueSaleNumber {usn} was interrupted on {existing.CreatedAt:yyyy-MM-dd HH:mm:ss} " +
                    "and may already be printed. Check the last receipt on the device, then resend with a new uniqueSaleNumber.");
                return (new ReceiptInfo(), status);
            }

            Journal.MarkPending(usn);
            closeAttempted = false;
            // An exception here (connection lost mid-receipt) leaves the entry pending on purpose.
            var (receiptInfo, deviceStatus) = base.PrintReceipt(receipt);

            if (deviceStatus.Ok)
            {
                Journal.MarkPrinted(usn, receiptInfo);
            }
            else if (!closeAttempted)
            {
                // Failed before the close command: the device aborted the receipt,
                // nothing reached the fiscal memory, the client may retry with the same USN.
                Journal.Remove(usn);
            }
            else
            {
                // The close command was sent but its answer is bad or missing: the receipt may
                // be fiscalized. Keep it pending so a retry does not print it a second time.
                deviceStatus.AddInfo($"Receipt with uniqueSaleNumber {usn} may be printed; it will not be printed again automatically");
            }
            return (receiptInfo, deviceStatus);
        }

        public override (ReceiptInfo, DeviceStatus) PrintReceiptBody(Receipt receipt)
        {
            pendingItemUnits.Clear();
            expectedReceiptAmount = null;
            if (receipt.Items != null)
            {
                foreach (var item in receipt.Items.Where(i => i.Type == ItemType.Sale))
                {
                    pendingItemUnits.Enqueue(string.IsNullOrWhiteSpace(item.Unit) ? DefaultUnit() : item.Unit!.Trim());
                }
                expectedReceiptAmount = ComputeReceiptTotal(receipt);
            }
            try
            {
                return base.PrintReceiptBody(receipt);
            }
            finally
            {
                pendingItemUnits.Clear();
                expectedReceiptAmount = null;
            }
        }

        protected string DefaultUnit()
        {
            var unit = PrinterOption(OptionItemUnit);
            return string.IsNullOrWhiteSpace(unit) ? DefaultItemUnit : unit!;
        }

        protected static decimal ComputeReceiptTotal(Receipt receipt)
        {
            decimal total = 0m;
            if (receipt.Items == null) return total;
            foreach (var item in receipt.Items)
            {
                switch (item.Type)
                {
                    case ItemType.Sale:
                        var quantity = Math.Round(item.Quantity == 0m ? 1m : item.Quantity, 3, MidpointRounding.AwayFromZero);
                        var price = Math.Round(quantity * Math.Round(item.UnitPrice, 2, MidpointRounding.AwayFromZero), 2, MidpointRounding.AwayFromZero);
                        var modifier = Math.Round(item.PriceModifierValue, 2, MidpointRounding.AwayFromZero);
                        price += item.PriceModifierType switch
                        {
                            PriceModifierType.DiscountAmount => -modifier,
                            PriceModifierType.SurchargeAmount => modifier,
                            PriceModifierType.DiscountPercent => -Math.Round(price * modifier / 100m, 2, MidpointRounding.AwayFromZero),
                            PriceModifierType.SurchargePercent => Math.Round(price * modifier / 100m, 2, MidpointRounding.AwayFromZero),
                            _ => 0m
                        };
                        total += price;
                        break;
                    case ItemType.SurchargeAmount:
                        total += Math.Round(item.Amount, 2, MidpointRounding.AwayFromZero);
                        break;
                    case ItemType.DiscountAmount:
                        total -= Math.Round(item.Amount, 2, MidpointRounding.AwayFromZero);
                        break;
                }
            }
            return total;
        }

        // ---------------------------------------------------------------------------------
        // Commands that differ from the Bulgarian X dialect
        // ---------------------------------------------------------------------------------

        public override (string, DeviceStatus) OpenReceipt(
            string uniqueSaleNumber,
            string operatorId,
            string operatorPassword)
        {
            // {OpCode}<SEP>{OpPwd}<SEP>{TillNmb}<SEP>{Invoice}<SEP>{...}<SEP>
            // The unique sale number is the client's id only, it is not sent to a Romanian device.
            var header = string.Join("\t",
                string.IsNullOrEmpty(operatorId)
                    ? Options.ValueOrDefault("Operator.ID", "1")
                    : operatorId,
                string.IsNullOrEmpty(operatorId)
                    ? Options.ValueOrDefault("Operator.Password", "0001").WithMaxLength(Info.OperatorPasswordMaxLength)
                    : operatorPassword,
                "1",
                "",
                "",
                "");
            return Request(CommandOpenFiscalReceipt, header);
        }

        public override (string, DeviceStatus) AddItem(
            int department,
            string itemText,
            decimal unitPrice,
            TaxGroup taxGroup,
            decimal quantity = 0m,
            decimal priceModifierValue = 0m,
            PriceModifierType priceModifierType = PriceModifierType.None,
            int ItemCode = 999)
        {
            var discountType = priceModifierType switch
            {
                PriceModifierType.None => "0",
                PriceModifierType.SurchargePercent => "1",
                PriceModifierType.DiscountPercent => "2",
                PriceModifierType.SurchargeAmount => "3",
                PriceModifierType.DiscountAmount => "4",
                _ => "0",
            };
            var unit = pendingItemUnits.Count > 0 ? pendingItemUnits.Dequeue() : DefaultUnit();

            // {PluName}<SEP>{TaxCd}<SEP>{Price}<SEP>{Quantity}<SEP>{DiscountType}<SEP>{DiscountValue}<SEP>{Department}<SEP>{Unit}<SEP>
            var itemData = string.Join("\t",
                itemText.WithMaxLength(Info.ItemTextMaxLength),
                GetTaxGroupText(taxGroup),
                unitPrice.ToString("F2", CultureInfo.InvariantCulture),
                quantity == 0m ? string.Empty : quantity.ToString("0.###", CultureInfo.InvariantCulture),
                discountType,
                // "0.00" with type 0 is what was verified on a Romanian DP-25 (ridvandev 2ed544b).
                priceModifierValue.ToString("F2", CultureInfo.InvariantCulture),
                department.ToString(CultureInfo.InvariantCulture),
                unit.WithMaxLength(6),
                "");
            return Request(CommandFiscalReceiptSale, itemData);
        }

        public override (string, DeviceStatus) CloseReceipt()
        {
            closeAttempted = true;
            return base.CloseReceipt();
        }

        public override (string, DeviceStatus) FullPayment()
        {
            // {PaidMode}<SEP>{Amount}<SEP> — empty amount pays the rest in cash.
            return Request(CommandFiscalReceiptTotal, "0\t\t");
        }

        public override (string, DeviceStatus) AddPayment(decimal amount, PaymentType paymentType)
        {
            if (paymentType == PaymentType.Card && DeviceInfo.SupportPaymentTerminal && DeviceInfo.UsePaymentTerminal)
            {
                // Pinpad flow (debit card with Type) is shared with the Bulgarian firmware.
                return base.AddPayment(amount, paymentType);
            }

            // {PaidMode}<SEP>{Amount}<SEP>
            var paymentData = string.Join("\t",
                GetPaymentTypeText(paymentType),
                amount.ToString("F2", CultureInfo.InvariantCulture),
                "");
            return Request(CommandFiscalReceiptTotal, paymentData);
        }

        public override (decimal?, DeviceStatus) GetReceiptAmount()
        {
            var (response, deviceStatus) = Request(CommandGetReceiptStatus);
            var fields = deviceStatus.Ok ? response.Split('\t') : Array.Empty<string>();

            var hasConfigured = int.TryParse(PrinterOption(OptionReceiptAmountField), out var configured);
            var candidates = (hasConfigured
                    ? new[] { configured, DefaultReceiptAmountField, 4 }
                    : new[] { DefaultReceiptAmountField, 4 })
                .Distinct()
                .Where(index => index > 0 && index < fields.Length)
                .ToList();

            // 1) A column whose value matches the total we sent is certainly the amount.
            foreach (var index in candidates)
            {
                if (TryParseAmount(fields[index], out var amount)
                    && (!expectedReceiptAmount.HasValue || amount == expectedReceiptAmount.Value))
                {
                    return (amount, deviceStatus);
                }
            }
            // 2) A column explicitly configured for this device is trusted as is.
            if (hasConfigured && configured > 0 && configured < fields.Length
                && TryParseAmount(fields[configured], out var configuredAmount))
            {
                return (configuredAmount, deviceStatus);
            }

            // The amount is informational only. Never abort a fiscal receipt because its
            // status line has an unknown layout: fall back to the total computed from the request.
            var fallbackStatus = new DeviceStatus();
            fallbackStatus.AddInfo($"Receipt amount not read from device (status: '{response}'), using computed total");
            Log.Warning($"{Info.SerialNumber}: cannot read receipt amount from '{response}', using computed total");
            return (expectedReceiptAmount ?? 0m, fallbackStatus);
        }

        protected static bool TryParseAmount(string text, out decimal amount)
        {
            amount = 0m;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            try
            {
                switch (text[0])
                {
                    case '+':
                        amount = decimal.Parse(text.Substring(1), CultureInfo.InvariantCulture) / 100m;
                        return true;
                    case '-':
                        amount = -decimal.Parse(text.Substring(1), CultureInfo.InvariantCulture) / 100m;
                        return true;
                    default:
                        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
                }
            }
            catch
            {
                return false;
            }
        }

        private static readonly string[] DateTimeFormats = {
            "dd-MM-yy HH:mm:ss", "dd-MM-yyyy HH:mm:ss", "dd.MM.yy HH:mm:ss", "dd.MM.yyyy HH:mm:ss",
            "dd-MM-yy HH:mm", "dd.MM.yy HH:mm", "dd/MM/yy HH:mm:ss", "dd/MM/yyyy HH:mm:ss"
        };

        public override (DateTime?, DeviceStatus) GetDateTime()
        {
            var (response, deviceStatus) = Request(CommandGetDateTime);
            if (!deviceStatus.Ok)
            {
                deviceStatus.AddInfo("Error occured while reading current date and time");
                return (null, deviceStatus);
            }
            var fields = response.Split('\t');
            var candidate = (fields.Length >= 2 ? fields[1] : fields[0]).Replace(" DST", "").Trim();
            if (DateTime.TryParseExact(candidate, DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dateTime))
            {
                return (dateTime, deviceStatus);
            }
            deviceStatus.AddInfo("Error occured while parsing current date and time");
            deviceStatus.AddError("E409", $"Wrong format of date and time: '{response}'");
            return (null, deviceStatus);
        }

        public override (string, DeviceStatus) GetFiscalMemorySerialNumber()
        {
            // Romanian devices may return no fiscal memory number. That is not an error:
            // fall back to the value parsed at connect time, then to the device serial number.
            var (rawDeviceInfo, deviceStatus) = GetRawDeviceInfo();
            var parsed = RoDatecsXIslFiscalPrinterDriver.TryParseRawDeviceInfo(rawDeviceInfo);
            var fm = parsed?.FiscalMemorySerialNumber;
            if (string.IsNullOrWhiteSpace(fm)) fm = Info.FiscalMemorySerialNumber;
            if (string.IsNullOrWhiteSpace(fm)) fm = Info.SerialNumber;
            return (fm ?? string.Empty, deviceStatus.Ok ? deviceStatus : new DeviceStatus());
        }
    }
}
