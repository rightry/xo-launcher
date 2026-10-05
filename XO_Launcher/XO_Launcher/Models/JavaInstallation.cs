namespace XO_Launcher.Models
{
    public sealed class JavaInstallation
    {
        public string Path { get; set; } = "";
        public int Major { get; set; }
        public string Vendor { get; set; } = "Java";
        public string Source { get; set; } = "";

        public string Display => Major > 0 ? $"Java {Major}" : "Java";
    }
}
