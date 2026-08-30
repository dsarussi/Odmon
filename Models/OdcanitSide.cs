using System;

namespace Odmon.Worker.Models
{
    public class OdcanitSide
    {
        public string? FullName { get; set; }
        public int TikCounter { get; set; }
        public string? TikNumber { get; set; }
        public string? ID { get; set; }
        public string? FullAddress { get; set; }
        public int? SideTypeCode { get; set; }
        public string? SideTypeName { get; set; }
        public DateTime? tsCreateDate { get; set; }
        public DateTime? tsModifyDate { get; set; }
    }
}

