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
    }
}
