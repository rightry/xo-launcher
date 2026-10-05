using System;
using System.IO;
using System.Linq;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 识别并切换 Minecraft 游戏目录：支持官方/HMCL 的 .minecraft，
    /// 以及误选 versions 子目录、或选到其上级文件夹的情况。
    /// </summary>
    public static class GameDirectoryService
    {
        public static string Resolve(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return LauncherSettings.Instance.GameDirectory;

            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch (Exception) { return path.Trim(); }

            if (LooksLikeRoot(full)) return full;

            if (string.Equals(Path.GetFileName(full), "versions", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(full)?.FullName;
                if (parent is not null && LooksLikeRoot(parent)) return parent;
            }

            foreach (var name in new[] { ".minecraft", "minecraft" })
            {
                var nested = Path.Combine(full, name);
                if (LooksLikeRoot(nested)) return nested;
            }

            var childVersions = Path.Combine(full, "versions");
            if (Directory.Exists(childVersions)) return full;

            return full;
        }

        public static string Describe(string gameDir)
        {
            if (!Directory.Exists(gameDir))
                return "目录不存在。请选择已有的 Minecraft 游戏文件夹。";

            var versions = InstalledVersionService.Scan(gameDir);
            var mods = CountAll(gameDir, ContentKind.Mod);
            var shaders = CountAll(gameDir, ContentKind.Shader);
            var packs = CountAll(gameDir, ContentKind.ResourcePack);
            if (versions.Count == 0)
                return "尚未在此目录找到游戏版本。若这是已有安装，请确认其中包含 versions 文件夹。";
            return $"已识别 {versions.Count} 个游戏版本，模组 {mods}，光影 {shaders}，资源包 {packs}。";
        }

        public static string Apply(string path, out string summary)
        {
            var resolved = Resolve(path);
            JavaRuntimeService.InvalidateScanCache();
            InstalledVersionService.Invalidate();

            var versions = InstalledVersionService.Scan(resolved);
            var settings = LauncherSettings.Instance;
            if (!string.Equals(settings.GameDirectory, resolved, StringComparison.OrdinalIgnoreCase))
                settings.GameDirectory = resolved;

            if (versions.Count > 0
                && !versions.Any(v => string.Equals(v.Id, settings.SelectedVersion, StringComparison.OrdinalIgnoreCase)))
                settings.SelectedVersion = versions[0].Id;

            summary = Describe(resolved);
            return resolved;
        }

        public static bool LooksLikeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            return Directory.Exists(Path.Combine(path, "versions"))
                || Directory.Exists(Path.Combine(path, "libraries"))
                || Directory.Exists(Path.Combine(path, "assets"))
                || Directory.Exists(Path.Combine(path, "mods"))
                || Directory.Exists(Path.Combine(path, "shaderpacks"))
                || Directory.Exists(Path.Combine(path, "resourcepacks"));
        }

        public static string? TryGetRootFromInstance(string instanceDir)
        {
            try
            {
                var versionsDir = Directory.GetParent(instanceDir)?.FullName;
                var root = versionsDir is null ? null : Directory.GetParent(versionsDir)?.FullName;
                if (root is not null && string.Equals(Path.GetFileName(versionsDir), "versions", StringComparison.OrdinalIgnoreCase))
                    return root;
            }
            catch (Exception) { }
            return null;
        }

        private static int CountAll(string gameDir, string projectType)
        {
            var total = CountShared(gameDir, projectType);
            var versionsDir = Path.Combine(gameDir, "versions");
            if (!Directory.Exists(versionsDir)) return total;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(versionsDir))
                    total += CountShared(dir, projectType);
            }
            catch (Exception) { }
            return total;
        }

        private static int CountShared(string gameDir, string projectType)
        {
            var folder = Path.Combine(gameDir, ContentKind.LibraryFolder(projectType));
            if (!Directory.Exists(folder)) return 0;
            try
            {
                var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Count();
                if (projectType is ContentKind.Shader or ContentKind.ResourcePack)
                    files += Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly).Count();
                return files;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
