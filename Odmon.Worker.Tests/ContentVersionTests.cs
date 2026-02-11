using System;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for the deterministic content-based versioning (SHA-256 checksum)
    /// that replaced the unreliable tsModifyDate-based OdcanitVersion.
    /// </summary>
    public class ContentVersionTests
    {
        // ====================================================================
        // 1. Determinism: identical data produces identical hash
        // ====================================================================

        [Fact]
        public void IdenticalCases_ProduceSameHash()
        {
            var a = MakeCase();
            var b = MakeCase();

            var hashA = SyncService.ComputeContentVersion(a);
            var hashB = SyncService.ComputeContentVersion(b);

            Assert.Equal(hashA, hashB);
        }

        // ====================================================================
        // 2. Field change without tsModifyDate update triggers different hash
        // ====================================================================

        [Fact]
        public void FieldChange_WithoutTsModifyDateChange_ProducesDifferentHash()
        {
            var original = MakeCase();
            original.tsModifyDate = new DateTime(2026, 2, 10, 10, 0, 0);

            var modified = MakeCase();
            modified.tsModifyDate = original.tsModifyDate; // same tsModifyDate
            modified.DefendantName = "Changed Defendant"; // field changed

            var hashOriginal = SyncService.ComputeContentVersion(original);
            var hashModified = SyncService.ComputeContentVersion(modified);

            Assert.NotEqual(hashOriginal, hashModified);
        }

        [Fact]
        public void DefendantSideRawChange_DetectedEvenIfTsModifyDateNull()
        {
            var original = MakeCase();
            original.tsModifyDate = null;
            original.DefendantSideRaw = "נתבע";

            var modified = MakeCase();
            modified.tsModifyDate = null; // still null
            modified.DefendantSideRaw = "נתבעת"; // changed

            var hashOriginal = SyncService.ComputeContentVersion(original);
            var hashModified = SyncService.ComputeContentVersion(modified);

            Assert.NotEqual(hashOriginal, hashModified);
        }

        [Fact]
        public void AmountChange_DetectedWithoutTsModifyDate()
        {
            var original = MakeCase();
            original.tsModifyDate = null;
            original.DirectDamageAmount = 10000m;

            var modified = MakeCase();
            modified.tsModifyDate = null;
            modified.DirectDamageAmount = 15000m;

            Assert.NotEqual(
                SyncService.ComputeContentVersion(original),
                SyncService.ComputeContentVersion(modified));
        }

        // ====================================================================
        // 3. NULL vs empty does NOT cause flapping
        // ====================================================================

        [Fact]
        public void NullVsEmpty_String_NoFlapping()
        {
            var withNull = MakeCase();
            withNull.DefendantName = null;

            var withEmpty = MakeCase();
            withEmpty.DefendantName = "";

            var withWhitespace = MakeCase();
            withWhitespace.DefendantName = "   ";

            // null and empty both normalize to "" -> same hash
            Assert.Equal(
                SyncService.ComputeContentVersion(withNull),
                SyncService.ComputeContentVersion(withEmpty));

            // Whitespace-only trims to "" -> same hash
            Assert.Equal(
                SyncService.ComputeContentVersion(withNull),
                SyncService.ComputeContentVersion(withWhitespace));
        }

        [Fact]
        public void NullVsEmpty_Decimal_NoFlapping()
        {
            var withNull = MakeCase();
            withNull.RequestedClaimAmount = null;

            var withZero = MakeCase();
            withZero.RequestedClaimAmount = 0m;

            // null decimal -> "" in hash, 0 decimal -> "0" in hash
            // These SHOULD differ (null means "no value" vs 0 means "zero amount")
            // This is correct behavior, not flapping
            var hashNull = SyncService.ComputeContentVersion(withNull);
            var hashZero = SyncService.ComputeContentVersion(withZero);
            Assert.NotEqual(hashNull, hashZero);
        }

        [Fact]
        public void NullVsEmpty_Date_NoFlapping()
        {
            var withNull = MakeCase();
            withNull.EventDate = null;

            var withDate = MakeCase();
            withDate.EventDate = new DateTime(2026, 1, 1);

            // Different values produce different hashes (correct)
            Assert.NotEqual(
                SyncService.ComputeContentVersion(withNull),
                SyncService.ComputeContentVersion(withDate));
        }

        // ====================================================================
        // 4. Phone normalization prevents false updates
        // ====================================================================

        [Fact]
        public void PhoneFormatDifferences_AfterNormalization_NoFalseUpdate()
        {
            // Different phone representations that normalize to the same digits
            var caseA = MakeCase();
            caseA.PolicyHolderPhone = "972541234567";

            var caseB = MakeCase();
            caseB.PolicyHolderPhone = "0541234567"; // local format

            // Both normalize to 0541234567 -> same hash
            Assert.Equal(
                SyncService.ComputeContentVersion(caseA),
                SyncService.ComputeContentVersion(caseB));
        }

        [Fact]
        public void PhoneFormatDifferences_DifferentNumbers_Detected()
        {
            var caseA = MakeCase();
            caseA.PolicyHolderPhone = "0541234567";

            var caseB = MakeCase();
            caseB.PolicyHolderPhone = "0549876543";

            Assert.NotEqual(
                SyncService.ComputeContentVersion(caseA),
                SyncService.ComputeContentVersion(caseB));
        }

        // ====================================================================
        // 5. Hash is SHA-256 hex (64 chars, lowercase)
        // ====================================================================

        [Fact]
        public void Hash_IsSha256HexLowercase()
        {
            var c = MakeCase();
            var hash = SyncService.ComputeContentVersion(c);

            Assert.Equal(64, hash.Length);
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }

        // ====================================================================
        // 6. Hearing field changes detected
        // ====================================================================

        [Fact]
        public void HearingDateChange_Detected()
        {
            var original = MakeCase();
            original.HearingDate = new DateTime(2026, 3, 1);

            var modified = MakeCase();
            modified.HearingDate = new DateTime(2026, 3, 15);

            Assert.NotEqual(
                SyncService.ComputeContentVersion(original),
                SyncService.ComputeContentVersion(modified));
        }

        [Fact]
        public void HearingJudgeChange_Detected()
        {
            var original = MakeCase();
            original.HearingJudgeName = "Judge A";

            var modified = MakeCase();
            modified.HearingJudgeName = "Judge B";

            Assert.NotEqual(
                SyncService.ComputeContentVersion(original),
                SyncService.ComputeContentVersion(modified));
        }

        // ====================================================================
        // Helper
        // ====================================================================

        private static OdcanitCase MakeCase()
        {
            return new OdcanitCase
            {
                TikCounter = 10001,
                TikNumber = "9/10001",
                TikName = "Test Case",
                ClientName = "Test Client",
                StatusName = "פעיל",
                tsCreateDate = new DateTime(2026, 2, 15),
                DocumentType = "כתב תביעה"
            };
        }
    }
}
