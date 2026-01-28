namespace Odmon.Worker.Services
{
    public class NispahWriterSettings
    {
        public string TikVisualIDRegex { get; set; } = @"^[A-Z0-9\-_]+$";
        public int MaxInfoLength { get; set; } = 2000;
        public int DeduplicationWindowMinutes { get; set; } = 60;
        public int MaxCreatesPerRun { get; set; } = 100;
        public int MaxCreatesPerMinute { get; set; } = 10;
        public int CommandTimeoutSeconds { get; set; } = 30;
    }
}
