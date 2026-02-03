using System;

namespace Odmon.Worker.Exceptions
{
    /// <summary>
    /// Thrown when a critical field fails validation before Monday sync.
    /// Prevents incorrect data from being sent to Monday.com.
    /// </summary>
    public class CriticalFieldValidationException : Exception
    {
        public int TikCounter { get; }
        public string? TikNumber { get; }
        public string ColumnId { get; }
        public string? FieldValue { get; }
        public string ValidationReason { get; }

        public CriticalFieldValidationException(
            int tikCounter,
            string? tikNumber,
            string columnId,
            string? fieldValue,
            string validationReason)
            : base($"Critical field validation failed for TikCounter={tikCounter}, TikNumber={tikNumber ?? "<null>"}, " +
                   $"ColumnId={columnId}, Value='{fieldValue ?? "<null>"}', Reason={validationReason}")
        {
            TikCounter = tikCounter;
            TikNumber = tikNumber;
            ColumnId = columnId;
            FieldValue = fieldValue;
            ValidationReason = validationReason;
        }

        public CriticalFieldValidationException(
            int tikCounter,
            string? tikNumber,
            string columnId,
            string? fieldValue,
            string validationReason,
            Exception innerException)
            : base($"Critical field validation failed for TikCounter={tikCounter}, TikNumber={tikNumber ?? "<null>"}, " +
                   $"ColumnId={columnId}, Value='{fieldValue ?? "<null>"}', Reason={validationReason}", innerException)
        {
            TikCounter = tikCounter;
            TikNumber = tikNumber;
            ColumnId = columnId;
            FieldValue = fieldValue;
            ValidationReason = validationReason;
        }
    }
}
