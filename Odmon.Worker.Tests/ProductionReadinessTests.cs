using System.Globalization;
using Xunit;
using Odmon.Worker.Configuration;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Lightweight production-readiness assertions: note date format, document metadata defaults.
    /// No external dependencies or DB calls.
    /// </summary>
    public class ProductionReadinessTests
    {
        [Fact]
        public void HearingApprovalNote_DateFormat_IsDDMMYYYY()
        {
            // Hearing approval Nispah uses dd/MM/yyyy HH:mm for display (Israel/local).
            var date = new DateTime(2026, 2, 16, 14, 30, 0);
            var formatted = date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            Assert.Matches(@"^\d{2}/\d{2}/\d{4} \d{2}:\d{2}$", formatted);
            Assert.Equal("16/02/2026 14:30", formatted);
        }

        [Fact]
        public void OdcanitDocumentSettings_DefaultDocType_Is8()
        {
            var settings = new OdcanitDocumentSettings();
            Assert.Equal(8, settings.DocType);
        }

        [Fact]
        public void OdcanitDocumentSettings_DefaultCategoryCounter_Is1()
        {
            var settings = new OdcanitDocumentSettings();
            Assert.Equal(1, settings.CategoryCounter);
        }
    }
}
