namespace Odmon.Worker.Models
{
    public class OdcanitUser
    {
        public int UserID { get; set; }
        public string FullName { get; set; } = string.Empty;
        public bool Active { get; set; }
        public string? EMail { get; set; }
    }
}

