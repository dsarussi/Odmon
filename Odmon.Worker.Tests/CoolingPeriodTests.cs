using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for the bootstrap onboarding cooling period using Israeli business days.
    /// Israeli business days: Sunday–Thursday. Friday and Saturday are skipped.
    /// The day the case is opened counts as business day #1.
    /// Cooling applies ONLY to bootstrap creation, NOT to reconcile updates of mapped cases.
    /// </summary>
    public class CoolingPeriodTests
    {
        // 2026-02-15 is a Sunday
        private static readonly DateTime UtcNow = new(2026, 2, 15, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime CutoffDate = new(2026, 2, 1);
        private const int DefaultCoolingDays = 3;

        // ====================================================================
        // AddIsraeliBusinessDays — core function tests
        // ====================================================================

        [Fact]
        public void AddBusinessDays_ThursdayOpen_3Days_EligibleTuesday()
        {
            // Thu(1), [Fri skip, Sat skip], Sun(2), Mon(3) → eligible Tue
            var start = new DateOnly(2026, 2, 12); // Thursday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 3);
            Assert.Equal(new DateOnly(2026, 2, 17), eligible); // Tuesday
        }

        [Fact]
        public void AddBusinessDays_FridayOpen_3Days_EligibleWednesday()
        {
            // Fri is not a business day → first BD = Sun(1), Mon(2), Tue(3) → eligible Wed
            var start = new DateOnly(2026, 2, 13); // Friday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 3);
            Assert.Equal(new DateOnly(2026, 2, 18), eligible); // Wednesday
        }

        [Fact]
        public void AddBusinessDays_SaturdayOpen_3Days_EligibleWednesday()
        {
            // Sat is not a business day → first BD = Sun(1), Mon(2), Tue(3) → eligible Wed
            var start = new DateOnly(2026, 2, 14); // Saturday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 3);
            Assert.Equal(new DateOnly(2026, 2, 18), eligible); // Wednesday
        }

        [Fact]
        public void AddBusinessDays_SundayOpen_3Days_EligibleWednesday()
        {
            // Sun(1), Mon(2), Tue(3) → eligible Wed
            var start = new DateOnly(2026, 2, 15); // Sunday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 3);
            Assert.Equal(new DateOnly(2026, 2, 18), eligible); // Wednesday
        }

        [Fact]
        public void AddBusinessDays_MondayOpen_3Days_EligibleThursday()
        {
            // Mon(1), Tue(2), Wed(3) → eligible Thu
            var start = new DateOnly(2026, 2, 16); // Monday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 3);
            Assert.Equal(new DateOnly(2026, 2, 19), eligible); // Thursday
        }

        [Fact]
        public void AddBusinessDays_WednesdayOpen_3Days_EligibleSunday()
        {
            // Wed(1), Thu(2), [Fri skip, Sat skip], Sun(3) → eligible Mon
            var start = new DateOnly(2026, 2, 11); // Wednesday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 3);
            Assert.Equal(new DateOnly(2026, 2, 16), eligible); // Monday
        }

        [Fact]
        public void AddBusinessDays_CrossingMultipleWeekends_7Days()
        {
            // Start: Thu 2026-02-12
            // Thu(1), [Fri,Sat], Sun(2), Mon(3), Tue(4), Wed(5), Thu(6), [Fri,Sat], Sun(7)
            // 7th business day = Sun 2026-02-22 → eligible Mon 2026-02-23
            var start = new DateOnly(2026, 2, 12); // Thursday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 7);
            Assert.Equal(new DateOnly(2026, 2, 23), eligible); // Monday
        }

        [Fact]
        public void AddBusinessDays_1Day_NextCalendarDay_IfBusinessDay()
        {
            // Mon(1) → eligible Tue
            var start = new DateOnly(2026, 2, 16); // Monday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 1);
            Assert.Equal(new DateOnly(2026, 2, 17), eligible); // Tuesday
        }

        [Fact]
        public void AddBusinessDays_1Day_Thursday_EligibleFriday()
        {
            // Thu(1) → eligible Fri (Fri is the NEXT calendar day, even though it's a weekend)
            var start = new DateOnly(2026, 2, 12); // Thursday
            var eligible = SyncService.AddIsraeliBusinessDays(start, 1);
            Assert.Equal(new DateOnly(2026, 2, 13), eligible); // Friday
        }

        [Fact]
        public void AddBusinessDays_ZeroDays_ReturnsStartDate()
        {
            var start = new DateOnly(2026, 2, 12);
            var eligible = SyncService.AddIsraeliBusinessDays(start, 0);
            Assert.Equal(start, eligible);
        }

        // ====================================================================
        // IsIsraeliBusinessDay
        // ====================================================================

        [Fact]
        public void IsBusinessDay_SundayThruThursday_True()
        {
            // 2026-02-15 is Sunday
            Assert.True(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 15)));  // Sun
            Assert.True(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 16)));  // Mon
            Assert.True(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 17)));  // Tue
            Assert.True(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 18)));  // Wed
            Assert.True(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 19)));  // Thu
        }

        [Fact]
        public void IsBusinessDay_FridayAndSaturday_False()
        {
            Assert.False(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 13))); // Fri
            Assert.False(SyncService.IsIsraeliBusinessDay(new DateOnly(2026, 2, 14))); // Sat
        }

        // ====================================================================
        // IsCoolingEligible — integrated tests with Israeli business days
        // ====================================================================

        [Fact]
        public void CoolingEligible_ThursdayOpen_3BD_NotEligibleOnSunday()
        {
            // Opened Thu 2026-02-12, cooling=3 BD
            // Thu(1), Sun(2), Mon(3) → eligible Tue 2026-02-17
            // UtcNow = Sun 2026-02-15 → NOT eligible yet
            var tsCreateDate = new DateTime(2026, 2, 12);
            Assert.False(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void CoolingEligible_ThursdayOpen_3BD_EligibleOnTuesday()
        {
            // Opened Thu 2026-02-12, cooling=3 BD
            // eligible from Tue 2026-02-17
            // "Now" = Tue 2026-02-17 12:00 UTC → eligible
            var tuesdayUtc = new DateTime(2026, 2, 17, 12, 0, 0, DateTimeKind.Utc);
            var tsCreateDate = new DateTime(2026, 2, 12);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, tuesdayUtc));
        }

        [Fact]
        public void CoolingEligible_SundayOpen_3BD_EligibleOnWednesday()
        {
            // Opened Sun 2026-02-15, cooling=3 BD
            // Sun(1), Mon(2), Tue(3) → eligible Wed 2026-02-18
            var wedUtc = new DateTime(2026, 2, 18, 12, 0, 0, DateTimeKind.Utc);
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, wedUtc));
        }

        [Fact]
        public void CoolingEligible_SundayOpen_3BD_NotYetOnTuesday()
        {
            // Opened Sun 2026-02-15, eligible from Wed 2026-02-18
            // "Now" = Tue 2026-02-17 → NOT eligible yet
            var tueUtc = new DateTime(2026, 2, 17, 12, 0, 0, DateTimeKind.Utc);
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.False(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, tueUtc));
        }

        [Fact]
        public void CoolingEligible_FridayOpen_3BD_EligibleWednesday()
        {
            // Opened Fri 2026-02-13, first BD = Sun
            // Sun(1), Mon(2), Tue(3) → eligible Wed 2026-02-18
            var wedUtc = new DateTime(2026, 2, 18, 12, 0, 0, DateTimeKind.Utc);
            var tsCreateDate = new DateTime(2026, 2, 13);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, wedUtc));
        }

        [Fact]
        public void CoolingEligible_OldCase_WellPastCooling_Eligible()
        {
            // Opened 2026-02-05 (Thursday), well past any cooling period
            var tsCreateDate = new DateTime(2026, 2, 5);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void CoolingEligible_NullCreateDate_NotEligible()
        {
            Assert.False(SyncService.IsCoolingEligible(null, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void CoolingEligible_ZeroDays_Immediate()
        {
            // Cooling disabled: case created today should be eligible
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.True(SyncService.IsCoolingEligible(tsCreateDate, coolingPeriodDays: 0, UtcNow));
        }

        [Fact]
        public void CoolingEligible_ZeroDays_NullCreateDate_NotEligible()
        {
            Assert.False(SyncService.IsCoolingEligible(null, coolingPeriodDays: 0, UtcNow));
        }

        // ====================================================================
        // Bootstrap eligibility (cutoff + Israeli BD cooling)
        // ====================================================================

        [Fact]
        public void Bootstrap_PreCutoff_NeverOnboarded()
        {
            var tsCreateDate = new DateTime(2026, 1, 15);
            Assert.False(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void Bootstrap_PostCutoff_TooRecent_NotOnboarded()
        {
            // Opened Sun 2026-02-15 (today), post-cutoff but within cooling
            var tsCreateDate = new DateTime(2026, 2, 15);
            Assert.False(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void Bootstrap_PostCutoff_Cooled_Onboarded()
        {
            // Opened Thu 2026-02-05, well past cooling
            var tsCreateDate = new DateTime(2026, 2, 5);
            Assert.True(SyncService.IsBootstrapEligible(tsCreateDate, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        [Fact]
        public void Bootstrap_NullCreateDate_NeverEligible()
        {
            Assert.False(SyncService.IsBootstrapEligible(null, CutoffDate, DefaultCoolingDays, UtcNow));
        }

        // ====================================================================
        // Already-mapped cases are NOT affected by cooling
        // ====================================================================

        [Fact]
        public void AlreadyMappedCase_NotAffectedByCooling()
        {
            var eligibleFromOdcanit = new HashSet<int> { 50001, 50002 };
            var mapped = new HashSet<int> { 50001 };

            var eligibleMapped = new HashSet<int>(eligibleFromOdcanit);
            eligibleMapped.IntersectWith(mapped);

            Assert.Contains(50001, eligibleMapped);
            Assert.DoesNotContain(50002, eligibleMapped);
        }

        // ====================================================================
        // Batch filtering simulation with Israeli business days
        // ====================================================================

        [Fact]
        public void BatchFiltering_IsraeliBD_CoolingApplied()
        {
            // UtcNow = Sun 2026-02-15
            // Case 1001: opened Thu 2026-02-05 → eligible from Mon 2026-02-09 → YES
            // Case 1002: opened Thu 2026-02-12 → eligible from Tue 2026-02-17 → NO (today is Sun)
            // Case 1003: opened Sun 2026-02-15 → eligible from Wed 2026-02-18 → NO
            // Case 1004: opened Mon 2026-02-09 → eligible from Thu 2026-02-12 → YES
            // Case 1005: null date → NO
            var cases = new List<OdcanitCase>
            {
                MakeCase(1001, new DateTime(2026, 2, 5)),
                MakeCase(1002, new DateTime(2026, 2, 12)),
                MakeCase(1003, new DateTime(2026, 2, 15)),
                MakeCase(1004, new DateTime(2026, 2, 9)),
                MakeCase(1005, null),
            };

            var eligible = cases
                .Where(c => SyncService.IsCoolingEligible(c.tsCreateDate, DefaultCoolingDays, UtcNow))
                .Select(c => c.TikCounter)
                .ToList();

            Assert.Equal(2, eligible.Count);
            Assert.Contains(1001, eligible);
            Assert.Contains(1004, eligible);
            Assert.DoesNotContain(1002, eligible);
            Assert.DoesNotContain(1003, eligible);
            Assert.DoesNotContain(1005, eligible);
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
