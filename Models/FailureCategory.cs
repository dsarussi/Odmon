namespace Odmon.Worker.Models
{
    /// <summary>
    /// Classification of failures for daily summary vs immediate alerts.
    /// Critical = immediate alert; Operational = daily summary only; Ignored = expected skips, not shown.
    /// </summary>
    public enum FailureCategory
    {
        Critical,
        Operational,
        KnownDataIssue,
        SkippedExpected,
        Ignored
    }
}
