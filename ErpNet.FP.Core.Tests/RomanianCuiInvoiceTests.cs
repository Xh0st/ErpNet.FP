namespace ErpNet.FP.Core.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using ErpNet.FP.Core;
    using ErpNet.FP.Core.Configuration;
    using Xunit;

    public class RomanianCuiInvoiceTests
    {
        // ---- CUI check digit --------------------------------------------------------------

        [Theory]
        [InlineData("14399840", true)]    // the Sincrona sample buyer
        [InlineData("RO14399840", true)]  // same, as a VAT payer
        [InlineData("ro14399840", true)]
        [InlineData("13548146", true)]
        [InlineData("99908", true)]
        [InlineData("14399841", false)]   // wrong control digit
        [InlineData("1439984", false)]    // the base without the control digit
        [InlineData("RO 14399840", true)] // spaces around the prefix
        [InlineData("4", false)]          // too short (1 digit)
        [InlineData("", false)]
        [InlineData("12345678901", false)] // too long (11 digits)
        [InlineData("RO1234x", false)]    // non-digits
        public void Cui_CheckDigit(string input, bool expected)
        {
            Assert.Equal(expected, RomanianTaxId.IsValid(input));
        }

        [Fact]
        public void CuiDigits_StripsRoPrefix()
        {
            Assert.Equal("14399840", RomanianInvoice.CuiDigits("RO14399840"));
            Assert.Equal("14399840", RomanianInvoice.CuiDigits("14399840"));
        }

        // ---- Recipient validation (country-level) -----------------------------------------

        private static Invoice SampleInvoice(Recipient? recipient, string number = "")
        {
            return new Invoice
            {
                UniqueSaleNumber = "pa-123",
                Items = new List<Item>
                {
                    new Item { Text = "Manopera", Quantity = 1, Unit = "buc", UnitPrice = 750.25m, TaxGroup = TaxGroup.TaxGroup1 },
                    new Item { Text = "Kit distributie", Quantity = 1, Unit = "buc", UnitPrice = 628.69m, TaxGroup = TaxGroup.TaxGroup1 },
                },
                Payments = new List<Payment> { new Payment { Amount = 1378.94m, PaymentType = PaymentType.Card } },
                Number = number,
                Recipient = recipient,
            };
        }

        private static Recipient ValidRecipient() => new Recipient
        {
            Name = "TRANSPORT LOGISTIC SRL",
            Identifier = "14399840",
            IdentifierType = IdentifierType.TaxNumber,
            VatNumber = "RO14399840",
            Address = "Splaiul Unirii 165, Bucuresti",
            City = "Bucuresti",
        };

        private static List<StatusMessage> Validate(Invoice invoice)
        {
            var status = new DeviceStatus();
            RomanianInvoice.ValidateRecipient(status, invoice, NumberAssignment.DeviceAssigned);
            return status.Messages.ToList();
        }

        private static bool Has(IEnumerable<StatusMessage> msgs, string code)
            => msgs.Any(m => m.Code == code);

        [Fact]
        public void ValidRecipient_PassesForTaxNumberAndLegalRegistration()
        {
            Assert.Empty(Validate(SampleInvoice(ValidRecipient())));

            var legal = ValidRecipient();
            legal.IdentifierType = IdentifierType.LegalRegistration;
            Assert.Empty(Validate(SampleInvoice(legal)));
        }

        [Fact]
        public void MissingRecipient_Name_Identifier_AreE405()
        {
            Assert.Contains("E405", Validate(SampleInvoice(null)).Select(m => m.Code));

            var noName = ValidRecipient(); noName.Name = "";
            Assert.True(Has(Validate(SampleInvoice(noName)), "E405"));

            var noId = ValidRecipient(); noId.Identifier = "";
            Assert.True(Has(Validate(SampleInvoice(noId)), "E405"));

            var unspecified = ValidRecipient(); unspecified.IdentifierType = IdentifierType.Unspecified;
            Assert.True(Has(Validate(SampleInvoice(unspecified)), "E405"));
        }

        [Fact]
        public void BadCheckDigit_IsE405()
        {
            var bad = ValidRecipient(); bad.Identifier = "14399841";
            var msgs = Validate(SampleInvoice(bad));
            Assert.True(Has(msgs, "E405"));
            Assert.False(Has(msgs, "E412"));
        }

        [Fact]
        public void RoPrefix_OnIdentifier_IsAccepted()
        {
            var r = ValidRecipient(); r.Identifier = "RO14399840";
            Assert.Empty(Validate(SampleInvoice(r)));
        }

        [Fact]
        public void NationalId_And_ForeignerId_AreE412()
        {
            var cnp = ValidRecipient(); cnp.IdentifierType = IdentifierType.NationalId; cnp.Identifier = "1900101410011";
            Assert.True(Has(Validate(SampleInvoice(cnp)), "E412"));

            var foreign = ValidRecipient(); foreign.IdentifierType = IdentifierType.ForeignerId; foreign.Identifier = "DE811569869";
            Assert.True(Has(Validate(SampleInvoice(foreign)), "E412"));
        }

        [Fact]
        public void SuppliedNumber_OnDeviceAssigned_IsE412()
        {
            Assert.True(Has(Validate(SampleInvoice(ValidRecipient(), number: "9000000007")), "E412"));
        }

        // ---- Driver-level ValidateInvoice (both RO drivers) -------------------------------

        [Fact]
        public void RoDatecs_ValidateInvoice_AcceptsValid_RejectsBadCui()
        {
            var printer = new FakeRoPrinter(new ServiceOptions());
            Assert.True(printer.ValidateInvoice(SampleInvoice(ValidRecipient())).Ok);

            var bad = ValidRecipient(); bad.Identifier = "14399841";
            Assert.False(printer.ValidateInvoice(SampleInvoice(bad)).Ok);
        }

        [Fact]
        public void RoTremol_Invoice_NotSupportedYet_IsE413()
        {
            // Tremol ZFP RO invoice command is not confirmed from an official doc yet, so SupportsInvoice
            // is false and /invoice returns E413. (The recipient rules themselves are covered above.)
            var printer = new FakeRoTremol(new ServiceOptions());
            var status = printer.ValidateInvoice(SampleInvoice(ValidRecipient()));
            Assert.False(status.Ok);
            Assert.Contains(status.Messages, m => m.Code == "E413");
        }
    }
}
