using System.Collections.Generic;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for the ClientNumber dropdown resolution logic.
    /// Validates that when Monday dropdown labels do not contain the ClientVisualID,
    /// the dropdown column is omitted (never sent with an invalid label)
    /// and a fallback text column is used when configured.
    /// </summary>
    public class ClientDropdownTests
    {
        private static readonly HashSet<string> AllowedLabels = new() { "100", "101", "102", "103" };

        // ====================================================================
        // 1. Label not in allowed set -> dropdown omitted
        // ====================================================================

        [Fact]
        public void LabelNotInAllowed_NoFallback_OmitEntirely()
        {
            // ClientVisualID "104" is NOT in the allowed set,
            // and no fallback text column is configured -> omit entirely
            var action = SyncService.ResolveClientDropdownAction("104", AllowedLabels, fallbackTextColumnId: null);

            Assert.Equal(SyncService.ClientDropdownAction.OmitEntirely, action);
        }

        [Fact]
        public void LabelNotInAllowed_FallbackConfigured_UseFallbackText()
        {
            // ClientVisualID "104" is NOT in the allowed set,
            // but a fallback text column is configured -> use fallback
            var action = SyncService.ResolveClientDropdownAction("104", AllowedLabels, fallbackTextColumnId: "text_fallback_123");

            Assert.Equal(SyncService.ClientDropdownAction.UseFallbackText, action);
        }

        // ====================================================================
        // 2. Label IS in allowed set -> include dropdown
        // ====================================================================

        [Fact]
        public void LabelInAllowed_IncludeDropdown()
        {
            var action = SyncService.ResolveClientDropdownAction("101", AllowedLabels, fallbackTextColumnId: null);

            Assert.Equal(SyncService.ClientDropdownAction.IncludeDropdown, action);
        }

        [Fact]
        public void LabelInAllowed_IgnoresFallback()
        {
            // Even when fallback is configured, if label is valid, use dropdown
            var action = SyncService.ResolveClientDropdownAction("101", AllowedLabels, fallbackTextColumnId: "text_fallback_123");

            Assert.Equal(SyncService.ClientDropdownAction.IncludeDropdown, action);
        }

        // ====================================================================
        // 3. Null/empty ClientVisualID -> omit entirely
        // ====================================================================

        [Fact]
        public void NullClientVisualId_OmitEntirely()
        {
            var action = SyncService.ResolveClientDropdownAction(null, AllowedLabels, fallbackTextColumnId: "text_fallback_123");

            Assert.Equal(SyncService.ClientDropdownAction.OmitEntirely, action);
        }

        [Fact]
        public void EmptyClientVisualId_OmitEntirely()
        {
            var action = SyncService.ResolveClientDropdownAction("", AllowedLabels, fallbackTextColumnId: "text_fallback_123");

            Assert.Equal(SyncService.ClientDropdownAction.OmitEntirely, action);
        }

        [Fact]
        public void WhitespaceClientVisualId_OmitEntirely()
        {
            var action = SyncService.ResolveClientDropdownAction("   ", AllowedLabels, fallbackTextColumnId: "text_fallback_123");

            Assert.Equal(SyncService.ClientDropdownAction.OmitEntirely, action);
        }

        // ====================================================================
        // 4. Trimming is applied before lookup
        // ====================================================================

        [Fact]
        public void WhitespaceAroundValidLabel_Trimmed_IncludesDropdown()
        {
            // " 101 " trims to "101" which IS in the allowed set
            var action = SyncService.ResolveClientDropdownAction(" 101 ", AllowedLabels, fallbackTextColumnId: null);

            Assert.Equal(SyncService.ClientDropdownAction.IncludeDropdown, action);
        }

        [Fact]
        public void WhitespaceAroundInvalidLabel_Trimmed_UsesFallback()
        {
            // " 104 " trims to "104" which is NOT in the allowed set
            var action = SyncService.ResolveClientDropdownAction(" 104 ", AllowedLabels, fallbackTextColumnId: "text_fallback_123");

            Assert.Equal(SyncService.ClientDropdownAction.UseFallbackText, action);
        }
    }
}
