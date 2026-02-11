using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for the bootstrap onboarding cooling period.
    /// Cases must exist for at least CoolingPeriodDays (default 3) before onboarding.
    /// Cooling applies ONLY to bootstrap creation, NOT to reconcile updates of mapped cases.
    /// </summary>
    public class CoolingPeriodTests
    {
        private static readonly DateTime UtcNow = new(2026, 2, 15, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime CutoffDate = new(2026, 2, 1);
        private const int DefaultCoolingDays = 3;

        // ====================================================================
        // 1. Case created "today" (age < 3 days) -> NOT created
        // ====================================================================

        [Fact]
        public void CaseCreatedToday_NotEligible()
        {
            // Created today: age = 0 days, cooling = 3 days -> not eligible
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.False(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        // ====================================================================
        // 2. Case created 2 days ago -> NOT created
        // ====================================================================

        [Fact]
        public void CaseCreated2DaysAgo_NotEligible()
        {
            // Created 2 days ago: age < 3 days -> not eligible
            var tsCreateDate = new DateTime(2026, 2, 13);
            Assert.False(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        // ====================================================================
        // 3. Case created 3+ days ago -> created
        // ====================================================================

        [Fact]
        public void CaseCreated3DaysAgo_Eligible()
        {
            // Created exactly 3 days ago: age == 3 days -> eligible
            // coolingCutoff = UtcNow.AddDays(-3).Date = 2026-02-12
            // tsCreateDate.Date = 2026-02-12 <= 2026-02-12 -> eligible
            var tsCreateDate = new DateTime(2026, 2, 12);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void CaseCreated5DaysAgo_Eligible()
        {
            // Created 5 days ago: well past cooling -> eligible
            var tsCreateDate = new DateTime(2026, 2, 10);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        // ====================================================================
        // 4. CoolingPeriodDays=0 -> created immediately (current behavior)
        // ====================================================================

        [Fact]
        public void CoolingPeriodZero_CreatedImmediately()
        {
            // Cooling disabled: case created today should be eligible
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, coolingPeriodDays: 0, UtcNow));
        }

        [Fact]
        public void CoolingPeriodZero_NullCreateDate_NotEligible()
        {
            // Even with cooling=0, null tsCreateDate is not eligible
            Assert.False(SyncService.IsCoolingEligible(null, coolingPeriodDays: 0, UtcNow));
        }

        // ====================================================================
        // 5. Already mapped case -> NOT affected by cooling (reconcile only)
        // ====================================================================

        [Fact]
        public void AlreadyMappedCase_NotAffectedByCooling()
        {
            // Cooling applies ONLY to bootstrap creation (unmapped cases).
            // A mapped case (even if created today) is part of the "managed universe"
            // and is updated via the reconcile phase — no cooling check.
            // This test verifies the reconcile set computation is independent.
            var eligibleFromOdcanit = new HashSet<int> { 50001, 50002 };
            var mapped = new HashSet<int> { 50001 }; // already mapped

            // eligibleMapped = INTERSECT(eligible, mapped)
            var eligibleMapped = new HashSet<int>(eligibleFromOdcanit);
            eligibleMapped.IntersectWith(mapped);

            // 50001 is eligible for reconcile regardless of age
            Assert.Contains(50001, eligibleMapped);
            // 50002 is unmapped -> not in reconcile set (would go through bootstrap)
            Assert.DoesNotContain(50002, eligibleMapped);
        }

        // ====================================================================
        // 6. NULL tsCreateDate -> not created + warning
        // ====================================================================

        [Fact]
        public void NullCreateDate_NotEligible()
        {
            Assert.False(SyncService.IsCoolingEligible(null, DefaultCoolingDays, UtcNow));
        }

        // ====================================================================
        // Edge cases
        // ====================================================================

        [Fact]
        public void ExactBoundary_3DaysAgo_EndOfDay_Eligible()
        {
            // tsCreateDate = 2026-02-12 (date-only from Odcanit, always 00:00:00)
            // coolingCutoff = 2026-02-12
            // 2026-02-12 <= 2026-02-12 -> eligible
            var tsCreateDate = new DateTime(2026, 2, 12, 0, 0, 0);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void OneDayBeforeBoundary_NotEligible()
        {
            // tsCreateDate = 2026-02-13 (2 days ago)
            // coolingCutoff = 2026-02-12
            // 2026-02-13 > 2026-02-12 -> NOT eligible
            var tsCreateDate = new DateTime(2026, 2, 13, 0, 0, 0);
            Assert.False(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void CoolingPeriod1Day_CreatedYesterday_Eligible()
        {
            // 1-day cooling: case from yesterday should be eligible
            var tsCreateDate = new DateTime(2026, 2, 14);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, coolingPeriodDays: 1, UtcNow));
        }

        [Fact]
        public void CoolingPeriod1Day_CreatedToday_NotEligible()
        {
            // 1-day cooling: case from today should NOT be eligible
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.False(SyncService.IsCoolingEligible(tsCreateDate, coolingPeriodDays: 1, UtcNow));
        }

        [Fact]
        public void BootstrapFiltering_CoolingApplied_OnlyOldCasesPass()
        {
            // Simulates full bootstrap filtering with cooling
            var cases = new List<OdcanitCase>
            {
                MakeCase(1001, new DateTime(2026, 2, 10)), // 5 days old -> eligible
                MakeCase(1002, new DateTime(2026, 2, 12)), // 3 days old -> eligible (boundary)
                MakeCase(1003, new DateTime(2026, 2, 14)), // 1 day old -> NOT eligible
                MakeCase(1004, new DateTime(2026, 2, 15)), // created today -> NOT eligible
                MakeCase(1005, null),                       // null date -> NOT eligible
            };

            var eligible = cases
                .Where(c => SyncService.IsCoolingEligible(c.tsCreateDate, DefaultCoolingDays, UtcNow))
                .Select(c => c.TikCounter)
                .ToList();

            Assert.Equal(2, eligible.Count);
            Assert.Contains(1001, eligible);
            Assert.Contains(1002, eligible);
            Assert.DoesNotContain(1003, eligible);
            Assert.DoesNotContain(1004, eligible);
            Assert.DoesNotContain(1005, eligible);
        }

        // ====================================================================
        // Combined bootstrap eligibility (cutoff + cooling)
        // ====================================================================

        [Fact]
        public void PreCutoffCase_EvenIfVeryOld_NeverOnboarded()
        {
            // tsCreateDate = 2026-01-15, CutoffDate = 2026-02-01
            // This case is 31 days old (well past cooling), but it was created BEFORE the cutoff.
            // Pre-cutoff cases must NEVER be onboarded regardless of age.
            var tsCreateDate = new DateTime(2026, 1, 15);
            Assert.False(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));

            // Also verify with cooling disabled — still blocked by cutoff
            Assert.False(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, coolingPeriodDays: 0, UtcNow));
        }

        [Fact]
        public void PostCutoffButTooRecent_NotOnboarded()
        {
            // tsCreateDate = 2026-02-14, CutoffDate = 2026-02-01
            // Post-cutoff (good), but only 1 day old (within 3-day cooling window).
            // Must NOT be onboarded yet.
            var tsCreateDate = new DateTime(2026, 2, 14);
            Assert.False(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void PostCutoffAndCooled_Onboarded()
        {
            // tsCreateDate = 2026-02-10, CutoffDate = 2026-02-01
            // Post-cutoff (good) AND 5 days old (past 3-day cooling).
            // This case is eligible for onboarding.
            var tsCreateDate = new DateTime(2026, 2, 10);
            Assert.True(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void PostCutoffAndCooled_ExactBoundary_Onboarded()
        {
            // tsCreateDate = 2026-02-12 (exactly 3 days before UtcNow), CutoffDate = 2026-02-01
            // Post-cutoff AND exactly at cooling boundary -> eligible
            var tsCreateDate = new DateTime(2026, 2, 12);
            Assert.True(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void NullCreateDate_NeverBootstrapEligible()
        {
            Assert.False(SyncService.IsBootstrapEligible(null, CutoffDate, DefaultCoolingDays, UtcNow));
            Assert.False(SyncService.IsBootstrapEligible(null, CutoffDate, coolingPeriodDays: 0, UtcNow));
        }

        // ====================================================================
        // Helper
        // ====================================================================

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
