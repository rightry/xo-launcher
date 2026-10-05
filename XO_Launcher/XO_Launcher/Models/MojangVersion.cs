using System.Collections.Generic;

namespace XO_Launcher.Models
{
    /// <summary>Mojang 版本清单（version_manifest_v2.json）。</summary>
    public class MojangVersionManifest
    {
        public LatestVersion Latest { get; set; } = new();
        public List<MinecraftVersion> Versions { get; set; } = new();
    }

    public class LatestVersion
    {
        public string Release { get; set; } = "";
        public string Snapshot { get; set; } = "";
    }

    public class MinecraftVersion
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
        public string Time { get; set; } = "";
        public string ReleaseTime { get; set; } = "";
    }
}