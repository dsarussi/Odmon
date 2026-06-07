using System.Collections.Generic;
using System.Text.Json;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class DriverEmailMappingTests
    {
        private const string DriverEmailColumnId = "email_mkwefwgy";

        [Fact]
        public void UserDataDriverEmail_PopulatesDriverEmail()
        {
            var odcanitCase = new OdcanitCase();
            var row = new OdcanitUserData
            {
                PageName = "פרטי תיק נזיקין מליגל",
                FieldName = "אימייל נהג",
                strData = " driver@example.com "
            };

            Assert.True(SqlOdcanitReader.ApplyUserDataField(odcanitCase, row));
            Assert.Equal("driver@example.com", odcanitCase.DriverEmail);
        }

        [Fact]
        public void PopulatedDriverEmail_IsAddedToRepurposedMondayColumn()
        {
            var columns = new Dictionary<string, object>();

            Assert.True(SyncService.TryAddValidatedEmailColumn(
                columns,
                DriverEmailColumnId,
                "driver@example.com"));

            var json = JsonSerializer.Serialize(columns[DriverEmailColumnId]);
            Assert.Contains("\"email\":\"driver@example.com\"", json);
            Assert.Contains("\"text\":\"driver@example.com\"", json);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MissingDriverEmail_OmitsOnlyDriverEmailColumn(string? driverEmail)
        {
            var columns = new Dictionary<string, object>
            {
                ["text_other"] = "preserved"
            };

            Assert.False(SyncService.TryAddValidatedEmailColumn(
                columns,
                DriverEmailColumnId,
                driverEmail));
            Assert.False(columns.ContainsKey(DriverEmailColumnId));
            Assert.Equal("preserved", columns["text_other"]);
        }

        [Theory]
        [InlineData("not-an-email")]
        [InlineData("driver@")]
        [InlineData("@example.com")]
        public void InvalidDriverEmail_OmitsOnlyDriverEmailColumn(string driverEmail)
        {
            var columns = new Dictionary<string, object>
            {
                ["text_other"] = "preserved"
            };

            Assert.False(SyncService.TryAddValidatedEmailColumn(
                columns,
                DriverEmailColumnId,
                driverEmail));
            Assert.False(columns.ContainsKey(DriverEmailColumnId));
            Assert.Equal("preserved", columns["text_other"]);
        }

        [Fact]
        public void OldClientAndPolicyHolderEmails_DoNotPopulateRepurposedColumn()
        {
            var odcanitCase = new OdcanitCase
            {
                ClientEmail = "client@example.com",
                PolicyHolderEmail = "policy@example.com",
                DriverEmail = null
            };
            var columns = new Dictionary<string, object>();

            Assert.False(SyncService.TryAddValidatedEmailColumn(
                columns,
                DriverEmailColumnId,
                odcanitCase.DriverEmail));
            Assert.False(columns.ContainsKey(DriverEmailColumnId));
        }

        [Fact]
        public void DriverEmailChange_ChangesContentVersion_ButClientEmailChangeDoesNot()
        {
            var original = new OdcanitCase
            {
                TikNumber = "1",
                ClientEmail = "old-client@example.com",
                DriverEmail = "driver@example.com"
            };
            var clientChanged = new OdcanitCase
            {
                TikNumber = "1",
                ClientEmail = "new-client@example.com",
                DriverEmail = "driver@example.com"
            };
            var driverChanged = new OdcanitCase
            {
                TikNumber = "1",
                ClientEmail = "old-client@example.com",
                DriverEmail = "new-driver@example.com"
            };

            Assert.Equal(
                SyncService.ComputeContentVersion(original),
                SyncService.ComputeContentVersion(clientChanged));
            Assert.NotEqual(
                SyncService.ComputeContentVersion(original),
                SyncService.ComputeContentVersion(driverChanged));
        }
    }
}
