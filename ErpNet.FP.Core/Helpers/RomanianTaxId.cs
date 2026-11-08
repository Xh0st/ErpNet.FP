namespace ErpNet.FP.Core
{
    using System.Linq;

    /// <summary>
    /// Romanian fiscal identifier (CUI / CIF) helpers.
    /// <para>
    /// The CUI is 2 to 10 digits; the last digit is a control digit computed with the national
    /// key 753217532 (sum of each base digit times the key digit aligned from the right, times 10,
    /// modulo 11, with 10 mapped to 0). A VAT payer's number is the same CUI prefixed with "RO".
    /// This is a public, country-level algorithm, not a device command.
    /// </para>
    /// </summary>
    public static class RomanianTaxId
    {
        private const string ControlKey = "753217532";
        public const int MinDigits = 2;
        public const int MaxDigits = 10;

        /// <summary>
        /// Strips an optional leading "RO"/"ro" (and surrounding spaces) and returns the bare digits.
        /// Returns false if what remains is not a plausible CUI (2..10 digits).
        /// </summary>
        public static bool TryNormalize(string? input, out string digits, out bool hadRoPrefix)
        {
            digits = string.Empty;
            hadRoPrefix = false;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var s = input.Trim();
            if (s.Length >= 2 && (s[0] == 'R' || s[0] == 'r') && (s[1] == 'O' || s[1] == 'o'))
            {
                hadRoPrefix = true;
                s = s.Substring(2).Trim();
            }

            if (s.Length < MinDigits || s.Length > MaxDigits || !s.All(char.IsDigit))
            {
                return false;
            }

            digits = s;
            return true;
        }

        /// <summary>
        /// True when <paramref name="input"/> is a valid Romanian CUI, with or without the "RO" prefix.
        /// </summary>
        public static bool IsValid(string? input)
        {
            if (!TryNormalize(input, out var digits, out _)) return false;

            var baseDigits = digits.Substring(0, digits.Length - 1);
            var control = digits[digits.Length - 1] - '0';

            // The control key is used right-aligned against the base digits.
            var key = ControlKey.Substring(ControlKey.Length - baseDigits.Length);
            var sum = 0;
            for (var i = 0; i < baseDigits.Length; i++)
            {
                sum += (baseDigits[i] - '0') * (key[i] - '0');
            }

            var computed = (sum * 10) % 11;
            if (computed == 10) computed = 0;
            return computed == control;
        }
    }
}
