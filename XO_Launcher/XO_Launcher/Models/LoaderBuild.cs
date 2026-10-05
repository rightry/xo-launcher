using System;
using System.Collections.Generic;

namespace XO_Launcher.Models
{
    /// <summary>某个 Minecraft 版本可用的模组加载器构建（Fabric / Forge / NeoForge / Quilt）。</summary>
    public sealed class LoaderBuild
    {
        public string Loader { get; set; } = "";
        public string Version { get; set; } = "";
        public string Display { get; set; } = "";
        public bool Stable { get; set; }
        public string MavenVersion { get; set; } = "";
        public string InstallerUrl { get; set; } = "";
    }

    /// <summary>各加载器已发布构建所覆盖的 Minecraft 版本集合。</summary>
    public sealed class LoaderSupportIndex
    {
        public HashSet<string> Fabric { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Quilt { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Forge { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NeoForge { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Format(string minecraftId)
        {
            if (string.IsNullOrWhiteSpace(minecraftId)) return "";
            var tags = new List<string>(4);
            if (Fabric.Contains(minecraftId)) tags.Add("Fabric");
            if (Forge.Contains(minecraftId)) tags.Add("Forge");
            if (NeoForge.Contains(minecraftId)) tags.Add("NeoForge");
            if (Quilt.Contains(minecraftId)) tags.Add("Quilt");
            return string.Join("  ·  ", tags);
        }
    }
}
