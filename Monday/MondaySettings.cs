namespace Odmon.Worker.Monday
{
    public class MondaySettings
    {
        public string? ApiToken { get; set; }
        public long BoardId { get; set; }
        public long CasesBoardId { get; set; }
        public string? ToDoGroupId { get; set; }
        public string? ClientPhoneColumnId { get; set; } = "phone_mkwe10tx";
        public string? ClientEmailColumnId { get; set; } = "email_mkwefwgy";
    }
}

