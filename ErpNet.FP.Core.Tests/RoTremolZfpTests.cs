namespace ErpNet.FP.Core.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using ErpNet.FP.Core;
    using ErpNet.FP.Core.Configuration;
    using ErpNet.FP.Core.Drivers;
    using ErpNet.FP.Core.Drivers.RoTremol;
    using Xunit;

    /// <summary>
    /// Romanian Tremol whose frames are answered in memory. Answers are encoded as real ZFP frames
    /// (STX LEN NBL CMD DATA CS CS ETX / ACK NBL STE STE CS CS ETX), so the frame parser is exercised too.
    /// </summary>
    internal class FakeRoTremol : RoTremolZfpFiscalPrinter
    {
        public const string Serial = "DP30012102";
        public readonly List<(byte Command, string Data)> Sent = new();
        public Func<byte, string, (string? Data, string Ack)> Responder;
        private static readonly Encoding Cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

        public FakeRoTremol(ServiceOptions options) : base(new NullChannel(), options, null)
        {
            Info = new RoTremolZfpFiscalPrinterDriver().ParseDeviceInfo(Serial, "Ver 1.00,TRM25-RO", "", true);
            Info.SupportedPaymentTypes = GetSupportedPaymentTypes();
            Responder = Default;
        }

        public static (string?, string) Default(byte cmd, string data) => cmd switch
        {
            0x33 => ("0.30", "00"),                       // subtotal
            0x60 => (Serial, "00"),
            0x21 => ("Ver 1.00,TRM25-RO", "00"),
            0x61 => ("RO12345678     ;          ;1", "00"),
            0x68 => ("01-11-2026 21:05", "00"),
            0x71 => ("0042;0000123", "00"),
            0x6E => ("0;125.50;0.00;0.00;0.00;0.00;0.00;0.00;0.00;0.00;0.00", "00"),
            0x64 => ("NUMERAR   ;CARD      ;CREDIT    ;TICHETE   ;          ;          ;          ;          ;          ;VALUTA    ;0.00000", "00"),
            _ => (null, "00"),                            // ACK
        };

        protected override byte[]? RawRequest(byte command, byte[]? data)
        {
            var text = data == null ? string.Empty : Cp1252.GetString(data);
            Sent.Add((command, text));
            var (answer, ack) = Responder(command, text);
            return answer == null ? AckFrame(ack) : DataFrame(command, Cp1252.GetBytes(answer));
        }

        private byte[] DataFrame(byte command, byte[] payload)
        {
            var body = new List<byte> { (byte)(0x20 + 3 + payload.Length), 0x21, command };
            body.AddRange(payload);
            var frame = new List<byte> { 0x02 };
            frame.AddRange(body);
            frame.AddRange(ComputeCS(body.ToArray()));
            frame.Add(0x0A);
            return frame.ToArray();
        }

        private byte[] AckFrame(string ack)
        {
            var body = new List<byte> { 0x21, (byte)ack[0], (byte)ack[1] };
            var frame = new List<byte> { 0x06 };
            frame.AddRange(body);
            frame.AddRange(ComputeCS(body.ToArray()));
            frame.Add(0x0A);
            return frame.ToArray();
        }

        public List<string> DataOf(byte command) => Sent.Where(s => s.Command == command).Select(s => s.Data).ToList();

        public (byte[]?, DeviceStatus) ParseRaw(byte[] raw) => ParseResponseAsByteArray(raw);
    }

    public class RoTremolZfpTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "erpnet-rotremol-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private ServiceOptions Options()
        {
            var options = new ServiceOptions();
            options.PrintersProperties[FakeRoTremol.Serial] = new PrinterProperties
            {
                PrinterOptions = new Dictionary<string, string>
                {
                    [RoTremolZfpFiscalPrinter.OptionUsnJournalPath] = Path.Combine(dir, "j.json")
                }
            };
            return options;
        }

        private static Receipt Sample(string usn = "", IList<Payment>? payments = null) => new Receipt
        {
            UniqueSaleNumber = usn,
            Items = new List<Item>
            {
                new Item { Text = "Pâine albă", TaxGroup = TaxGroup.TaxGroup1, UnitPrice = 0.10m, Quantity = 1m },
                new Item { Type = ItemType.Comment, Text = "Auto B-123-ABC" },
                new Item { Text = "Ulei", TaxGroup = TaxGroup.TaxGroup2, UnitPrice = 0.40m, Quantity = 0.5m, Unit = "l" },
                new Item { Type = ItemType.FooterComment, Text = "Mulțumim!" },
            },
            Payments = payments,
        };

        [Fact]
        public void RealDeviceFrames_FromTheLog_AreParsed()
        {
            // Captured from the Tremol S25 RO on COM9 (debug log 2026-11-01 20:46:29).
            var p = new FakeRoTremol(Options());
            var serialFrame = new byte[] { 0x02, 0x2D, 0x5C, 0x60, 0x44, 0x50, 0x33, 0x30, 0x30, 0x31, 0x32, 0x31, 0x30, 0x32, 0x30, 0x36, 0x0A };
            var (serial, s1) = p.ParseRaw(serialFrame);
            Assert.True(s1.Ok);
            Assert.Equal("DP30012102", Encoding.ASCII.GetString(serial!));

            var versionFrame = new byte[] { 0x02, 0x34, 0x5D, 0x21 }
                .Concat(Encoding.ASCII.GetBytes("Ver 1.00,TRM25-RO"))
                .Concat(new byte[] { 0x36, 0x36, 0x0A }).ToArray();
            var (version, s2) = p.ParseRaw(versionFrame);
            Assert.True(s2.Ok);
            Assert.Equal("Ver 1.00,TRM25-RO", Encoding.ASCII.GetString(version!));
        }

        [Fact]
        public void Detection_AcceptsRomanian_RejectsBulgarian()
        {
            Assert.True(RoTremolZfpFiscalPrinterDriver.LooksRomanian("Ver 1.00,TRM25-RO"));
            Assert.False(RoTremolZfpFiscalPrinterDriver.LooksRomanian("1;784;04-02-2019 08:00;TREMOL M20; Ver. 1.01 TRA20 C.S. 2541"));
            Assert.False(RoTremolZfpFiscalPrinterDriver.LooksRomanian(""));
            var info = new RoTremolZfpFiscalPrinterDriver().ParseDeviceInfo("DP30012102", "Ver 1.00,TRM25-RO", "", true);
            Assert.Equal("TRM25-RO", info.Model);
            Assert.Equal("Ver 1.00", info.FirmwareVersion);
            Assert.Equal("DP30012102", info.FiscalMemorySerialNumber);
            Assert.Throws<InvalidDeviceInfoException>(() =>
                new RoTremolZfpFiscalPrinterDriver().ParseDeviceInfo("ZK126720;50163145", "1;784;x;TREMOL M20;y", "", true));
        }

        [Fact]
        public void Receipt_UsesRomanianCommandFormats()
        {
            var p = new FakeRoTremol(Options());
            var (info, status) = p.PrintReceipt(Sample());

            Assert.True(status.Ok, string.Join("; ", status.Messages.Select(m => m.Code + " " + m.Text)));
            Assert.Equal(new[] { "1;0   ;0;0;2" }, p.DataOf(0x30));

            var sales = p.DataOf(0x31);
            Assert.Equal(2, sales.Count);
            // name[30] + '`' + unit[3] + 2 spaces = 36, diacritics transliterated
            Assert.Equal("Paine alba".PadRight(30) + "`buc  ;A;0.10*1", sales[0]);
            Assert.Equal("Ulei".PadRight(30) + "`l    ;B;0.40*0.5", sales[1]);
            Assert.Equal(36, sales[0].Split(';')[0].Length);

            Assert.Equal(new[] { "Auto B-123-ABC", "Multumim!" }, p.DataOf(0x37));
            Assert.Equal(new[] { "0;0;\";1" }, p.DataOf(0x35));          // pay exact sum in cash
            Assert.Single(p.DataOf(0x38));

            Assert.Equal("0042", info.ReceiptNumber);
            Assert.Equal(0.30m, info.ReceiptAmount);
            Assert.Equal(new DateTime(2026, 11, 1, 21, 5, 0), info.ReceiptDateTime);
            Assert.Equal(FakeRoTremol.Serial, info.FiscalMemorySerialNumber);
        }

        [Fact]
        public void Payments_CardBeforeCash_ChangeSkipped()
        {
            var p = new FakeRoTremol(Options());
            var (_, status) = p.PrintReceipt(Sample(payments: new List<Payment>
            {
                new Payment { PaymentType = PaymentType.Cash, Amount = 1m },
                new Payment { PaymentType = PaymentType.Card, Amount = 0.10m },
                new Payment { PaymentType = PaymentType.Change, Amount = -0.80m },
            }));
            Assert.True(status.Ok);
            Assert.Equal(new[] { "1;0;0.10;1", "0;0;1.00;1" }, p.DataOf(0x35));
        }

        [Fact]
        public void Discounts_UseSignedPercentAndValue()
        {
            var p = new FakeRoTremol(Options());
            p.PrintReceipt(new Receipt
            {
                Items = new List<Item>
                {
                    new Item { Text = "A", TaxGroup = TaxGroup.TaxGroup1, UnitPrice = 1m, PriceModifierType = PriceModifierType.DiscountPercent, PriceModifierValue = 10m },
                    new Item { Text = "B", TaxGroup = TaxGroup.TaxGroup1, UnitPrice = 1m, PriceModifierType = PriceModifierType.SurchargeAmount, PriceModifierValue = 0.5m },
                    new Item { Type = ItemType.DiscountAmount, Amount = 0.2m },
                }
            });
            var sales = p.DataOf(0x31);
            Assert.EndsWith(";A;1.00,-10.00", sales[0]);
            Assert.EndsWith(";A;1.00:0.50", sales[1]);
            Assert.Contains("1;0:-0.20", p.DataOf(0x33));
        }

        [Fact]
        public void DeviceError_AbortsReceipt_AndReportsCode()
        {
            var p = new FakeRoTremol(Options());
            p.Responder = (cmd, data) => cmd == 0x31 ? (null, "34") : FakeRoTremol.Default(cmd, data);   // syntax error
            var (_, status) = p.PrintReceipt(Sample());
            Assert.False(status.Ok);
            Assert.Contains(status.Messages, m => m.Code == "E401");
            Assert.Empty(p.DataOf(0x38));
            Assert.True(p.DataOf(0x39).Count >= 2);   // abort before open + abort after failure
        }

        [Fact]
        public void UnknownRomanianCommandError_DoesNotCrash()
        {
            var p = new FakeRoTremol(Options());
            p.Responder = (cmd, data) => cmd == 0x30 ? (null, "3<") : FakeRoTremol.Default(cmd, data);   // server comms active
            var (_, status) = p.PrintReceipt(Sample());
            Assert.False(status.Ok);
            Assert.Contains(status.Messages, m => m.Text.Contains("server communications"));
        }

        [Fact]
        public void SameUsn_IsPrintedOnce()
        {
            var options = Options();
            var first = new FakeRoTremol(options);
            Assert.True(first.PrintReceipt(Sample("POS-1")).Item2.Ok);
            var second = new FakeRoTremol(options);
            var (info, status) = second.PrintReceipt(Sample("POS-1"));
            Assert.True(status.Ok);
            Assert.Equal("0042", info.ReceiptNumber);
            Assert.Empty(second.Sent);
        }

        [Fact]
        public void CardPayment_NumberDetectedFromDeviceNames()
        {
            var p = new FakeRoTremol(Options());
            Assert.Equal("1", p.DetectCardPaymentNumber());
            p.Responder = (cmd, data) => cmd == 0x64
                ? ("NUMERAR   ;CREDIT    ;CARD      ;x;x;x;x;x;x;x;0", "00")
                : FakeRoTremol.Default(cmd, data);
            Assert.Equal("2", p.DetectCardPaymentNumber());
        }

        [Fact]
        public void CashInDrawer_AndMoneyTransfer()
        {
            var p = new FakeRoTremol(Options());
            var cash = p.Cash(new Credentials());
            Assert.True(cash.Ok);
            Assert.Equal(125.50m, cash.Amount);

            p.PrintMoneyWithdraw(new TransferAmount { Amount = 10m });
            Assert.Equal(new[] { "1;0   ;0;-10.00" }, p.DataOf(0x3B));
        }

        [Fact]
        public void TaxGroups_MapToLatinLetters()
        {
            var p = new FakeRoTremol(Options());
            Assert.Equal("A", p.GetTaxGroupText(TaxGroup.TaxGroup1));
            Assert.Equal("E", p.GetTaxGroupText(TaxGroup.TaxGroup5));
            Assert.Equal("F", p.GetTaxGroupText(TaxGroup.TaxGroup6));
            Assert.Throws<StandardizedStatusMessageException>(() => p.GetTaxGroupText(TaxGroup.TaxGroup7));
        }
    }
}
