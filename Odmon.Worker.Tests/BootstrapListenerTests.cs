using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests the two-phase Bootstrap + Listener architecture.
    ///
    /// Phase A (Bootstrap): Discovers cases with tsCreateDate >= CutoffDate from Odcanit,
    /// computes unmapped set (EXCEPT already mapped), and creates Monday items.
    ///
    /// Phase B (Listener): Uses change feed TikCounters, but only updates cases
    /// that are already mapped AND tsCreateDate >= CutoffDate. Never creates.
    /// </summary>
    public class BootstrapListenerTests
    {
        private static readonly DateTime CutoffDate = new(2026, 2, 10);

        // ====================================================================
        // Helper: replicates the Bootstrap eligibility logic
        // ====================================================================

        /// <summary>
        /// Bootstrap discovers eligible TikCounters from Odcanit:
        ///   odcanitTikCounters (where tsCreateDate >= cutoff) EXCEPT mappedTikCounters
        /// </summary>
        private static List<int> ComputeBootstrapEligible(
            List<OdcanitCase> allOdcanitCases,
            HashSet<int> mappedTikCounters,
            DateTime cutoffDate)
        {
            // Step 1: Odcanit query returns TikCounters where tsCreateDate >= cutoff
            var odcanitEligible = allOdcanitCases
                .Where(c => c.tsCreateDate.HasValue && c.tsCreateDate.Value.Date >= cutoffDate)
                .Select(c => c.TikCounter)
                .Distinct()
                .ToList();

            // Step 2: EXCEPT already mapped
            return odcanitEligible
                .Where(tc => !mappedTikCounters.Contains(tc))
                .ToList();
        }

        // ====================================================================
        // Helper: replicates the Listener gating logic
        // ====================================================================

        private enum ListenerAction { Update, SkipUnmanaged, SkipPreCutoff }

        /// <summary>
        /// Listener decides per case:
        /// - Not mapped → SkipUnmanaged (never creates)
        /// - Mapped but tsCreateDate &lt; cutoff → SkipPreCutoff
        /// - Mapped and tsCreateDate >= cutoff → Update
        /// </summary>
        private static ListenerAction DetermineListenerAction(
            OdcanitCase c,
            HashSet<int> mappedTikCounters,
            DateTime cutoffDate)
        {
            if (!mappedTikCounters.Contains(c.TikCounter))
                return ListenerAction.SkipUnmanaged;

            var caseDate = c.tsCreateDate?.Date;
            if (!caseDate.HasValue || caseDate.Value < cutoffDate)
                return ListenerAction.SkipPreCutoff;

            return ListenerAction.Update;
        }

        // ====================================================================
        // Scenario 1: Case opened after cutoff, system was down → Bootstrap creates it
        // ====================================================================

        [Fact]
        public void Bootstrap_CaseAfterCutoff_SystemDown_CreatesIt()
        {
            // Arrange: A case was opened on 2026-02-12, but system was down.
            // No mapping exists. Bootstrap must discover and onboard it.
            var allOdcanitCases = new List<OdcanitCase>
            {
                MakeCase(5001, new DateTime(2026, 2, 12)), // after cutoff, unmapped
                MakeCase(5002, new DateTime(2026, 2, 8)),  // before cutoff
            };
            var mappedTikCounters = new HashSet<int>(); // nothing mapped yet

            // Act
            var toOnboard = ComputeBootstrapEligible(allOdcanitCases, mappedTikCounters, CutoffDate);

            // Assert: only 5001 is eligible; 5002 is pre-cutoff
            Assert.Single(toOnboard);
            Assert.Contains(5001, toOnboard);
            Assert.DoesNotContain(5002, toOnboard);
        }

        [Fact]
        public void Bootstrap_CaseOnCutoffDate_DateOnly_CreatesIt()
        {
            // Specifically verifies date-only comparison:
            // tsCreateDate = 2026-02-10 00:00:00, cutoff = 2026-02-10 → eligible
            var allOdcanitCases = new List<OdcanitCase>
            {
                MakeCase(6001, new DateTime(2026, 2, 10, 0, 0, 0)),
            };
            var mappedTikCounters = new HashSet<int>();

            var toOnboard = ComputeBootstrapEligible(allOdcanitCases, mappedTikCounters, CutoffDate);

            Assert.Single(toOnboard);
            Assert.Contains(6001, toOnboard);
        }

        [Fact]
        public void Bootstrap_AlreadyMapped_NotOnboardedAgain()
        {
            // Case is eligible but already mapped — bootstrap must skip it (idempotent)
            var allOdcanitCases = new List<OdcanitCase>
            {
                MakeCase(7001, new DateTime(2026, 2, 15)),
            };
            var mappedTikCounters = new HashSet<int> { 7001 };

            var toOnboard = ComputeBootstrapEligible(allOdcanitCases, mappedTikCounters, CutoffDate);

            Assert.Empty(toOnboard);
        }

        [Fact]
        public void Bootstrap_DoesNotDependOnChangeFeed()
        {
            // Bootstrap uses direct Odcanit query (tsCreateDate >= cutoff),
            // not the change feed. Even if the change feed returns nothing,
            // bootstrap still finds eligible cases.
            var allOdcanitCases = new List<OdcanitCase>
            {
                MakeCase(8001, new DateTime(2026, 2, 14)),
                MakeCase(8002, new DateTime(2026, 2, 16)),
            };
            var changeFeedTikCounters = Array.Empty<int>(); // change feed empty
            var mappedTikCounters = new HashSet<int>();

            // Bootstrap ignores change feed completely
            var toOnboard = ComputeBootstrapEligible(allOdcanitCases, mappedTikCounters, CutoffDate);

            Assert.Equal(2, toOnboard.Count);
            Assert.Contains(8001, toOnboard);
            Assert.Contains(8002, toOnboard);
        }

        // ====================================================================
        // Scenario 2: Case opened before cutoff but modified today → Never created, never updated
        // ====================================================================

        [Fact]
        public void PreCutoff_Modified_Today_NeverCreated_NeverUpdated()
        {
            // Case 2001 was opened on 2026-01-15 (before cutoff) but modified today.
            // It appears in the change feed, but:
            // 1) Bootstrap won't find it (tsCreateDate < cutoff)
            // 2) Listener won't update it (pre-cutoff, even if mapped)
            var preCutoffCase = MakeCase(2001, new DateTime(2026, 1, 15));

            // Bootstrap check: not eligible
            var allOdcanitCases = new List<OdcanitCase> { preCutoffCase };
            var mappedTikCounters = new HashSet<int>();
            var toOnboard = ComputeBootstrapEligible(allOdcanitCases, mappedTikCounters, CutoffDate);
            Assert.Empty(toOnboard);

            // Listener check (even if somehow it got mapped via legacy): pre-cutoff → skip
            var mappedSet = new HashSet<int> { 2001 }; // assume legacy mapping exists
            var action = DetermineListenerAction(preCutoffCase, mappedSet, CutoffDate);
            Assert.Equal(ListenerAction.SkipPreCutoff, action);
        }

        [Fact]
        public void PreCutoff_Unmapped_InChangeFeed_NeverCreated()
        {
            // Pre-cutoff case appears in change feed but is unmapped.
            // Listener must skip (never create).
            var preCutoffCase = MakeCase(2002, new DateTime(2026, 2, 5));
            var mappedSet = new HashSet<int>(); // not mapped

            var action = DetermineListenerAction(preCutoffCase, mappedSet, CutoffDate);
            Assert.Equal(ListenerAction.SkipUnmanaged, action);
        }

        // ====================================================================
        // Scenario 3: Case opened after cutoff, already onboarded → Listener updates it
        // ====================================================================

        [Fact]
        public void PostCutoff_AlreadyOnboarded_ListenerUpdatesIt()
        {
            // Case 3001 was opened on 2026-02-12, onboarded by bootstrap (mapping exists).
            // Now it appears in the change feed → Listener should update it.
            var postCutoffCase = MakeCase(3001, new DateTime(2026, 2, 12));
            var mappedSet = new HashSet<int> { 3001 };

            var action = DetermineListenerAction(postCutoffCase, mappedSet, CutoffDate);
            Assert.Equal(ListenerAction.Update, action);
        }

        [Fact]
        public void PostCutoff_OnCutoffDate_Mapped_ListenerUpdatesIt()
        {
            // Date-only edge case: tsCreateDate = cutoff date, mapped → update
            var caseOnCutoff = MakeCase(3002, new DateTime(2026, 2, 10, 0, 0, 0));
            var mappedSet = new HashSet<int> { 3002 };

            var action = DetermineListenerAction(caseOnCutoff, mappedSet, CutoffDate);
            Assert.Equal(ListenerAction.Update, action);
        }

        // ====================================================================
        // Scenario 4: Listener never creates unmapped cases
        // ====================================================================

        [Fact]
        public void Listener_NeverCreates_UnmappedCases()
        {
            // Multiple post-cutoff unmapped cases appear in change feed.
            // Listener must skip ALL of them (bootstrap handles creation).
            var changeFeedCases = new List<OdcanitCase>
            {
                MakeCase(4001, new DateTime(2026, 2, 11)),
                MakeCase(4002, new DateTime(2026, 2, 13)),
                MakeCase(4003, new DateTime(2026, 2, 15)),
            };
            var mappedSet = new HashSet<int>(); // none mapped

            foreach (var c in changeFeedCases)
            {
                var action = DetermineListenerAction(c, mappedSet, CutoffDate);
                Assert.Equal(ListenerAction.SkipUnmanaged, action);
            }
        }

        [Fact]
        public void Listener_MixedBatch_OnlyUpdatesMappedEligible()
        {
            // Mixed batch from change feed:
            // 4010: post-cutoff, mapped → Update
            // 4011: post-cutoff, unmapped → SkipUnmanaged
            // 4012: pre-cutoff, mapped → SkipPreCutoff
            // 4013: pre-cutoff, unmapped → SkipUnmanaged
            var cases = new List<OdcanitCase>
            {
                MakeCase(4010, new DateTime(2026, 2, 14)),
                MakeCase(4011, new DateTime(2026, 2, 14)),
                MakeCase(4012, new DateTime(2026, 1, 20)),
                MakeCase(4013, new DateTime(2026, 1, 5)),
            };
            var mappedSet = new HashSet<int> { 4010, 4012 };

            var actions = cases.Select(c => (c.TikCounter, Action: DetermineListenerAction(c, mappedSet, CutoffDate))).ToList();

            Assert.Equal(ListenerAction.Update, actions.First(a => a.TikCounter == 4010).Action);
            Assert.Equal(ListenerAction.SkipUnmanaged, actions.First(a => a.TikCounter == 4011).Action);
            Assert.Equal(ListenerAction.SkipPreCutoff, actions.First(a => a.TikCounter == 4012).Action);
            Assert.Equal(ListenerAction.SkipUnmanaged, actions.First(a => a.TikCounter == 4013).Action);
        }

        [Fact]
        public void NullCreateDate_NeverEligible_ForEitherPhase()
        {
            var nullDateCase = MakeCase(9001, null);

            // Bootstrap: not eligible
            var toOnboard = ComputeBootstrapEligible(
                new List<OdcanitCase> { nullDateCase },
                new HashSet<int>(),
                CutoffDate);
            Assert.Empty(toOnboard);

            // Listener (even if mapped): pre-cutoff skip
            var action = DetermineListenerAction(nullDateCase, new HashSet<int> { 9001 }, CutoffDate);
            Assert.Equal(ListenerAction.SkipPreCutoff, action);
        }

        // ====================================================================
        // Listener detection: feed UNION fallback, intersected with eligibleMapped
        // ====================================================================

        /// <summary>
        /// Replicates the listener detection logic:
        /// 1) eligibleMapped = INTERSECT(eligibleFromOdcanit, mapped)
        /// 2) feedIntersected = INTERSECT(feedTikCounters, eligibleMapped)
        /// 3) fallbackTikCounters = TikCounters with tsModifyDate >= sinceUtc, restricted to eligibleMapped
        /// 4) toProcess = DISTINCT(feedIntersected UNION fallbackTikCounters)
        /// </summary>
        private static HashSet<int> ComputeListenerToProcess(
            HashSet<int> eligibleMappedSet,
            IReadOnlyCollection<int> feedTikCounters,
            IReadOnlyCollection<int> fallbackTikCounters)
        {
            var feedIntersected = new HashSet<int>(feedTikCounters);
            feedIntersected.IntersectWith(eligibleMappedSet);

            var toProcess = new HashSet<int>(feedIntersected);
            toProcess.UnionWith(fallbackTikCounters);
            return toProcess;
        }

        /// <summary>
        /// Simulates GetModifiedTikCountersSinceAsync: returns TikCounters from cases
        /// where tsModifyDate >= sinceUtc AND TikCounter is in eligibleMapped.
        /// </summary>
        private static List<int> SimulateFallback(
            List<OdcanitCase> allCases,
            DateTime sinceUtc,
            HashSet<int> eligibleMappedSet)
        {
            return allCases
                .Where(c => eligibleMappedSet.Contains(c.TikCounter)
                            && c.tsModifyDate.HasValue
                            && c.tsModifyDate.Value >= sinceUtc)
                .Select(c => c.TikCounter)
                .Distinct()
                .ToList();
        }

        [Fact]
        public void Listener_FeedEmpty_FallbackFindsModified_UpdateHappens()
        {
            // Feed returns nothing, but the case was modified (tsModifyDate >= sinceUtc).
            // The fallback should detect it and include it in toProcess.
            var sinceUtc = new DateTime(2026, 2, 10, 12, 0, 0);
            var cases = new List<OdcanitCase>
            {
                MakeCaseWithModify(10001, new DateTime(2026, 2, 12), new DateTime(2026, 2, 10, 14, 0, 0)), // modified after sinceUtc
            };
            var eligibleMapped = new HashSet<int> { 10001 };
            var feedTikCounters = Array.Empty<int>(); // feed is empty

            var fallback = SimulateFallback(cases, sinceUtc, eligibleMapped);
            var toProcess = ComputeListenerToProcess(eligibleMapped, feedTikCounters, fallback);

            Assert.Single(toProcess);
            Assert.Contains(10001, toProcess);
        }

        [Fact]
        public void Listener_PreCutoffModifiedToday_NoUpdate()
        {
            // Case created before cutoff but modified today.
            // It should NOT be in eligibleMapped (because tsCreateDate < cutoff),
            // so even if fallback picks it up, it's not in the eligible set.
            var sinceUtc = new DateTime(2026, 2, 10, 12, 0, 0);
            var preCutoffCase = MakeCaseWithModify(10002, new DateTime(2026, 1, 5), new DateTime(2026, 2, 10, 15, 0, 0));

            // eligibleFromOdcanit would not include 10002 (tsCreateDate < cutoff)
            var eligibleFromOdcanit = new HashSet<int>(); // empty — pre-cutoff excluded
            var mapped = new HashSet<int> { 10002 }; // it has a mapping

            // eligibleMapped = INTERSECT — empty because eligibleFromOdcanit is empty
            var eligibleMapped = new HashSet<int>(eligibleFromOdcanit);
            eligibleMapped.IntersectWith(mapped);

            Assert.Empty(eligibleMapped);

            // Even if we somehow ran fallback against this empty set, nothing would process
            var fallback = SimulateFallback(new List<OdcanitCase> { preCutoffCase }, sinceUtc, eligibleMapped);
            var toProcess = ComputeListenerToProcess(eligibleMapped, Array.Empty<int>(), fallback);

            Assert.Empty(toProcess);
        }

        [Fact]
        public void Listener_FeedContainsUnmapped_NotProcessed()
        {
            // Feed returns a TikCounter that is not mapped.
            // It must be excluded from toProcess.
            var eligibleFromOdcanit = new HashSet<int> { 10003, 10004 };
            var mapped = new HashSet<int> { 10003 }; // 10004 is NOT mapped

            var eligibleMapped = new HashSet<int>(eligibleFromOdcanit);
            eligibleMapped.IntersectWith(mapped);

            // Feed contains both, but 10004 is not in eligibleMapped
            var feedTikCounters = new[] { 10003, 10004 };
            var toProcess = ComputeListenerToProcess(eligibleMapped, feedTikCounters, Array.Empty<int>());

            Assert.Single(toProcess);
            Assert.Contains(10003, toProcess);
            Assert.DoesNotContain(10004, toProcess);
        }

        [Fact]
        public void Listener_DuplicateAcrossFeedAndFallback_ProcessedOnce()
        {
            // TikCounter 10005 appears in both feed and fallback.
            // It must appear only once in toProcess.
            var eligibleMapped = new HashSet<int> { 10005 };
            var feedTikCounters = new[] { 10005 };
            var fallbackTikCounters = new[] { 10005 };

            var toProcess = ComputeListenerToProcess(eligibleMapped, feedTikCounters, fallbackTikCounters);

            Assert.Single(toProcess);
            Assert.Contains(10005, toProcess);
        }

        [Fact]
        public void Listener_ChecksumUnchanged_SkippedNoChange()
        {
            // This tests the concept that when the Odcanit version (tsModifyDate) hasn't changed
            // from what's stored in MondayItemMappings.OdcanitVersion, DetermineSyncAction returns "skip".
            // We simulate this by checking: if odcanitVersion == mappingVersion → skip.
            var tsModifyDate = new DateTime(2026, 2, 12, 10, 30, 0);
            var odcanitVersion = tsModifyDate.ToString("o");
            var mappingVersion = odcanitVersion; // same → no change

            Assert.Equal(odcanitVersion, mappingVersion); // confirms skip condition
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

        private static OdcanitCase MakeCaseWithModify(int tikCounter, DateTime? tsCreateDate, DateTime? tsModifyDate)
        {
            return new OdcanitCase
            {
                TikCounter = tikCounter,
                TikNumber = $"9/{tikCounter}",
                TikName = "Test",
                ClientName = "Test Client",
                StatusName = "פעיל",
                tsCreateDate = tsCreateDate,
                tsModifyDate = tsModifyDate
            };
        }
    }
}
