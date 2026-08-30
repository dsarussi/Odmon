namespace Odmon.Worker.Models
{
    /// <summary>
    /// Minimal EmailFiling-only projection over dbo.SIDES. This must remain
    /// separate from OdcanitSide, which maps the production export view.
    /// </summary>
    public sealed class OdcanitSideDataLink
    {
        public int TikCounter { get; set; }
        public int? SideDataCounter { get; set; }
    }
}
