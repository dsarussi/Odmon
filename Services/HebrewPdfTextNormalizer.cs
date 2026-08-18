using System.Globalization;
using System.Text;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Converts the visual-order Hebrew runs produced by PdfPig into logical order.
    /// Non-Hebrew content is copied without reordering.
    /// </summary>
    public sealed class HebrewPdfTextNormalizer
    {
        public string Normalize(string extractedText)
        {
            ArgumentNullException.ThrowIfNull(extractedText);

            var normalized = new StringBuilder(extractedText.Length);
            for (var index = 0; index < extractedText.Length;)
            {
                if (!IsHebrewRunStart(extractedText, index))
                {
                    normalized.Append(extractedText[index++]);
                    continue;
                }

                var runStart = index;
                var runEnd = index;
                var containsHebrewLetter = false;
                while (runEnd < extractedText.Length)
                {
                    var character = extractedText[runEnd];
                    if (IsHebrewLetter(character))
                    {
                        containsHebrewLetter = true;
                        runEnd++;
                        continue;
                    }

                    if (!IsReversibleConnector(character))
                    {
                        break;
                    }

                    runEnd++;
                }

                while (runEnd > runStart && char.IsWhiteSpace(extractedText[runEnd - 1]))
                {
                    runEnd--;
                }

                if (!containsHebrewLetter || runEnd == runStart)
                {
                    normalized.Append(extractedText[index++]);
                    continue;
                }

                if (normalized.Length > 0 &&
                    IsDecimalDigit(normalized[^1]) &&
                    !char.IsWhiteSpace(normalized[^1]))
                {
                    normalized.Append(' ');
                }

                AppendReversedTextElements(
                    normalized,
                    extractedText[runStart..runEnd]);

                if (runEnd < extractedText.Length &&
                    IsDecimalDigit(extractedText[runEnd]))
                {
                    normalized.Append(' ');
                }

                index = runEnd;
            }

            return normalized.ToString();
        }

        private static bool IsHebrewRunStart(string text, int index)
        {
            if (IsHebrewLetter(text[index]))
            {
                return true;
            }

            return !char.IsWhiteSpace(text[index]) &&
                   IsReversibleConnector(text[index]) &&
                   index + 1 < text.Length &&
                   IsHebrewLetter(text[index + 1]);
        }

        private static bool IsHebrewLetter(char character)
            => character is >= '\u0590' and <= '\u05FF' && char.IsLetter(character);

        private static bool IsReversibleConnector(char character)
        {
            if (character is '\r' or '\n' or ':' or '\u05C3')
            {
                return false;
            }

            if (char.IsWhiteSpace(character) ||
                character is >= '\u0590' and <= '\u05FF')
            {
                return true;
            }

            return character is '\'' or '"' or '.' or ',' or '-' or '–' or '—' or
                   '(' or ')' or '[' or ']' or '{' or '}' or '/' or '\\';
        }

        private static bool IsDecimalDigit(char character)
            => CharUnicodeInfo.GetDecimalDigitValue(character) >= 0;

        private static void AppendReversedTextElements(
            StringBuilder destination,
            string value)
        {
            var elements = new List<string>();
            var enumerator = StringInfo.GetTextElementEnumerator(value);
            while (enumerator.MoveNext())
            {
                elements.Add(enumerator.GetTextElement());
            }

            for (var index = elements.Count - 1; index >= 0; index--)
            {
                destination.Append(elements[index]);
            }
        }
    }
}
