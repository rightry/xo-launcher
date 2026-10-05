using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 版本图标：按加载器与版本类型自动匹配 Assets\icons 下的图标
    /// （正式版 = 草方块，快照版 = 命令方块，Fabric = 布料，Forge = 铁砧，NeoForge = 狐狸，Quilt = 横幅），
    /// 目录里其余图片作为可在版本设置中选择的自定义图标。
    /// </summary>
    public static class VersionIconService
    {
        private static readonly Dictionary<string, string> LoaderIcons = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fabric"] = "Fabric.png",
            ["forge"] = "Anvil.png",
            ["neoforge"] = "NeoForge.png",
            ["quilt"] = "banner.png",
            ["bedrock"] = "Grass.png",
            ["bedrock-preview"] = "CommandBlock.png",
            ["bedrock-server"] = "Anvil.png"
        };

        public static string IconsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "icons");

        /// <summary>按加载器与版本类型解析默认图标文件名。</summary>
        public static string GetAutoIconName(string loaderId, string type)
        {
            if (!string.IsNullOrWhiteSpace(loaderId) && LoaderIcons.TryGetValue(loaderId, out var loaderIcon))
                return loaderIcon;
            return string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase) || type is "old_alpha" or "old_beta"
                ? "CommandBlock.png"
                : "Grass.png";
        }

        /// <summary>自定义可选项：icons 目录下未被默认映射占用的图片。</summary>
        public static IReadOnlyList<string> GetCustomChoices()
        {
            try
            {
                var reserved = LoaderIcons.Values
                    .Append("Grass.png")
                    .Append("CommandBlock.png")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Directory.Exists(IconsFolder)
                    ? Directory.EnumerateFiles(IconsFolder, "*.png")
                        .Select(Path.GetFileName)
                        .Where(n => n is not null && !reserved.Contains(n))
                        .Cast<string>()
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static readonly string[] InstanceIconNames =
        {
            "pack-icon.png", "pack-icon.jpg", "pack-icon.jpeg", "pack-icon.gif",
            "icon.png", "icon.jpg", "pack.png"
        };

        public static bool Exists(string? name)
            => !string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(IconsFolder, name));

        /// <summary>实例目录里的整合包封面或用户放入的图标。</summary>
        public static string? FindInstanceIcon(string? instanceDir)
        {
            if (string.IsNullOrWhiteSpace(instanceDir) || !Directory.Exists(instanceDir))
                return null;
            foreach (var name in InstanceIconNames)
            {
                var path = Path.Combine(instanceDir, name);
                if (IsDisplayableImage(path))
                    return path;
            }
            return null;
        }

        public static bool IsInstanceIcon(string? name, string? instanceDir)
            => ResolveInstanceIcon(instanceDir, name) is not null;

        public static string? ResolveInstanceIcon(string? instanceDir, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Path.IsPathRooted(name) && IsDisplayableImage(name))
                return name;
            if (string.IsNullOrWhiteSpace(instanceDir)) return null;
            var path = Path.Combine(instanceDir, name);
            return IsDisplayableImage(path) ? path : null;
        }

        /// <summary>解析版本图标完整路径：优先实例内封面 / 自定义方块图，否则按加载器/类型自动匹配。</summary>
        public static string GetIconPath(string? customIcon, string loaderId, string type, string? instanceDir = null)
        {
            var instance = ResolveInstanceIcon(instanceDir, customIcon) ?? FindInstanceIcon(instanceDir);
            if (!string.IsNullOrWhiteSpace(instance))
                return instance;
            if (Exists(customIcon))
                return Path.Combine(IconsFolder, customIcon!);
            return Path.Combine(IconsFolder, GetAutoIconName(loaderId, type));
        }

        public static bool IsDisplayableImage(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return false;
                var length = new FileInfo(path).Length;
                if (length is < 24 or > 2_000_000)
                    return false;
                var header = new byte[16];
                using var stream = File.OpenRead(path);
                if (stream.Read(header, 0, header.Length) < 8)
                    return false;
                return RemoteIconLoader.IsSupportedImage(header);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
