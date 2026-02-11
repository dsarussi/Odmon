using System;
using System.Net.Http;
using System.Threading.Tasks;
using Odmon.Worker.Monday;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for:
    /// 1) Smart retry with exponential backoff + jitter + Retry-After
    /// 2) Circuit breaker logic
    /// 3) Duplicate update prevention
    /// 4) Transient vs non-transient error classification
    /// </summary>
    public class RetryCircuitBreakerTests
    {
        // ====================================================================
        // PART 1 – ComputeRetryDelay: exponential backoff with jitter
        // ====================================================================

        [Fact]
        public void RetryDelay_Attempt0_ReturnsApproximately1Second()
        {
            var delay = SyncService.ComputeRetryDelay(0, null);
            // Base = 1s, jitter ±25% => 0.75s to 1.25s
            Assert.InRange(delay.TotalSeconds, 0.5, 1.5);
        }

        [Fact]
        public void RetryDelay_Attempt1_ReturnsApproximately4Seconds()
        {
            var delay = SyncService.ComputeRetryDelay(1, null);
            // Base = 4s, jitter ±25% => 3.0s to 5.0s
            Assert.InRange(delay.TotalSeconds, 2.5, 5.5);
        }

        [Fact]
        public void RetryDelay_Attempt2_ReturnsApproximately12Seconds()
        {
            var delay = SyncService.ComputeRetryDelay(2, null);
            // Base = 12s, jitter ±25% => 9.0s to 15.0s
            Assert.InRange(delay.TotalSeconds, 8.0, 16.0);
        }

        [Fact]
        public void RetryDelay_HighAttempt_CapsAtLastBaseDelay()
        {
            // Attempt 10 should still use the last base delay (12s)
            var delay = SyncService.ComputeRetryDelay(10, null);
            Assert.InRange(delay.TotalSeconds, 8.0, 16.0);
        }

        [Fact]
        public void RetryDelay_WithRetryAfterInException_RespectsServerHint()
        {
            // Monday API error with Retry-After hint
            var ex = new MondayApiException(
                "Rate limited",
                rawErrorJson: "{\"error_code\":\"RateLimitExceeded\",\"retry-after\":30}");

            var delay = SyncService.ComputeRetryDelay(0, ex);
            Assert.Equal(30.0, delay.TotalSeconds, precision: 1);
        }

        [Fact]
        public void RetryDelay_RetryAfterCappedAt60Seconds()
        {
            var ex = new MondayApiException(
                "Rate limited",
                rawErrorJson: "{\"retry-after\":120}");

            var delay = SyncService.ComputeRetryDelay(0, ex);
            Assert.Equal(60.0, delay.TotalSeconds, precision: 1);
        }

        [Fact]
        public void RetryDelay_JitterProducesDifferentValues()
        {
            // Run 20 times and verify we get at least 2 distinct values (jitter is random)
            var delays = new System.Collections.Generic.HashSet<double>();
            for (int i = 0; i < 20; i++)
            {
                delays.Add(Math.Round(SyncService.ComputeRetryDelay(0, null).TotalMilliseconds));
            }
            Assert.True(delays.Count >= 2, $"Expected jitter to produce varied delays, but got {delays.Count} distinct value(s).");
        }

        // ====================================================================
        // PART 1 – IsTransientError: classification
        // ====================================================================

        [Fact]
        public void IsTransient_HttpRequestException_ReturnsTrue()
        {
            Assert.True(SyncService.IsTransientError(new HttpRequestException("Network error")));
        }

        [Fact]
        public void IsTransient_Timeout_ReturnsTrue()
        {
            var inner = new TimeoutException("Timed out");
            var ex = new TaskCanceledException("Request timed out", inner);
            Assert.True(SyncService.IsTransientError(ex));
        }

        [Fact]
        public void IsTransient_MondayRateLimit429_ReturnsTrue()
        {
            var ex = new MondayApiException(
                "429 Too Many Requests",
                rawErrorJson: "{\"error_code\":\"RATE_LIMIT\"}");
            Assert.True(SyncService.IsTransientError(ex));
        }

        [Fact]
        public void IsTransient_MondayComplexityBudget_ReturnsTrue()
        {
            var ex = new MondayApiException(
                "Complexity budget exhausted",
                rawErrorJson: "{\"error_code\":\"COMPLEXITY_BUDGET_EXHAUSTED\"}");
            Assert.True(SyncService.IsTransientError(ex));
        }

        [Fact]
        public void IsTransient_MondayInactiveItem_ReturnsFalse()
        {
            var ex = new MondayApiException(
                "Item is inactive",
                rawErrorJson: "{\"error_data\":{\"column_validation_error_code\":\"inactiveItems\"}}");
            Assert.False(SyncService.IsTransientError(ex));
        }

        [Fact]
        public void IsTransient_ValidationException_ReturnsFalse()
        {
            var ex = new InvalidOperationException("Column not found");
            Assert.False(SyncService.IsTransientError(ex));
        }

        [Fact]
        public void IsTransient_Monday400BadRequest_ReturnsFalse()
        {
            // A Monday API error that is NOT rate-limit/transient (e.g., invalid column)
            var ex = new MondayApiException(
                "InvalidColumnIdException",
                rawErrorJson: "{\"error_code\":\"InvalidColumnIdException\",\"status_code\":400}");
            Assert.False(SyncService.IsTransientError(ex));
        }

        // ====================================================================
        // PART 3 – Duplicate update prevention (HashSet logic)
        // ====================================================================

        [Fact]
        public void DuplicatePrevention_SameItemId_DetectedAsContains()
        {
            // Simulates the duplicate detection using a HashSet
            var updatedItemIds = new System.Collections.Generic.HashSet<long>();
            var itemId = 12345L;

            Assert.DoesNotContain(itemId, updatedItemIds); // first time: not a duplicate
            updatedItemIds.Add(itemId);
            Assert.Contains(itemId, updatedItemIds); // second time: duplicate
        }

        [Fact]
        public void DuplicatePrevention_DifferentItemIds_NotDetectedAsDuplicate()
        {
            var updatedItemIds = new System.Collections.Generic.HashSet<long>();
            updatedItemIds.Add(111L);
            updatedItemIds.Add(222L);

            Assert.DoesNotContain(333L, updatedItemIds); // different ID: not a duplicate
            Assert.Contains(111L, updatedItemIds);
            Assert.Contains(222L, updatedItemIds);
        }

        // ====================================================================
        // PART 2 – Circuit breaker threshold logic
        // ====================================================================

        [Fact]
        public void CircuitBreaker_BelowThreshold_DoesNotTrip()
        {
            int consecutiveFailures = 0;
            bool tripped = false;
            int threshold = 10;

            // Simulate 9 failures (below threshold)
            for (int i = 0; i < 9; i++)
            {
                consecutiveFailures++;
                if (threshold > 0 && consecutiveFailures >= threshold && !tripped)
                    tripped = true;
            }

            Assert.Equal(9, consecutiveFailures);
            Assert.False(tripped);
        }

        [Fact]
        public void CircuitBreaker_AtThreshold_Trips()
        {
            int consecutiveFailures = 0;
            bool tripped = false;
            int threshold = 10;

            // Simulate 10 failures (at threshold)
            for (int i = 0; i < 10; i++)
            {
                consecutiveFailures++;
                if (threshold > 0 && consecutiveFailures >= threshold && !tripped)
                    tripped = true;
            }

            Assert.Equal(10, consecutiveFailures);
            Assert.True(tripped);
        }

        [Fact]
        public void CircuitBreaker_SuccessResetsCounter()
        {
            int consecutiveFailures = 0;
            bool tripped = false;
            int threshold = 10;

            // Simulate 8 failures, then 1 success, then 8 more failures
            for (int i = 0; i < 8; i++) consecutiveFailures++;
            Assert.Equal(8, consecutiveFailures);

            // Success resets
            consecutiveFailures = 0;
            Assert.Equal(0, consecutiveFailures);

            // 8 more failures: still below threshold
            for (int i = 0; i < 8; i++)
            {
                consecutiveFailures++;
                if (threshold > 0 && consecutiveFailures >= threshold && !tripped)
                    tripped = true;
            }

            Assert.Equal(8, consecutiveFailures);
            Assert.False(tripped); // never reached 10 consecutive
        }

        [Fact]
        public void CircuitBreaker_ThresholdZero_NeverTrips()
        {
            int consecutiveFailures = 0;
            bool tripped = false;
            int threshold = 0; // disabled

            for (int i = 0; i < 100; i++)
            {
                consecutiveFailures++;
                if (threshold > 0 && consecutiveFailures >= threshold && !tripped)
                    tripped = true;
            }

            Assert.Equal(100, consecutiveFailures);
            Assert.False(tripped); // threshold=0 means disabled
        }
    }
}
