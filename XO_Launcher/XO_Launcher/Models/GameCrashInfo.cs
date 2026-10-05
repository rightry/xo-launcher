namespace XO_Launcher.Models
{
    public sealed class GameCrashInfo
    {
        public string VersionId { get; set; } = "";
        public string InstanceDirectory { get; set; } = "";
        public int ExitCode { get; set; }
        public string Kind { get; set; } = "游戏崩溃";
        public string Summary { get; set; } = "";
        public string? CrashReportPath { get; set; }
        public string? HsErrPath { get; set; }
        public string? LatestLogPath { get; set; }
        public string? LaunchLogPath { get; set; }
    }
}
