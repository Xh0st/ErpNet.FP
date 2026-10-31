namespace ErpNet.FP.Core.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using ErpNet.FP.Core;
    using ErpNet.FP.Core.Configuration;
    using ErpNet.FP.Core.Drivers;
    using ErpNet.FP.Core.Drivers.RoDatecs;
    using Xunit;

    /// <summary>Channel that must never be used: the fake printer answers at frame level.</summary>
    internal class NullChannel : IChannel
    {
        public string Descriptor => "test";
        public byte[] Read() => throw new InvalidOperationException("not used");
        public void Write(byte[] data) => throw new InvalidOperationException("not used");
    }

    /// <summary>
    /// Romanian Datecs printer whose frames are intercepted: every command is recorded and
    /// answered by <see cref="Responder"/>, so the exact protocol payloads can be asserted.
    /// </summary>
    internal class FakeRoPrinter : RoDatecsXIslFiscalPrinter
    {
        public const string Serial = "DA4600123456";
        public readonly List<(byte Command, string Data)> Sent = new();
        public Func<byte, string, string> Responder;
        public string ReceiptStatusAnswer = "0\t1\t15\t1\t0\t0\t0\t25.00\t25.00\t";

        public FakeRoPrinter(ServiceOptions options) : base(new NullChannel(), options, null)
        {
            Info = new DeviceInfo
            {
                SerialNumber = Serial,
                FiscalMemorySerialNumber = Serial,
                Model = "DP-25",
                ItemTextMaxLength = 72,
                CommentTextMaxLength = 40,
                OperatorPasswordMaxLength = 8,
            };
            Info.SupportedPaymentTypes = GetSupportedPaymentTypes();
            Responder = DefaultResponder;
        }

        private string DefaultResponder(byte command, string data) => command switch
        {
            0x3c => "0\t",                                   // abort
            0x30 => "0\t15\t",                               // open receipt
            0x31 => "0\t15\t",                               // sale
            0x33 => "0\t15\t25.00\t",                        // subtotal
            0x35 => "0\tR\t0.00\t",                          // payment
            0x36 => "0\t",                                   // comment
            0x5a => $"DP-25,263453 08Nov18 1047,F0C3,00000000,{Serial}",  // device info, no FM number
            0x3e => "0\t31-10-26 23:00:00\t",                // date/time
            0x4c => ReceiptStatusAnswer,                     // receipt status
            0x38 => "0\t15\t",                               // close
            _ => "0\t",
        };

        protected override byte[]? RawRequest(byte command, byte[]? data)
        {
            var text = data == null ? string.Empty : PrinterEncoding.GetString(data);
            Sent.Add((command, text));
            return PrinterEncoding.GetBytes(Responder(command, text));
        }

        protected override (string, DeviceStatus) ParseResponse(byte[]? rawResponse)
        {
            return (PrinterEncoding.GetString(rawResponse ?? Array.Empty<byte>()), new DeviceStatus());
        }

        public List<string> DataOf(byte command) => Sent.Where(s => s.Command == command).Select(s => s.Data).ToList();
    }

    public class RoDatecsXIslTests : IDisposable
    {
        private readonly string journalDir = Path.Combine(Path.GetTempPath(), "erpnet-ro-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(journalDir, true); } catch { }
        }

        private ServiceOptions Options(Dictionary<string, string>? extra = null)
        {
            var printerOptions = new Dictionary<string, string>
            {
                [RoDatecsXIslFiscalPrinter.OptionUsnJournalPath] = Path.Combine(journalDir, "journal.json")
            };
            if (extra != null) foreach (var kv in extra) printerOptions[kv.Key] = kv.Value;
            var options = new ServiceOptions();
            options.PrintersProperties[FakeRoPrinter.Serial] = new PrinterProperties { PrinterOptions = printerOptions };
            return options;
        }

        private static Receipt SampleReceipt(string usn = "", IList<Payment>? payments = null) => new Receipt
        {
            UniqueSaleNumber = usn,
            Items = new List<Item>
            {
                new Item { Text = "Paine", TaxGroup = TaxGroup.TaxGroup1, UnitPrice = 12.50m, Quantity = 1m },
                new Item { Text = "Mere", TaxGroup = TaxGroup.TaxGroup2, UnitPrice = 12.50m, Quantity = 1m, Unit = "kg" },
            },
            Payments = payments,
        };

        [Fact]
        public void OpenReceipt_UsesRomanianHeader_WithoutUniqueSaleNumber()
        {
            var printer = new FakeRoPrinter(Options());
            var (_, status) = printer.PrintReceipt(SampleReceipt(usn: "POS1-000001"));

            Assert.True(status.Ok, string.Join("; ", status.Messages.Select(m => m.Text)));
            Assert.Equal(new[] { "1\t0001\t1\t\t\t" }, printer.DataOf(0x30));
        }

        [Fact]
        public void Sale_SendsUnitOfMeasure_DefaultAndPerItem()
        {
            var printer = new FakeRoPrinter(Options());
            printer.PrintReceipt(SampleReceipt());

            Assert.Equal(new[]
            {
                "Paine\t1\t12.50\t1\t0\t0.00\t0\tbuc.\t",
                "Mere\t2\t12.50\t1\t0\t0.00\t0\tkg\t",
            }, printer.DataOf(0x31));
        }

        [Fact]
        public void Sale_DefaultUnit_CanBeConfiguredPerDevice()
        {
            var printer = new FakeRoPrinter(Options(new() { [RoDatecsXIslFiscalPrinter.OptionItemUnit] = "BUC" }));
            printer.PrintReceipt(SampleReceipt());

            Assert.EndsWith("\tBUC\t", printer.DataOf(0x31)[0]);
        }

        [Fact]
        public void NoPayments_PaysFullInCash_WithRomanianTotalFormat()
        {
            var printer = new FakeRoPrinter(Options());
            printer.PrintReceipt(SampleReceipt());

            Assert.Equal(new[] { "0\t\t" }, printer.DataOf(0x35));
        }

        [Fact]
        public void MixedPayments_CardBeforeCash_ChangeAndZeroSkipped()
        {
            // Issue #97: cash with change sent first closes the payment, the card is then rejected.
            var printer = new FakeRoPrinter(Options());
            var payments = new List<Payment>
            {
                new Payment { PaymentType = PaymentType.Cash, Amount = 20m },
                new Payment { PaymentType = PaymentType.Check, Amount = 0m },
                new Payment { PaymentType = PaymentType.Card, Amount = 10m },
                new Payment { PaymentType = PaymentType.Change, Amount = -5m },
            };
            var (_, status) = printer.PrintReceipt(SampleReceipt(payments: payments));

            Assert.True(status.Ok);
            Assert.Equal(new[] { "1\t10.00\t", "0\t20.00\t" }, printer.DataOf(0x35));
        }

        [Fact]
        public void ReceiptInfo_IsFilled_WhenFiscalMemoryNumberIsMissing()
        {
            var printer = new FakeRoPrinter(Options());
            var (info, status) = printer.PrintReceipt(SampleReceipt());

            Assert.True(status.Ok);
            Assert.Equal("15", info.ReceiptNumber);
            Assert.Equal(25.00m, info.ReceiptAmount);
            Assert.Equal(FakeRoPrinter.Serial, info.FiscalMemorySerialNumber);
            Assert.Equal(new DateTime(2026, 10, 31, 23, 0, 0), info.ReceiptDateTime);
        }

        [Fact]
        public void ReceiptAmount_UnknownStatusLayout_FallsBackToComputedTotal_AndStillCloses()
        {
            var printer = new FakeRoPrinter(Options()) { ReceiptStatusAnswer = "0\t1\t" };
            var (info, status) = printer.PrintReceipt(SampleReceipt());

            Assert.True(status.Ok);
            Assert.Equal(25.00m, info.ReceiptAmount);
            Assert.Single(printer.DataOf(0x38));
        }

        [Fact]
        public void ReceiptAmount_BulgarianColumn_IsAcceptedWhenItMatches()
        {
            var printer = new FakeRoPrinter(Options()) { ReceiptStatusAnswer = "0\t1\t15\t2\t25.00\t25.00\t" };
            var (info, status) = printer.PrintReceipt(SampleReceipt());

            Assert.True(status.Ok);
            Assert.Equal(25.00m, info.ReceiptAmount);
        }

        [Fact]
        public void SameUsn_IsNotPrintedTwice()
        {
            var options = Options();
            var first = new FakeRoPrinter(options);
            var (info1, status1) = first.PrintReceipt(SampleReceipt(usn: "POS1-000002"));
            Assert.True(status1.Ok);

            // A new printer instance (e.g. after a service restart) reads the journal from disk.
            var second = new FakeRoPrinter(options);
            var (info2, status2) = second.PrintReceipt(SampleReceipt(usn: "POS1-000002"));

            Assert.True(status2.Ok);
            Assert.Empty(second.DataOf(0x30));
            Assert.Empty(second.DataOf(0x31));
            Assert.Equal(info1.ReceiptNumber, info2.ReceiptNumber);
            Assert.Equal(info1.ReceiptAmount, info2.ReceiptAmount);
        }

        [Fact]
        public void FailedReceipt_CanBeRetriedWithSameUsn()
        {
            var options = Options();
            var failing = new FakeRoPrinter(options);
            var normal = failing.Responder;
            failing.Responder = (cmd, data) => cmd == 0x30 ? throw new InvalidResponseException("paper out") : normal(cmd, data);
            var (_, status1) = failing.PrintReceipt(SampleReceipt(usn: "POS1-000003"));
            Assert.False(status1.Ok);

            var retry = new FakeRoPrinter(options);
            var (info, status2) = retry.PrintReceipt(SampleReceipt(usn: "POS1-000003"));
            Assert.True(status2.Ok);
            Assert.Equal("15", info.ReceiptNumber);
            Assert.Single(retry.DataOf(0x30));
        }

        [Fact]
        public void LostCloseAnswer_KeepsUsnPending_SoRetryDoesNotReprint()
        {
            var options = Options();
            var printer = new FakeRoPrinter(options);
            var normal = printer.Responder;
            printer.Responder = (cmd, data) => cmd == 0x38 ? throw new InvalidResponseException("no response") : normal(cmd, data);
            var (_, status1) = printer.PrintReceipt(SampleReceipt(usn: "POS1-000006"));
            Assert.False(status1.Ok);

            var retry = new FakeRoPrinter(options);
            var (_, status2) = retry.PrintReceipt(SampleReceipt(usn: "POS1-000006"));
            Assert.False(status2.Ok);
            Assert.Contains(status2.Messages, m => m.Code == "E409");
            Assert.Empty(retry.Sent);
        }

        [Fact]
        public void InterruptedReceipt_IsNotReprintedAutomatically()
        {
            var options = Options();
            var journal = new UsnJournal(Path.Combine(journalDir, "journal.json"));
            journal.MarkPending("POS1-000004");   // as if the bridge crashed mid-print

            var printer = new FakeRoPrinter(options);
            var (_, status) = printer.PrintReceipt(SampleReceipt(usn: "POS1-000004"));

            Assert.False(status.Ok);
            Assert.Contains(status.Messages, m => m.Code == "E409");
            Assert.Empty(printer.Sent);
        }

        [Fact]
        public void Validation_AcceptsClientUsn_RejectsGarbage()
        {
            var printer = new FakeRoPrinter(Options());
            Assert.True(printer.ValidateReceipt(SampleReceipt(usn: "POS1-000005")).Ok);
            Assert.True(printer.ValidateReceipt(SampleReceipt(usn: "")).Ok);
            Assert.False(printer.ValidateReceipt(SampleReceipt(usn: "bad usn with spaces")).Ok);
        }

        [Theory]
        [InlineData("DP-25,263453 08Nov18 1047,F0C3,00000000,DA4600123456", "DP-25", "DA4600123456", "")]
        [InlineData("DP-25MX,1.00RO 01Jan24 1200,ABCD,00000000,DB4800000123,", "DP-25MX", "DB4800000123", "")]
        [InlineData("FP-700MX,1.00RO 01Jan24 1200,ABCD,00000000,DE4900000001,50001234", "FP-700MX", "DE4900000001", "50001234")]
        [InlineData("0\tDP-150MX\t1.00RO\t01Jan24\t1200\tABCD\t00000000\tDC4700000007\t", "DP-150MX", "DC4700000007", "")]
        public void DeviceInfo_RomanianLayouts_AreParsed(string raw, string model, string serial, string fm)
        {
            var info = RoDatecsXIslFiscalPrinterDriver.TryParseRawDeviceInfo(raw);
            Assert.NotNull(info);
            Assert.Equal(model, info!.Model);
            Assert.Equal(serial, info.SerialNumber);
            Assert.Equal(fm, info.FiscalMemorySerialNumber);
            Assert.False(RoDatecsXIslFiscalPrinterDriver.LooksBulgarian(info));
        }

        [Fact]
        public void DeviceInfo_BulgarianAnswer_IsRecognised()
        {
            var info = RoDatecsXIslFiscalPrinterDriver.TryParseRawDeviceInfo(
                "DP-25X,263453 08Nov18 1047,F0C3,00000000,DT123456,02123456");
            Assert.NotNull(info);
            Assert.True(RoDatecsXIslFiscalPrinterDriver.LooksBulgarian(info!));
        }

        [Fact]
        public void DeviceInfo_Garbage_IsRejected()
        {
            Assert.Null(RoDatecsXIslFiscalPrinterDriver.TryParseRawDeviceInfo(""));
            Assert.Null(RoDatecsXIslFiscalPrinterDriver.TryParseRawDeviceInfo("0\t"));
            Assert.Null(RoDatecsXIslFiscalPrinterDriver.TryParseRawDeviceInfo("a,b"));
        }
    }
}
