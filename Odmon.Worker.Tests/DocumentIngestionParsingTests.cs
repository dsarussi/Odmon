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
