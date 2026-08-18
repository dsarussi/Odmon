using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    internal sealed record FieldValidationResult<TValue>(
        TValue Value,
        CaseIntakeFieldStatus Status,
        string? Message);

    internal static partial class CaseIntakeFieldValidators
    {
        private static readonly string[] AcceptedDateFormats =
        [
            "dd/MM/yyyy",
            "dd.MM.yyyy",
            "dd-MM-yyyy",
            "yyyy-MM-dd"
        ];

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
        private static partial Regex WhitespaceRegex();

        [GeneratedRegex(
            @"(?<![\d.,])[-+]?(?:\d{1,3}(?:[.,]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?![\d.,])",
            RegexOptions.CultureInvariant)]
        private static partial Regex AmountTokenRegex();

        internal static FieldValidationResult<string?> ValidateIdentifierNumber(string raw)
        {
            if (!TryCollectDigits(raw, IsIdentifierSeparator, out var digits))
            {
                return InvalidString("Israeli ID contains unsupported characters.");
            }

            if (digits.Length is < 5 or > 9)
            {
                return InvalidString("Israeli ID must contain between 5 and 9 digits.");
            }

            var normalized = digits.PadLeft(9, '0');
            var checksum = 0;
            for (var index = 0; index < normalized.Length; index++)
            {
                var digit = normalized[index] - '0';
                var product = digit * (index % 2 == 0 ? 1 : 2);
                checksum += product > 9 ? product - 9 : product;
            }

            return checksum % 10 == 0
                ? ValidString(normalized)
                : InvalidString("Israeli ID checksum is invalid.");
        }

        internal static FieldValidationResult<string?> ValidatePhone(string raw)
        {
            if (!TryCollectDigits(raw, IsPhoneSeparator, out var digits))
            {
                return InvalidString("Phone number contains unsupported characters.");
            }

            if (digits.StartsWith("00972", StringComparison.Ordinal))
            {
                digits = "0" + digits[5..];
            }
            else if (digits.StartsWith("972", StringComparison.Ordinal))
            {
                digits = "0" + digits[3..];
            }
            else if (digits.Length == 9 && !digits.StartsWith('0'))
            {
                digits = "0" + digits;
            }

            var isMobileOrVoip =
                digits.Length == 10 &&
                (digits.StartsWith("05", StringComparison.Ordinal) ||
                 digits.StartsWith("07", StringComparison.Ordinal));
            var isLandline =
                digits.Length == 9 &&
                digits[0] == '0' &&
                "23489".IndexOf(digits[1], StringComparison.Ordinal) >= 0;

            return isMobileOrVoip || isLandline
                ? ValidString(digits)
                : InvalidString("Phone number is not a plausible Israeli phone number.");
        }

        internal static FieldValidationResult<string?> ValidateVehicleNumber(string raw)
        {
            if (!TryCollectDigits(raw, IsVehicleSeparator, out var digits))
            {
                return InvalidString("Vehicle number contains unsupported characters.");
            }

            return digits.Length is 7 or 8
                ? ValidString(digits)
                : InvalidString("Vehicle number must contain 7 or 8 digits.");
        }

        internal static FieldValidationResult<DateOnly?> ValidateDate(string raw)
        {
            var normalized = raw.Trim();
            foreach (var format in AcceptedDateFormats)
            {
                if (DateOnly.TryParseExact(
                        normalized,
                        format,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var date))
                {
                    return new(date, CaseIntakeFieldStatus.Valid, null);
                }
            }

            return new(
                null,
                CaseIntakeFieldStatus.Invalid,
                "Date must use dd/MM/yyyy, dd.MM.yyyy, dd-MM-yyyy, or yyyy-MM-dd.");
        }

        internal static FieldValidationResult<decimal?> ValidateAmount(string raw)
        {
            var normalized = NormalizeUnicodeDigits(raw);
            var tokens = AmountTokenRegex()
                .Matches(normalized)
                .Cast<Match>()
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (tokens.Length == 0)
            {
                return new(null, CaseIntakeFieldStatus.Invalid, "Amount contains no numeric value.");
            }

            if (tokens.Length > 1)
            {
                return new(
                    null,
                    CaseIntakeFieldStatus.Ambiguous,
                    "Amount contains multiple distinct numeric values.");
            }

            return TryParseAmountToken(tokens[0], out var amount)
                ? new(amount, CaseIntakeFieldStatus.Valid, null)
                : new(null, CaseIntakeFieldStatus.Invalid, "Amount format is not supported.");
        }

        internal static FieldValidationResult<string?> ValidateNumber(string raw, string fieldName)
        {
            var normalized = RemoveWhitespaceAndNormalizeDigits(raw);
            if (normalized.Length == 0)
            {
                return InvalidString($"{fieldName} is empty after normalization.");
            }

            if (!normalized.Any(char.IsDigit))
            {
                return InvalidString($"{fieldName} must contain at least one digit.");
            }

            if (normalized.Any(character =>
                    !char.IsLetterOrDigit(character) &&
                    character is not '-' and not '/' and not '.' and not '_'))
            {
                return InvalidString($"{fieldName} contains unsupported characters.");
            }

            return ValidString(normalized);
        }

        internal static FieldValidationResult<string?> ValidateName(string raw)
        {
            var normalized = WhitespaceRegex().Replace(raw.Trim(), " ");
            if (normalized.Length == 0 || !normalized.Any(char.IsLetter))
            {
                return InvalidString("Name must contain at least one letter.");
            }

            if (normalized.Any(char.IsControl))
            {
                return InvalidString("Name contains unsupported control characters.");
            }

            return ValidString(normalized);
        }

        private static bool TryCollectDigits(
            string raw,
            Func<char, bool> isAllowedSeparator,
            out string digits)
        {
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw.Trim())
            {
                var numericValue = CharUnicodeInfo.GetDecimalDigitValue(character);
                if (numericValue >= 0)
                {
                    builder.Append((char)('0' + numericValue));
                }
                else if (!isAllowedSeparator(character))
                {
                    digits = string.Empty;
                    return false;
                }
            }

            digits = builder.ToString();
            return true;
        }

        private static string RemoveWhitespaceAndNormalizeDigits(string raw)
        {
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                var numericValue = CharUnicodeInfo.GetDecimalDigitValue(character);
                builder.Append(numericValue >= 0 ? (char)('0' + numericValue) : character);
            }

            return builder.ToString();
        }

        private static string NormalizeUnicodeDigits(string raw)
        {
            var builder = new StringBuilder(raw.Length);
            foreach (var character in raw)
            {
                var numericValue = CharUnicodeInfo.GetDecimalDigitValue(character);
                builder.Append(numericValue >= 0 ? (char)('0' + numericValue) : character);
            }

            return builder.ToString();
        }

        private static bool TryParseAmountToken(string token, out decimal amount)
        {
            amount = default;
            var sign = string.Empty;
            if (token.StartsWith('+') || token.StartsWith('-'))
            {
                sign = token[..1];
                token = token[1..];
            }

            var commaIndex = token.LastIndexOf(',');
            var dotIndex = token.LastIndexOf('.');
            char? decimalSeparator = null;

            if (commaIndex >= 0 && dotIndex >= 0)
            {
                var lastSeparator = Math.Max(commaIndex, dotIndex);
                var digitsAfter = token.Length - lastSeparator - 1;
                if (digitsAfter is 1 or 2)
                {
                    decimalSeparator = token[lastSeparator];
                }
            }
            else
            {
                var separator = commaIndex >= 0 ? ',' : dotIndex >= 0 ? '.' : (char?)null;
                if (separator.HasValue)
                {
                    var separatorCount = token.Count(character => character == separator.Value);
                    var lastSeparator = token.LastIndexOf(separator.Value);
                    var digitsAfter = token.Length - lastSeparator - 1;
                    if (digitsAfter is 1 or 2)
                    {
                        decimalSeparator = separator;
                    }
                    else if (digitsAfter != 3 || separatorCount == 0)
                    {
                        return false;
                    }
                }
            }

            var normalized = new StringBuilder(token.Length + sign.Length);
            normalized.Append(sign);
            for (var index = 0; index < token.Length; index++)
            {
                var character = token[index];
                if (character is not ',' and not '.')
                {
                    normalized.Append(character);
                    continue;
                }

                if (decimalSeparator.HasValue &&
                    character == decimalSeparator.Value &&
                    index == token.LastIndexOf(decimalSeparator.Value))
                {
                    normalized.Append('.');
                }
            }

            return decimal.TryParse(
                normalized.ToString(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out amount);
        }

        private static bool IsIdentifierSeparator(char character)
            => char.IsWhiteSpace(character) || character is '-' or '.';

        private static bool IsPhoneSeparator(char character)
            => char.IsWhiteSpace(character) || character is '+' or '-' or '(' or ')' or '.';

        private static bool IsVehicleSeparator(char character)
            => char.IsWhiteSpace(character) || character is '-' or '.';

        private static FieldValidationResult<string?> ValidString(string value)
            => new(value, CaseIntakeFieldStatus.Valid, null);

        private static FieldValidationResult<string?> InvalidString(string message)
            => new(null, CaseIntakeFieldStatus.Invalid, message);
    }
}
