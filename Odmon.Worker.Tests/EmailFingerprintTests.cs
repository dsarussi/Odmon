using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for email alert fingerprinting, normalization, dedup, and rate limiting.
    /// </summary>
    public class EmailFingerprintTests
    {
        // ================================================================
        // ComputeFingerprint — deterministic hashing
        // ================================================================

        [Fact]
        public void ComputeFingerprint_SameInputs_SameHash()
        {
            var fp1 = EmailNotifier.ComputeFingerprint("NullReferenceException", "Object reference not set", "SyncService");
            var fp2 = EmailNotifier.ComputeFingerprint("NullReferenceException", "Object reference not set", "SyncService");
            Assert.Equal(fp1, fp2);
        }

        [Fact]
        public void ComputeFingerprint_DifferentExceptionType_DifferentHash()
        {
            var fp1 = EmailNotifier.ComputeFingerprint("NullReferenceException", "Object reference not set", "SyncService");
            var fp2 = EmailNotifier.ComputeFingerprint("ArgumentException", "Object reference not set", "SyncService");
            Assert.NotEqual(fp1, fp2);
        }

        [Fact]
        public void ComputeFingerprint_DifferentSource_DifferentHash()
        {
            var fp1 = EmailNotifier.ComputeFingerprint("NullReferenceException", "error", "SyncService");
            var fp2 = EmailNotifier.ComputeFingerprint("NullReferenceException", "error", "Bootstrap");
            Assert.NotEqual(fp1, fp2);
        }

        [Fact]
        public void ComputeFingerprint_NullInputs_DoesNotThrow()
        {
            var fp = EmailNotifier.ComputeFingerprint(null, null, null);
            Assert.NotNull(fp);
            Assert.Equal(64, fp.Length); // SHA-256 hex string
        }

        [Fact]
        public void ComputeFingerprint_ReturnsLowercaseHex()
        {
            var fp = EmailNotifier.ComputeFingerprint("Test", "msg", "src");
            Assert.Matches("^[a-f0-9]{64}$", fp);
        }

        // ================================================================
        // NormalizeForFingerprint — strips volatile parts
        // ================================================================

        [Fact]
        public void Normalize_StripsRunId()
        {
            var input = "Error in RunId=abc123def456 during sync";
            var result = EmailNotifier.NormalizeForFingerprint(input);
            Assert.Contains("runid=<id>", result);
            Assert.DoesNotContain("abc123def456", result);
        }

        [Fact]
        public void Normalize_StripsLineNumbers()
        {
            var input = "at SyncService.cs:line 789";
            var result = EmailNotifier.NormalizeForFingerprint(input);
            Assert.Contains(":line <n>", result);
            Assert.DoesNotContain("789", result);
        }

        [Fact]
        public void Normalize_StripsNumericIds()
        {
            var input = "Failed for TikCounter=12345";
            var result = EmailNotifier.NormalizeForFingerprint(input);
            Assert.Contains("tikcounter=<n>", result);
            Assert.DoesNotContain("12345", result);
        }

        [Fact]
        public void Normalize_PreservesShortNumbers()
        {
            // Numbers less than 3 digits should not be stripped (e.g., error codes)
            var input = "HTTP status 42";
            var result = EmailNotifier.NormalizeForFingerprint(input);
            Assert.Contains("42", result);
        }

        [Fact]
        public void Normalize_NullAndEmpty_ReturnEmpty()
        {
            Assert.Equal(string.Empty, EmailNotifier.NormalizeForFingerprint(null));
            Assert.Equal(string.Empty, EmailNotifier.NormalizeForFingerprint(""));
            Assert.Equal(string.Empty, EmailNotifier.NormalizeForFingerprint("   "));
        }

        [Fact]
        public void Normalize_SameErrorDifferentRunId_SameOutput()
        {
            var a = EmailNotifier.NormalizeForFingerprint("RunId=aaa111bbb222 failed for TikCounter=50001");
            var b = EmailNotifier.NormalizeForFingerprint("RunId=ccc333ddd444 failed for TikCounter=60002");
            Assert.Equal(a, b);
        }

        // ================================================================
        // IsDuplicate — dedup window logic
        // ================================================================

        [Fact]
        public void IsDuplicate_FirstOccurrence_NotDuplicate()
        {
            var notifier = CreateNotifier(dedupWindowMinutes: 60);
            var fp = EmailNotifier.ComputeFingerprint("TestEx", "test message", "test");
            Assert.False(notifier.IsDuplicate(fp));
        }

        [Fact]
        public void IsDuplicate_SecondOccurrenceWithinWindow_AfterSend_IsDuplicate()
        {
            var notifier = CreateNotifier(dedupWindowMinutes: 60);
            var fp = EmailNotifier.ComputeFingerprint("TestEx", "test message", "test");

            // First call: not duplicate
            Assert.False(notifier.IsDuplicate(fp));
            // Record that we "sent" an email for this fingerprint
            notifier.RecordSent(fp);
            // Second call within window: IS duplicate
            Assert.True(notifier.IsDuplicate(fp));
        }

        [Fact]
        public void IsDuplicate_SecondOccurrence_NoSendRecorded_NotDuplicate()
        {
            var notifier = CreateNotifier(dedupWindowMinutes: 60);
            var fp = EmailNotifier.ComputeFingerprint("TestEx", "test message", "test");

            // First call: not duplicate (creates entry with LastEmailSentUtc=null)
            Assert.False(notifier.IsDuplicate(fp));
            // Second call: LastEmailSentUtc is still null, so window check returns false
            Assert.False(notifier.IsDuplicate(fp));
        }

        [Fact]
        public void IsDuplicate_DifferentFingerprints_IndependentTracking()
        {
            var notifier = CreateNotifier(dedupWindowMinutes: 60);
            var fp1 = EmailNotifier.ComputeFingerprint("Type1", "msg1", "src");
            var fp2 = EmailNotifier.ComputeFingerprint("Type2", "msg2", "src");

            Assert.False(notifier.IsDuplicate(fp1));
            notifier.RecordSent(fp1);

            // fp2 is independent — should not be affected by fp1
            Assert.False(notifier.IsDuplicate(fp2));
        }

        // ================================================================
        // IsRateLimited — hourly cap
        // ================================================================

        [Fact]
        public void IsRateLimited_BelowCap_NotLimited()
        {
            var notifier = CreateNotifier(maxPerHour: 10);
            Assert.False(notifier.IsRateLimited());
        }

        [Fact]
        public void IsRateLimited_AtCap_IsLimited()
        {
            var notifier = CreateNotifier(maxPerHour: 3);

            // Simulate 3 sends (at the cap)
            for (int i = 0; i < 3; i++)
            {
                var fp = $"fp{i}";
                notifier.RecordSent(fp); // adds to _sentTimestamps
            }

            Assert.True(notifier.IsRateLimited());
        }

        [Fact]
        public void IsRateLimited_ZeroCap_NeverLimited()
        {
            var notifier = CreateNotifier(maxPerHour: 0);
            Assert.False(notifier.IsRateLimited());
        }

        // ================================================================
        // Suppressed alert tracking
        // ================================================================

        [Fact]
        public void GetSuppressedAlerts_NoSuppressed_EmptyList()
        {
            var notifier = CreateNotifier();
            var alerts = notifier.GetSuppressedAlerts();
            Assert.Empty(alerts);
        }

        [Fact]
        public void IncrementSuppressed_CountsCorrectly()
        {
            var notifier = CreateNotifier(dedupWindowMinutes: 60);
            var fp = EmailNotifier.ComputeFingerprint("TestEx", "msg", "src");

            // Create the entry
            notifier.IsDuplicate(fp);
            notifier.RecordSent(fp);

            // Suppress it twice
            notifier.IncrementSuppressed(fp);
            notifier.IncrementSuppressed(fp);

            var alerts = notifier.GetSuppressedAlerts();
            Assert.Single(alerts);
            Assert.Equal(2, alerts[0].SuppressedCount);
        }

        [Fact]
        public void ClearSuppressedCounts_ResetsAll()
        {
            var notifier = CreateNotifier(dedupWindowMinutes: 60);
            var fp = EmailNotifier.ComputeFingerprint("TestEx", "msg", "src");

            notifier.IsDuplicate(fp);
            notifier.RecordSent(fp);
            notifier.IncrementSuppressed(fp);

            Assert.Single(notifier.GetSuppressedAlerts());

            notifier.ClearSuppressedCounts();
            Assert.Empty(notifier.GetSuppressedAlerts());
        }

        // ================================================================
        // Helper
        // ================================================================

        private static EmailNotifier CreateNotifier(int dedupWindowMinutes = 60, int maxPerHour = 10)
        {
            var configData = new Dictionary<string, string?>
            {
                ["Email:Enabled"] = "true",
                ["Email:DedupWindowMinutes"] = dedupWindowMinutes.ToString(),
                ["Email:MaxEmailsPerHour"] = maxPerHour.ToString(),
            };
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            return new EmailNotifier(
                NullLogger<EmailNotifier>.Instance,
                config);
        }
    }
}
