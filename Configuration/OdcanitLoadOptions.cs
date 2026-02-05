using System.Collections.Generic;

namespace Odmon.Worker.Configuration
{
    /// <summary>
    /// Configuration for loading cases from Odcanit.
    /// Controls whether to use an allowlist of specific cases vs production behavior.
    /// </summary>
    public class OdcanitLoadOptions
    {
        /// <summary>
        /// If true, load ONLY cases specified in TikCounters and TikNumbers allowlists.
        /// If false, use current production behavior (no allowlist filtering).
        /// </summary>
        public bool EnableAllowList { get; set; } = false;

        /// <summary>
        /// List of TikCounter values to load (internal integer IDs).
        /// Only used when EnableAllowList=true.
        /// </summary>
        public List<int> TikCounters { get; set; } = new();

        /// <summary>
        /// List of TikNumber values to load (e.g., "9/1808").
        /// These will be resolved to TikCounters via Odcanit DB lookup.
        /// Only used when EnableAllowList=true.
        /// </summary>
        public List<string> TikNumbers { get; set; } = new();
    }
}
