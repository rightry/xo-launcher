using System;
using System.IO;

namespace XO_Launcher.Models
{
    /// <summary>可共存的游戏安装目录。当前活动项的 Path 写入 <see cref="Services.LauncherSettings.GameDirectory"/>。</summary>
    public sealed class GameFolderEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";

        public string Display => string.IsNullOrWhiteSpace(Name) ? Path : Name;

        public static string SuggestName(string path)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(path);
                var name = System.IO.Path.GetFileName(full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.Equals(name, ".minecraft", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Directory.GetParent(full)?.Name;
                    return string.IsNullOrWhiteSpace(parent) ? "默认游戏目录" : parent;
                }
                return string.IsNullOrWhiteSpace(name) ? "游戏目录" : name;
            }
            catch (Exception)
            {
                return "游戏目录";
            }
        }
    }
}
