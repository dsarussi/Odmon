using System.Globalization;

namespace Odmon.Worker.Services
{
    /// <summary>Pure, conservative normalization shared by extraction and lookup.</summary>
    internal static class EmailEvidenceNormalization
    {
        internal static string? VehicleNumber(string value)
        {
            var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
            return digits.Length is >= 5 and <= 10 ? digits : null;
        }

        internal static string? EventDate(string value)
        {
            var formats = new[]
            {
                "d/M/yyyy", "dd/MM/yyyy", "d.M.yyyy", "dd.MM.yyyy",
                "d-M-yyyy", "dd-MM-yyyy", "yyyy-M-d", "yyyy-MM-dd"
            };
            return DateOnly.TryParseExact(
                value.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
        }

        internal static string EventDate(DateTime value)
            => DateOnly.FromDateTime(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        internal static string? Text(string value, int maximumLength = 128)
        {
            var normalized = string.Join(
                " ",
                value.Trim()
                    .TrimEnd('.', ',', ';', ':')
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return normalized.Length is > 0 && normalized.Length <= maximumLength
                ? normalized
                : null;
        }

        internal static string? Phone(string value)
        {
            var trimmed = value.Trim();
            var hasInternationalPrefix = trimmed.StartsWith('+');
            var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length is < 9 or > 15)
                return null;
            return hasInternationalPrefix ? "+" + digits : digits;
        }
    }
}
