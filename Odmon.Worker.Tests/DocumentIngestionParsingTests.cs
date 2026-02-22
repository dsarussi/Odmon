using System.Text.Json;
using Xunit;
using Odmon.Worker.Services;

namespace Odmon.Worker.Tests
{
    public class DocumentIngestionParsingTests
    {
        // ── ParseLinkedItemIds ──────────────────────────────────────────

        [Fact]
        public void ParseLinkedItemIds_BoardRelationValue_ReturnsLinkedIds()
        {
            var json = """
            {
                "id": "board_relation_mkzenscq",
                "value": null,
                "linked_item_ids": ["2732400533", "9999"]
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = DocumentIngestionMondayService.ParseLinkedItemIds(doc.RootElement);

            Assert.Equal(2, result.Count);
            Assert.Equal(2732400533L, result[0]);
            Assert.Equal(9999L, result[1]);
        }

        [Fact]
        public void ParseLinkedItemIds_NullLinkedIds_FallsBackToValueJson()
        {
            var json = """
            {
                "id": "board_relation_mkzenscq",
                "value": "{\"linkedPulseIds\":[{\"linkedPulseId\":12345}]}"
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = DocumentIngestionMondayService.ParseLinkedItemIds(doc.RootElement);

            Assert.Single(result);
            Assert.Equal(12345L, result[0]);
        }

        [Fact]
        public void ParseLinkedItemIds_EmptyLinkedIds_FallsBackToValueJson()
        {
            var json = """
            {
                "id": "board_relation_mkzenscq",
                "linked_item_ids": [],
                "value": "{\"linkedPulseIds\":[{\"linkedPulseId\":777}]}"
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = DocumentIngestionMondayService.ParseLinkedItemIds(doc.RootElement);

            Assert.Single(result);
            Assert.Equal(777L, result[0]);
        }

        [Fact]
        public void ParseLinkedItemIds_NullValueAndNoLinkedIds_ReturnsEmpty()
        {
            var json = """
            {
                "id": "board_relation_mkzenscq",
                "value": null
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = DocumentIngestionMondayService.ParseLinkedItemIds(doc.RootElement);

            Assert.Empty(result);
        }

        [Fact]
        public void ParseLinkedItemIds_NumericLinkedIds_Parsed()
        {
            var json = """
            {
                "id": "board_relation_mkzenscq",
                "linked_item_ids": [2732400533]
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = DocumentIngestionMondayService.ParseLinkedItemIds(doc.RootElement);

            Assert.Single(result);
            Assert.Equal(2732400533L, result[0]);
        }

        // ── ResolveFileExtension (legacy compat) ─────────────────────────

        [Fact]
        public void ResolveFileExtension_NormalJpg_ReturnsJpg()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("testimg.jpg", null, null);
            Assert.Equal("jpg", ext);
        }

        [Fact]
        public void ResolveFileExtension_JwtLikeNameEndingWithPdf_ReturnsPdf()
        {
            var jwtName = "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJodHRwczovL2FwcC5tb25kYXkuY29tIiwic3ViIjoiMTIzNDU2Nzg5MCJ9.Ypg2yDCq7dXkv6kV.pdf";
            var ext = DocumentIngestionService.ResolveFileExtension(jwtName, null, null);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void ResolveFileExtension_ManyDotsEndingPng_ReturnsPng()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("file.name.with.many.dots.png", null, null);
            Assert.Equal("png", ext);
        }

        [Fact]
        public void ResolveFileExtension_UpperCase_NormalizesToLower()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("Photo.JPEG", null, null);
            Assert.Equal("jpeg", ext);
        }

        [Fact]
        public void ResolveFileExtension_NoExtension_UsesFileExtField()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("noextension", "pdf", null);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void ResolveFileExtension_NoExtension_FileExtWithDot()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("noextension", ".png", null);
            Assert.Equal("png", ext);
        }

        [Fact]
        public void ResolveFileExtension_NoExtensionNoFallback_ReturnsEmpty()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("noextension", null, null);
            Assert.Equal("", ext);
        }

        [Fact]
        public void ResolveFileExtension_EmptyFallback_ReturnsEmpty()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("noextension", "", null);
            Assert.Equal("", ext);
        }

        // ── ResolveFileExtension (with MIME fallback) ────────────────────

        [Fact]
        public void ResolveFileExtension_NameWithDot_ReturnsExtension()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("report.pdf", null, null);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void ResolveFileExtension_NameNoDot_UsesFileExtension()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("jwt-token-no-dot", "pdf", null);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void ResolveFileExtension_NameNoDot_NoFileExt_UsesMime()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("jwt-token-no-dot", null, "application/pdf");
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void ResolveFileExtension_MimeImageJpeg_ReturnsJpg()
        {
            var ext = DocumentIngestionService.ResolveFileExtension(null, null, "image/jpeg");
            Assert.Equal("jpg", ext);
        }

        [Fact]
        public void ResolveFileExtension_MimeImagePng_ReturnsPng()
        {
            var ext = DocumentIngestionService.ResolveFileExtension(null, null, "image/png");
            Assert.Equal("png", ext);
        }

        [Fact]
        public void ResolveFileExtension_UnknownMime_ReturnsEmpty()
        {
            var ext = DocumentIngestionService.ResolveFileExtension(null, null, "application/octet-stream");
            Assert.Equal("", ext);
        }

        [Fact]
        public void ResolveFileExtension_AllNull_ReturnsEmpty()
        {
            var ext = DocumentIngestionService.ResolveFileExtension(null, null, null);
            Assert.Equal("", ext);
        }

        [Fact]
        public void ResolveFileExtension_NamePrioritizedOverMime()
        {
            var ext = DocumentIngestionService.ResolveFileExtension("photo.png", null, "image/jpeg");
            Assert.Equal("png", ext);
        }

        // ── GenerateSafeFileName ─────────────────────────────────────────

        [Fact]
        public void GenerateSafeFileName_StandardInput_ProducesExpectedFormat()
        {
            var name = DocumentIngestionService.GenerateSafeFileName("1/11958", 198847023, "file_mm0qwtat", "pdf");
            Assert.Equal("1_11958_198847023_file_mm0qwtat.pdf", name);
        }

        [Fact]
        public void GenerateSafeFileName_BackslashInTik_Sanitized()
        {
            var name = DocumentIngestionService.GenerateSafeFileName("1\\11958", 123, "col", "jpg");
            Assert.Equal("1_11958_123_col.jpg", name);
        }

        [Fact]
        public void GenerateSafeFileName_NoSlashInTik_Unchanged()
        {
            var name = DocumentIngestionService.GenerateSafeFileName("99", 42, "file_abc", "png");
            Assert.Equal("99_42_file_abc.png", name);
        }

        // ── SanitizeTikVisualID ──────────────────────────────────────────

        [Fact]
        public void SanitizeTikVisualID_ForwardSlash_Replaced()
        {
            Assert.Equal("1_11958", DocumentIngestionService.SanitizeTikVisualID("1/11958"));
        }

        [Fact]
        public void SanitizeTikVisualID_Backslash_Replaced()
        {
            Assert.Equal("1_11958", DocumentIngestionService.SanitizeTikVisualID("1\\11958"));
        }

        // ── IsSuspiciousFilename / SanitizeFilename / DeriveSafeFilename / SafeFileNameForLog ───

        [Fact]
        public void IsSuspiciousFilename_NullOrEmpty_ReturnsTrue()
        {
            Assert.True(DocumentIngestionService.IsSuspiciousFilename(null));
            Assert.True(DocumentIngestionService.IsSuspiciousFilename(""));
            Assert.True(DocumentIngestionService.IsSuspiciousFilename("   "));
        }

        [Fact]
        public void IsSuspiciousFilename_StartsWithEyJ_ReturnsTrue()
        {
            Assert.True(DocumentIngestionService.IsSuspiciousFilename("eyJ0eXAiOiJKV1QiLCJhbGc.pdf"));
            Assert.True(DocumentIngestionService.IsSuspiciousFilename("eyJ..."));
        }

        [Fact]
        public void IsSuspiciousFilename_NormalName_ReturnsFalse()
        {
            Assert.False(DocumentIngestionService.IsSuspiciousFilename("report.pdf"));
            Assert.False(DocumentIngestionService.IsSuspiciousFilename("Photo.jpg"));
        }

        [Fact]
        public void DeriveSafeFilename_Suspicious_ReturnsAttachmentPattern()
        {
            var name = DocumentIngestionService.DeriveSafeFilename("eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJodHRwczovL2FwcC5tb25kYXkuY29tIiwic3ViIjoiMTIzNDU2Nzg5MCJ9.Ypg2yDCq7dXkv6kV.pdf", "1/11958", 39283, 198847023, "pdf");
            Assert.Equal("Attachment_1_11958_198847023.pdf", name);
        }

        [Fact]
        public void DeriveSafeFilename_NormalName_ReturnsSanitizedWithExtension()
        {
            var name = DocumentIngestionService.DeriveSafeFilename("My Report.pdf", "1/11958", 39283, 123, "pdf");
            Assert.Equal("My Report.pdf", name);
        }

        [Fact]
        public void SafeFileNameForLog_ShortName_ReturnsLenAndPrefix()
        {
            var log = DocumentIngestionService.SafeFileNameForLog("report.pdf");
            Assert.StartsWith("len=10 prefix=", log);
            Assert.Contains("report", log);
        }

        [Fact]
        public void SafeFileNameForLog_LongName_TruncatesPrefix()
        {
            var log = DocumentIngestionService.SafeFileNameForLog("eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJpc3M");
            Assert.StartsWith("len=", log);
            Assert.Contains("eyJ0eXAi...", log);
        }

        // ── GetAllowedExtension ─────────────────────────────────────────

        private static readonly string[] Allowlist = ["pdf", "jpg", "jpeg", "png"];

        [Fact]
        public void GetAllowedExtension_OriginalFileNameEndsWithPdf_ReturnsPdf()
        {
            var ext = DocumentIngestionService.GetAllowedExtension("report.pdf", null, null, null, Allowlist);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void GetAllowedExtension_OriginalFileNameJwtLikeWithPdf_ReturnsPdf()
        {
            var ext = DocumentIngestionService.GetAllowedExtension("eyJhbGciOiJIUzI1NiJ9.eyJpc3M.pdf", null, null, null, Allowlist);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void GetAllowedExtension_OriginalFileNameJwtLikeNoExtension_ReturnsNull()
        {
            var ext = DocumentIngestionService.GetAllowedExtension("eyJhbGciOiJIUzI1NiJ9.eyJpc3MiOiJodHRw", null, null, null, Allowlist);
            Assert.Null(ext);
        }

        [Fact]
        public void GetAllowedExtension_UrlEndsWithJpg_ReturnsJpg()
        {
            var ext = DocumentIngestionService.GetAllowedExtension(null, null, "https://example.com/files/photo.jpg", null, Allowlist);
            Assert.Equal("jpg", ext);
        }

        [Fact]
        public void GetAllowedExtension_UrlHasQueryString_ExtensionFromPathOnly()
        {
            var ext = DocumentIngestionService.GetAllowedExtension(null, null, "https://example.com/file.pdf?token=eyJzdWI", null, Allowlist);
            Assert.Equal("pdf", ext);
        }

        [Fact]
        public void GetAllowedExtension_NoExtension_ReturnsNull()
        {
            var ext = DocumentIngestionService.GetAllowedExtension(null, null, "https://example.com/noext", null, Allowlist);
            Assert.Null(ext);
        }

        [Fact]
        public void GetAllowedExtension_AssetFileExtensionJunk_IgnoredWhenNotInAllowlist()
        {
            var ext = DocumentIngestionService.GetAllowedExtension("eyJ.eyJ.eyJ.longbase64string", "eyjzdwjtaxn", null, null, Allowlist);
            Assert.Null(ext);
        }

        // ── SafeFileName (columnSlug_tikSlug_assetId.ext) ─────────────────

        [Fact]
        public void SafeFileName_StandardInput_ProducesExpectedFormat()
        {
            var name = DocumentIngestionService.SafeFileName("file_mm0qwtat", "1/11958", 198847023, "pdf");
            Assert.Equal("file_mm0qwtat_1-11958_198847023.pdf", name);
        }

        [Fact]
        public void SafeFileName_ColumnWithSpaces_Slugified()
        {
            var name = DocumentIngestionService.SafeFileName("file column", "1/11958", 123, "jpg");
            Assert.Equal("file_column_1-11958_123.jpg", name);
        }

        // ── IsSmtpAuthFailure ────────────────────────────────────────────

        [Fact]
        public void IsSmtpAuthFailure_570Response_ReturnsTrue()
        {
            var ex = new System.Net.Mail.SmtpException(
                System.Net.Mail.SmtpStatusCode.MustIssueStartTlsFirst,
                "5.7.0 Authentication Required");
            Assert.True(EmailNotifier.IsSmtpAuthFailure(ex));
        }

        [Fact]
        public void IsSmtpAuthFailure_TransientFailure_ReturnsFalse()
        {
            var ex = new System.Net.Mail.SmtpException(
                System.Net.Mail.SmtpStatusCode.ServiceNotAvailable,
                "Service not available, try again later");
            Assert.False(EmailNotifier.IsSmtpAuthFailure(ex));
        }
    }
}
