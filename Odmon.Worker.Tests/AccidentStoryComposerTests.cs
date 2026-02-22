using Odmon.Worker.Configuration;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class AccidentStoryComposerTests
    {
        private static readonly DateTime FixedTimestamp = new(2026, 2, 16, 14, 30, 0);

        private static readonly AccidentStoryColumnDef[] StandardColumns =
        [
            new() { ColumnId = "long_text_mkyes1qb", Title = "אנא פרט בדיוק מה קרה בתאונה", Type = "long_text" },
            new() { ColumnId = "color_mkyemecs", Title = "האם היית לבד ברכב?", Type = "status" },
            new() { ColumnId = "color_mkye16gd", Title = "האם היו עדי ראיה?", Type = "status" },
            new() { ColumnId = "color_mkyefd7b", Title = "האם יש מצלמת דרך?", Type = "status" },
            new() { ColumnId = "color_mkyeajzk", Title = "האם נפתח תיק משטרה?", Type = "status" }
        ];

        // ── Full composition with all values ─────────────────────────────

        [Fact]
        public void Compose_AllColumnsPopulated_ProducesFormattedText()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "נסעתי בכביש 4 ורכב פגע בי מאחור",
                ["color_mkyemecs"] = "כן",
                ["color_mkye16gd"] = "לא",
                ["color_mkyefd7b"] = "כן",
                ["color_mkyeajzk"] = "לא"
            };

            var result = AccidentStoryComposer.Compose(StandardColumns, values, 12345, FixedTimestamp);

            Assert.NotNull(result);
            Assert.Equal(5, result.LinesIncluded);
            Assert.Contains("סיפור תאונה (מקור: שאלון Monday, ItemId=12345, תאריך=16/02/2026 14:30)", result.Text);
            Assert.Contains("- אנא פרט בדיוק מה קרה בתאונה: נסעתי בכביש 4 ורכב פגע בי מאחור", result.Text);
            Assert.Contains("- האם היית לבד ברכב?: כן", result.Text);
            Assert.Contains("- האם היו עדי ראיה?: לא", result.Text);
            Assert.Contains("- האם יש מצלמת דרך?: כן", result.Text);
            Assert.Contains("- האם נפתח תיק משטרה?: לא", result.Text);
        }

        [Fact]
        public void Compose_IncludeHeaderFalse_ContainsOnlyBulletLines()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "נסעתי בכביש 4",
                ["color_mkyemecs"] = "כן"
            };
            var result = AccidentStoryComposer.Compose(StandardColumns, values, 12345, FixedTimestamp, includeHeader: false);
            Assert.NotNull(result);
            Assert.DoesNotContain("ItemId=", result.Text);
            Assert.DoesNotContain("תאריך=", result.Text);
            Assert.DoesNotContain("מקור: שאלון Monday", result.Text);
            Assert.Contains("- אנא פרט בדיוק מה קרה בתאונה: נסעתי בכביש 4", result.Text);
            Assert.Contains("- האם היית לבד ברכב?: כן", result.Text);
        }

        // ── Skip empty values by default ─────────────────────────────────

        [Fact]
        public void Compose_EmptyStatusColumn_SkippedByDefault()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "פגיעה אחורית",
                ["color_mkyemecs"] = "",
                ["color_mkye16gd"] = "לא"
            };

            var result = AccidentStoryComposer.Compose(StandardColumns, values, 99, FixedTimestamp);

            Assert.NotNull(result);
            Assert.Equal(2, result.LinesIncluded);
            Assert.DoesNotContain("האם היית לבד ברכב?", result.Text);
            Assert.Contains("- אנא פרט בדיוק מה קרה בתאונה: פגיעה אחורית", result.Text);
            Assert.Contains("- האם היו עדי ראיה?: לא", result.Text);
        }

        // ── IncludeIfEmpty shows placeholder ─────────────────────────────

        [Fact]
        public void Compose_IncludeIfEmpty_ShowsPlaceholder()
        {
            var columns = new AccidentStoryColumnDef[]
            {
                new() { ColumnId = "col1", Title = "שאלה חשובה", Type = "status", IncludeIfEmpty = true },
                new() { ColumnId = "col2", Title = "שאלה אחרת", Type = "text" }
            };

            var values = new Dictionary<string, string>
            {
                ["col1"] = "",
                ["col2"] = "תשובה"
            };

            var result = AccidentStoryComposer.Compose(columns, values, 1, FixedTimestamp);

            Assert.NotNull(result);
            Assert.Equal(2, result.LinesIncluded);
            Assert.Contains("- שאלה חשובה: (ריק)", result.Text);
            Assert.Contains("- שאלה אחרת: תשובה", result.Text);
        }

        // ── All values empty returns null ─────────────────────────────────

        [Fact]
        public void Compose_AllEmpty_ReturnsNull()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "",
                ["color_mkyemecs"] = "",
                ["color_mkye16gd"] = ""
            };

            var result = AccidentStoryComposer.Compose(StandardColumns, values, 1, FixedTimestamp);

            Assert.Null(result);
        }

        [Fact]
        public void Compose_MissingColumnValues_ReturnsNull()
        {
            var values = new Dictionary<string, string>();
            var result = AccidentStoryComposer.Compose(StandardColumns, values, 1, FixedTimestamp);
            Assert.Null(result);
        }

        // ── Deterministic output ─────────────────────────────────────────

        [Fact]
        public void Compose_SameInputs_ProducesSameHash()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "פגיעה",
                ["color_mkyemecs"] = "כן"
            };

            var result1 = AccidentStoryComposer.Compose(StandardColumns, values, 42, FixedTimestamp);
            var result2 = AccidentStoryComposer.Compose(StandardColumns, values, 42, FixedTimestamp);

            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.Equal(result1.ContentHash, result2.ContentHash);
            Assert.Equal(result1.Text, result2.Text);
        }

        [Fact]
        public void Compose_DifferentInputs_ProduceDifferentHash()
        {
            var values1 = new Dictionary<string, string> { ["long_text_mkyes1qb"] = "פגיעה אחורית" };
            var values2 = new Dictionary<string, string> { ["long_text_mkyes1qb"] = "פגיעה קדמית" };

            var result1 = AccidentStoryComposer.Compose(StandardColumns, values1, 42, FixedTimestamp);
            var result2 = AccidentStoryComposer.Compose(StandardColumns, values2, 42, FixedTimestamp);

            Assert.NotNull(result1);
            Assert.NotNull(result2);
            Assert.NotEqual(result1.ContentHash, result2.ContentHash);
        }

        // ── Whitespace-only values treated as empty ──────────────────────

        [Fact]
        public void Compose_WhitespaceOnlyValues_TreatedAsEmpty()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "   ",
                ["color_mkyemecs"] = "\t"
            };

            var result = AccidentStoryComposer.Compose(StandardColumns, values, 1, FixedTimestamp);
            Assert.Null(result);
        }

        // ── Long text with newlines preserved ────────────────────────────

        [Fact]
        public void Compose_LongTextWithNewlines_PreservedInOutput()
        {
            var values = new Dictionary<string, string>
            {
                ["long_text_mkyes1qb"] = "שורה ראשונה\nשורה שנייה\nשורה שלישית"
            };

            var result = AccidentStoryComposer.Compose(StandardColumns, values, 1, FixedTimestamp);

            Assert.NotNull(result);
            Assert.Contains("שורה ראשונה\nשורה שנייה\nשורה שלישית", result.Text);
        }

        // ── Content hash is valid SHA-256 hex ────────────────────────────

        [Fact]
        public void Compose_ContentHash_IsValidSha256Hex()
        {
            var values = new Dictionary<string, string> { ["long_text_mkyes1qb"] = "test" };
            var result = AccidentStoryComposer.Compose(StandardColumns, values, 1, FixedTimestamp);

            Assert.NotNull(result);
            Assert.Equal(64, result.ContentHash.Length);
            Assert.Matches("^[0-9a-f]{64}$", result.ContentHash);
        }

        // ── Single column backward compat ────────────────────────────────

        [Fact]
        public void Compose_SingleColumn_ProducesValidOutput()
        {
            var singleCol = new AccidentStoryColumnDef[]
            {
                new() { ColumnId = "long_text_abc", Title = "סיפור תאונה", Type = "long_text" }
            };

            var values = new Dictionary<string, string>
            {
                ["long_text_abc"] = "היה פיצוץ בכביש 2"
            };

            var result = AccidentStoryComposer.Compose(singleCol, values, 777, FixedTimestamp);

            Assert.NotNull(result);
            Assert.Equal(1, result.LinesIncluded);
            Assert.Contains("- סיפור תאונה: היה פיצוץ בכביש 2", result.Text);
            Assert.Contains("ItemId=777", result.Text);
        }
    }
}
