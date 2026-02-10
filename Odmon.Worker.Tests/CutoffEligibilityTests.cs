using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests the cutoff eligibility logic used in SyncService listener gating.
    /// A case is eligible iff tsCreateDate.Date >= cutoffDate.
    /// </summary>
    public class CutoffEligibilityTests
    {
        /// <summary>
        /// Replicates the inline eligibility check from SyncService.SyncOdcanitToMondayAsync:
        /// eligible := tsCreateDate.HasValue && tsCreateDate.Value.Date >= cutoffDate
        /// </summary>
        private static bool IsEligible(DateTime? tsCreateDate, DateTime cutoffDate)
        {
            var caseDate = tsCreateDate?.Date;
            return caseDate.HasValue && caseDate.Value >= cutoffDate;
        }

        [Fact]
        public void PreCutoff_Unmapped_InFeed_NotCreated()
        {
            // Case opened 2026-02-09, cutoff is 2026-02-10 → NOT eligible
            var cutoff = new DateTime(2026, 2, 10);
            var tsCreateDate = new DateTime(2026, 2, 9, 0, 0, 0); // date-only, 00:00:00

            Assert.False(IsEligible(tsCreateDate, cutoff));
        }

        [Fact]
        public void PreCutoff_Mapped_InFeed_NotUpdated()
        {
            // Case opened 2026-01-15, cutoff is 2026-02-10 → NOT eligible (skipped even if mapped)
            var cutoff = new DateTime(2026, 2, 10);
            var tsCreateDate = new DateTime(2026, 1, 15, 0, 0, 0);

            Assert.False(IsEligible(tsCreateDate, cutoff));
        }

        [Fact]
        public void PostCutoff_Unmapped_InFeed_Created()
        {
            // Case opened 2026-02-10 (same day as cutoff, date-only 00:00:00) → eligible
            var cutoff = new DateTime(2026, 2, 10);
            var tsCreateDate = new DateTime(2026, 2, 10, 0, 0, 0);

            Assert.True(IsEligible(tsCreateDate, cutoff));
        }

        [Fact]
        public void PostCutoff_Mapped_InFeed_Updated()
        {
            // Case opened 2026-02-11, cutoff is 2026-02-10 → eligible
            var cutoff = new DateTime(2026, 2, 10);
            var tsCreateDate = new DateTime(2026, 2, 11, 0, 0, 0);

            Assert.True(IsEligible(tsCreateDate, cutoff));
        }

        [Fact]
        public void NullCreateDate_NotEligible()
        {
            // tsCreateDate is null → not eligible (strict)
            var cutoff = new DateTime(2026, 2, 10);
            Assert.False(IsEligible(null, cutoff));
        }

        [Fact]
        public void SameDayCutoff_DateOnly00_IsEligible()
        {
            // Specifically covers the known bug: tsCreateDate is 2026-02-10 00:00:00,
            // cutoff is 2026-02-10. Date-only comparison: 2026-02-10 >= 2026-02-10 → true.
            var cutoff = new DateTime(2026, 2, 10);
            var tsCreateDate = new DateTime(2026, 2, 10, 0, 0, 0);

            Assert.True(IsEligible(tsCreateDate, cutoff));
        }

        [Fact]
        public void BatchFilteringPreservesOnlyEligibleCases()
        {
            var cutoff = new DateTime(2026, 2, 10);
            var cases = new List<OdcanitCase>
            {
                MakeCase(1001, new DateTime(2026, 2, 9, 0, 0, 0)),  // pre-cutoff
                MakeCase(1002, new DateTime(2026, 2, 10, 0, 0, 0)), // exactly on cutoff
                MakeCase(1003, new DateTime(2026, 2, 11, 0, 0, 0)), // post-cutoff
                MakeCase(1004, null),                                 // null create date
            };

            var eligible = cases.Where(c => IsEligible(c.tsCreateDate, cutoff)).ToList();

            Assert.Equal(2, eligible.Count);
            Assert.Contains(eligible, c => c.TikCounter == 1002);
            Assert.Contains(eligible, c => c.TikCounter == 1003);
            Assert.DoesNotContain(eligible, c => c.TikCounter == 1001);
            Assert.DoesNotContain(eligible, c => c.TikCounter == 1004);
        }

        private static OdcanitCase MakeCase(int tikCounter, DateTime? tsCreateDate)
        {
            return new OdcanitCase
            {
                TikCounter = tikCounter,
                TikNumber = $"9/{tikCounter}",
                TikName = "Test",
                ClientName = "Test Client",
                StatusName = "פעיל",
                tsCreateDate = tsCreateDate
            };
        }
    }
}
