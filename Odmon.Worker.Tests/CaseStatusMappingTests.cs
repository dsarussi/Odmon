using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class CaseStatusMappingTests
    {
        [Theory]
        [InlineData("בוטל", "בוטל")]
        [InlineData("דיווח", "דיווח")]
        [InlineData("הוחזר לביטוח", "הוחזר לביטוח")]
        [InlineData("הסדר תשלום", "הסדר תשלום")]
        [InlineData("ממתין לפסק דין", "ממתין לפסק דין")]
        [InlineData("ממתין לתשלום", "ממתין לתשלום")]
        [InlineData("מעוכב", "מעוכב")]
        [InlineData("סגור", "סגור")]
        [InlineData("פתוח", "פתוח")]
        [InlineData("סגור- נפתח בטעות", "סגור - נפתח בטעות פש\"ר")]
        public void MapCaseStatusLabel_SupportedStatus_ReturnsMondayLabel(string statusName, string expectedLabel)
        {
            Assert.Equal(expectedLabel, SyncService.MapCaseStatusLabel(statusName));
        }

        [Fact]
        public void MapCaseStatusLabel_TrimsBeforeMapping()
        {
            Assert.Equal("פתוח", SyncService.MapCaseStatusLabel("  פתוח  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("פעיל")]
        [InlineData("חדש")]
        [InlineData("באמצע תהליך")]
        public void MapCaseStatusLabel_MissingOrUnsupportedStatus_ReturnsNull(string? statusName)
        {
            Assert.Null(SyncService.MapCaseStatusLabel(statusName));
        }
    }
}
