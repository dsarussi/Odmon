using System;

namespace Odmon.Worker.Models
{
    public class OdcanitDiaryEvent
    {
        public string? SortName { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? FromTime { get; set; }
        public DateTime? EndDate { get; set; }
        public DateTime? ToTime { get; set; }
        public string? Descr { get; set; }
        public string? Place { get; set; }
        public string? Phone { get; set; }
        public string? City { get; set; }
        public string? MeetingTypeName { get; set; }
        public string? CourtCodeName { get; set; }
        public string? CourtProcess { get; set; }
        public string? IDInCourt { get; set; }
        public string? CourtName { get; set; }
        public string? JudgeName { get; set; }
        public string? GlobCourtNum { get; set; }
        public string? HozNum { get; set; }
        public string? LishcaName { get; set; }
        public DateTime? tsCreateDate { get; set; }
        public string? tsCreatedBy { get; set; }
        public DateTime? tsModifyDate { get; set; }
        public string? tsModifiedBy { get; set; }
        public string? UsersNames { get; set; }
        public int? SideCounter { get; set; }
        public int? TikCounter { get; set; }
        public int? MeetStatus { get; set; }
        public string? MeetStatusName { get; set; }
    }
}

