using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Odmon.Worker.Voicenter;
using Odmon.Worker.Workers;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class DailySummaryRenderingTests
    {
        [Fact]
        public void DocumentIngestionColumns_RenderInCorrectOrder()
        {
            var html = Render(
                docs: new[]
                {
                    Doc("5/123", "file_col", "claim.pdf", "bad data", retryCount: 2)
                });

            Assert.True(
                html.IndexOf("TikNumber / TikVisualID", StringComparison.Ordinal) <
                html.IndexOf("ColumnId", StringComparison.Ordinal));
            Assert.True(
                html.IndexOf("ColumnId", StringComparison.Ordinal) <
                html.IndexOf("SampleFileNames", StringComparison.Ordinal));
            Assert.True(
                html.IndexOf("SampleFileNames", StringComparison.Ordinal) <
                html.IndexOf("RetryCount", StringComparison.Ordinal));
            Assert.Contains("<td style='border:1px solid #ddd;'>5/123</td>", html);
            Assert.Contains("<td style='border:1px solid #ddd;'>file_col</td>", html);
            Assert.Contains("<td style='border:1px solid #ddd;'>claim.pdf</td>", html);
        }

        [Fact]
        public void MissingTikNumberAndFileName_RenderAsNA()
        {
            var html = Render(
                docs: new[]
                {
                    Doc(null, "file_col", "", "missing context")
                });

            Assert.Contains("<td style='border:1px solid #ddd;'>N/A</td>", html);
            Assert.Contains("missing context", html);
        }

        [Fact]
        public void RepeatedTaskTimeouts_AreGroupedAsKnownStaleFailures()
        {
            var docs = Enumerable.Range(0, 12)
                .Select(i => Doc(
                    "5/123",
                    "file_mm1bvngc",
                    "",
                    "Timeout: 2h passed since status ready, no file",
                    updatedAtUtc: new DateTime(2026, 6, 1, 8, i, 0)))
                .ToArray();

            var html = Render(docs: docs);

            Assert.Contains("Known/Stale document ingestion failures", html);
            Assert.Contains("<td style='border:1px solid #ddd;'>12</td>", html);
            Assert.DoesNotContain("Actionable document ingestion failures</h4>", html);
        }

        [Fact]
        public void Client21_IsVisibleAsKnownDataIssue_NotSuppressed()
        {
            var html = Render(
                docs: new[]
                {
                    Doc("21/999", "file_col", "bad.docx", "Invalid email address")
                });

            Assert.Contains("Known data issues", html);
            Assert.Contains("21/999", html);
            Assert.Contains("Invalid email address", html);
        }

        [Fact]
        public void KnownBlockedTikVisualId_IsVisibleInGroupedSection()
        {
            var html = Render(
                docs: new[]
                {
                    Doc("23/121", "file_col", "blocked.pdf", "Blocked by business process")
                },
                options: new EmailBackgroundService.DailySummaryRenderOptions(
                    new[] { "23/121" },
                    400));

            Assert.Contains("Known blocked cases", html);
            Assert.Contains("23/121", html);
            Assert.Contains("Blocked by business process", html);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        public void VoicenterHardLimitMissingOrZero_DoesNotRenderZeroDenominator(int? hardLimit)
        {
            var html = Render(
                weeklyDetailReq: 59,
                options: new EmailBackgroundService.DailySummaryRenderOptions(
                    Array.Empty<string>(),
                    hardLimit));

            Assert.DoesNotContain("59 / 0", html);
            Assert.Contains("59 / not configured", html);
            Assert.Contains("WeeklyUsageHardLimit is missing or zero", html);
        }

        [Fact]
        public void MondayApiExceptionGroup_IncludesUsefulMessageBodyDetails()
        {
            var html = Render(
                failures: new[]
                {
                    new EmailBackgroundService.DailySummaryFailureGroup(
                        "5/123",
                        "update",
                        "MondayApiException",
                        "Monday API failed: HTTP 400 body={\"errors\":[{\"message\":\"Column value invalid\"}]}",
                        3,
                        new DateTime(2026, 6, 1, 8, 0, 0),
                        new DateTime(2026, 6, 1, 9, 0, 0))
                });

            Assert.Contains("MondayApiException", html);
            Assert.Contains("Column value invalid", html);
            Assert.Contains("body=", html);
            Assert.Contains("5/123", html);
        }

        [Fact]
        public void SampleDailySummaryHtml_WritesVisualReviewArtifact()
        {
            var docs = Enumerable.Range(0, 12)
                .Select(i => Doc(
                    "5/123",
                    "file_mm1bvngc",
                    "",
                    "Timeout: 2h passed since status ready, no file",
                    retryCount: 3,
                    updatedAtUtc: new DateTime(2026, 6, 15, 9, i, 0)))
                .Concat(new[]
                {
                    Doc("23/121", "file_mkzr2cmr", "blocked-case.pdf", "Blocked by business process until source data is corrected", retryCount: 1, updatedAtUtc: new DateTime(2026, 6, 15, 10, 0, 0)),
                    Doc("21/987", "file_mkyet713", "bad-email.docx", "Invalid client email address: not-an-email", retryCount: 2, updatedAtUtc: new DateTime(2026, 6, 15, 11, 0, 0)),
                    Doc(null, "file_general", "", "Monday asset download failed with HTTP 500", retryCount: 4, updatedAtUtc: new DateTime(2026, 6, 15, 12, 0, 0))
                })
                .ToList();

            var failures = new[]
            {
                new EmailBackgroundService.DailySummaryFailureGroup(
                    "5/123",
                    "update",
                    "MondayApiException",
                    "Monday API failed: HTTP 400 body={\"errors\":[{\"message\":\"Column value invalid for status column\"}]}",
                    2,
                    new DateTime(2026, 6, 15, 8, 15, 0),
                    new DateTime(2026, 6, 15, 9, 45, 0))
            };

            var html = Render(
                docs: docs,
                failures: failures,
                weeklyDetailReq: 59,
                options: new EmailBackgroundService.DailySummaryRenderOptions(
                    new[] { "23/121" },
                    400));

            var repoRoot = FindRepoRoot();
            var outDir = Path.Combine(repoRoot, "artifacts");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "DailySummarySample.html"), html);

            Assert.Contains("59 / 400", html);
            Assert.DoesNotContain("59 / 0", html);
            Assert.Contains("Known/Stale document ingestion failures", html);
            Assert.Contains("Known blocked cases", html);
            Assert.Contains("Known data issues", html);
            Assert.Contains("Column value invalid for status column", html);
        }

        [Fact]
        public void KnownAndSkippedFailures_RenderOutsideIssuesRequiringAttention()
        {
            var html = Render(
                knownDataIssues: new[]
                {
                    new EmailBackgroundService.DailySummaryFailureGroup(
                        "21/100",
                        "update",
                        "KnownBlockedClient",
                        "Client 21 has no explicit DocumentType business rule.",
                        2,
                        new DateTime(2026, 6, 1, 8, 0, 0),
                        new DateTime(2026, 6, 1, 9, 0, 0))
                },
                skippedExpected: new[]
                {
                    new EmailBackgroundService.DailySummaryFailureGroup(
                        "23/159",
                        "netcourt_skipped_missing_routing",
                        "SkippedMissingRouting",
                        "No recipient mapping for client number 23.",
                        3,
                        new DateTime(2026, 6, 1, 8, 0, 0),
                        new DateTime(2026, 6, 1, 9, 0, 0))
                });

            Assert.Contains("Known Data Issues", html);
            Assert.Contains("KnownBlockedClient", html);
            Assert.Contains("Skipped Expected", html);
            Assert.Contains("SkippedMissingRouting", html);
            Assert.Contains("Issues Requiring Attention</h3>\r\n<p>None.</p>", html);
        }

        [Fact]
        public void InvalidUserFileAndHttp503_RenderInSeparateDocumentBuckets()
        {
            var html = Render(
                docs: new[]
                {
                    Doc("5/1", "file_col", "bad.zip", "DENYLIST_EXTENSION; zip"),
                    Doc("5/2", "file_col", "asset.pdf", "Monday asset download failed with HTTP 503")
                });

            Assert.Contains("Invalid user file document ingestion failures", html);
            Assert.Contains("External transient document ingestion failures", html);
            Assert.DoesNotContain("Actionable document ingestion failures</h4>", html);
        }

        [Fact]
        public void FailureClassifier_ExcludesKnownAndSkippedFromRealFailures()
        {
            Assert.False(FailureClassifier.IsRealFailure("KnownBlockedClient", "update"));
            Assert.False(FailureClassifier.IsRealFailure("SkippedMissingRouting", "netcourt_skipped_missing_routing"));
            Assert.True(FailureClassifier.IsRealFailure("MappingIntegrityMismatch", "update"));
            Assert.True(FailureClassifier.IsRealFailure("MondayApiException", "update"));
        }

        private static string Render(
            IEnumerable<MondayDocumentImport>? docs = null,
            IEnumerable<EmailBackgroundService.DailySummaryFailureGroup>? failures = null,
            IEnumerable<EmailBackgroundService.DailySummaryFailureGroup>? knownDataIssues = null,
            IEnumerable<EmailBackgroundService.DailySummaryFailureGroup>? skippedExpected = null,
            int weeklyDetailReq = 0,
            EmailBackgroundService.DailySummaryRenderOptions? options = null)
        {
            return EmailBackgroundService.BuildDailySummaryHtml(
                new DateOnly(2026, 6, 1),
                casesCreatedCount: 0,
                hearingsSyncedCount: 0,
                itemsUpdatedCount: 0,
                realFailureCount: failures?.Count() ?? 0,
                groupedFailures: failures?.ToList() ?? new List<EmailBackgroundService.DailySummaryFailureGroup>(),
                docIngestionSucceeded: 0,
                docIngestionFailures: docs?.ToList() ?? new List<MondayDocumentImport>(),
                voicenterWritten: 0,
                voicenterFailed: new List<NispahWriteLog>(),
                lastVcResult: new VoicenterRunResult(),
                weeklyDetailReq: weeklyDetailReq,
                quotaExceededRecently: false,
                circuitBreakerTripped: false,
                highFailureNote: false,
                options: options ?? new EmailBackgroundService.DailySummaryRenderOptions(Array.Empty<string>(), 400),
                knownDataIssueFailures: knownDataIssues?.ToList(),
                skippedExpectedFailures: skippedExpected?.ToList());
        }

        private static MondayDocumentImport Doc(
            string? tikVisualId,
            string columnId,
            string fileName,
            string error,
            int retryCount = 0,
            DateTime? updatedAtUtc = null)
        {
            var updated = updatedAtUtc ?? new DateTime(2026, 6, 1, 10, 0, 0);
            return new MondayDocumentImport
            {
                TikVisualID = tikVisualId,
                ColumnId = columnId,
                OriginalFileName = fileName,
                ErrorMessage = error,
                RetryCount = retryCount,
                CreatedAtUtc = updated.AddHours(-1),
                UpdatedAtUtc = updated,
                Status = DocumentImportStatus.Failed
            };
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Odmon.Worker.csproj")))
                dir = dir.Parent;

            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }
}
