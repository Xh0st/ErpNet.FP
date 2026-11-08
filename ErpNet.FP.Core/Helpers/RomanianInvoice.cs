namespace ErpNet.FP.Core
{
    /// <summary>
    /// Shared validation for a Romanian simplified fiscal invoice (bon fiscal cu CUI cumpărător,
    /// art. 319 alin. 12 Cod fiscal / OUG 28/1999). Country-level rules only; the device command
    /// that carries the CUI is driver-specific.
    /// </summary>
    public static class RomanianInvoice
    {
        /// <summary>
        /// Validates the recipient (buyer) block and the number-assignment rule for a Romanian
        /// native invoice. The items/payments are validated separately by the driver's receipt
        /// validation; this only covers the invoice-mode fields.
        /// </summary>
        public static void ValidateRecipient(
            DeviceStatus status,
            IInvoiceDocument document,
            NumberAssignment numberAssignment)
        {
            var recipient = document.Recipient;
            if (recipient == null)
            {
                status.AddError("E405", "Invoice requires a \"recipient\" (buyer) block");
                return;
            }

            if (string.IsNullOrWhiteSpace(recipient.Name))
            {
                status.AddError("E405", "Recipient \"name\" is required");
            }

            if (string.IsNullOrWhiteSpace(recipient.Identifier))
            {
                status.AddError("E405", "Recipient \"identifier\" (CUI) is required");
            }

            // Which identifier types the Romanian fiscal invoice accepts as the buyer CUI.
            switch (recipient.IdentifierType)
            {
                case IdentifierType.TaxNumber:
                case IdentifierType.LegalRegistration:
                    break;
                case IdentifierType.Unspecified:
                    status.AddError("E405", "Recipient \"identifierType\" is required");
                    break;
                case IdentifierType.NationalId:
                    status.AddError("E412", "Recipient \"identifierType\" national-id (CNP) is not supported on the fiscal invoice");
                    break;
                case IdentifierType.ForeignerId:
                    status.AddError("E412", "Recipient \"identifierType\" foreigner-id is not supported on the fiscal invoice");
                    break;
                default:
                    status.AddError("E412", $"Recipient \"identifierType\" {recipient.IdentifierType} is not supported");
                    break;
            }

            // Only check the CUI control digit when the type is a domestic CUI and a value was given.
            if (!string.IsNullOrWhiteSpace(recipient.Identifier)
                && (recipient.IdentifierType == IdentifierType.TaxNumber
                    || recipient.IdentifierType == IdentifierType.LegalRegistration)
                && !RomanianTaxId.IsValid(recipient.Identifier))
            {
                status.AddError("E405", $"Recipient \"identifier\" is not a valid Romanian CUI: '{recipient.Identifier}'");
            }

            if (!string.IsNullOrEmpty(document.Number) && numberAssignment == NumberAssignment.DeviceAssigned)
            {
                status.AddError("E412", "The invoice number is assigned by the device; do not send \"number\"");
            }
        }

        /// <summary>
        /// Returns the bare CUI digits (without the "RO" prefix) to send to the device,
        /// or the original identifier unchanged if it cannot be normalized.
        /// </summary>
        public static string CuiDigits(string? identifier)
        {
            return RomanianTaxId.TryNormalize(identifier, out var digits, out _) ? digits : (identifier ?? string.Empty);
        }
    }
}
