using System;

namespace Odmon.Worker.Models
{
    public class OdcanitUserData
    {
        public string? PageName { get; set; }
        public string? FieldName { get; set; }
        public long TikCounter { get; set; }
        public string? strData { get; set; }
        public DateTime? dateData { get; set; }
        public double? numData { get; set; }
        public string? Data { get; set; }
        public long? RowNum { get; set; }
    }
}

