using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 单个游戏实例目录（HMCL 版本隔离 / Prism instance）：
    /// 本体 jar、加载器 JSON、mods / saves 都放在 versions/{id}/ 下，
    /// libraries 与 assets 仍与其它版本共享。
    /// </summary>
    public static class GameInstanceLayout
    {
        public const string MetaFileName = "xo-instance.json";

        private static readonly string[] SubFolders =
        {
            "mods", "config", "saves", "screenshots", "resourcepacks", "shaderpacks", "logs", "natives"
        };

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string GetDirectory(string gameDir, string versionId)
            => Path.Combine(gameDir, "versions", versionId);

        public static string GetSubFolder(string instanceDir, string name)
        {
            var path = Path.Combine(instanceDir, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public static void Ensure(string instanceDir)
        {
            Directory.CreateDirectory(instanceDir);
            foreach (var sub in SubFolders)
                Directory.CreateDirectory(Path.Combine(instanceDir, sub));
        }

        public static void EnsureLaunchSupport(string instanceDir)
        {
            Directory.CreateDirectory(instanceDir);
            Directory.CreateDirectory(Path.Combine(instanceDir, "natives"));
            Directory.CreateDirectory(Path.Combine(instanceDir, "logs"));
        }

        /// <summary>
        /// 本启动器安装的实例、开启独立设置、或版本目录里已有模组/光影等内容时使用隔离目录；
        /// 官方 / HMCL 共享的 .minecraft 则读取游戏根目录。
        /// </summary>
        public static bool UsesIsolatedContent(string instanceDir)
        {
            if (HasContentFiles(instanceDir)) return true;
            var meta = ReadMeta(instanceDir);
            if (meta is null) return false;
            if (meta.IndependentSettings) return true;
            if (!string.IsNullOrWhiteSpace(meta.Loader) && !string.Equals(meta.Loader, "vanilla", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrWhiteSpace(meta.MinecraftVersion);
        }

        public static string ResolveContentRoot(string gameDir, string instanceDir)
            => UsesIsolatedContent(instanceDir) ? instanceDir : gameDir;

        private static bool HasContentFiles(string instanceDir)
        {
            foreach (var folder in new[] { "mods", "shaderpacks", "resourcepacks", "saves", "config" })
            {
                var path = Path.Combine(instanceDir, folder);
                if (!Directory.Exists(path)) continue;
                try
                {
                    if (Directory.EnumerateFileSystemEntries(path).Any())
                        return true;
                }
                catch (Exception) { }
            }
            return false;
        }

        public static string MakeId(string mcId, string? loader, string? loaderVersion)
        {
            mcId = Sanitize(mcId);
            if (string.IsNullOrWhiteSpace(loader) || loader == "vanilla")
                return mcId;
            var ver = Sanitize(loaderVersion ?? "");
            return string.IsNullOrEmpty(ver) ? $"{mcId}-{loader}" : $"{mcId}-{loader}-{ver}";
        }

        public static string AllocateId(string gameDir, string desiredName)
        {
            var baseId = Sanitize(desiredName);
            if (string.IsNullOrWhiteSpace(baseId)) baseId = "Minecraft";
            var id = baseId;
            var n = 2;
            while (Directory.Exists(Path.Combine(gameDir, "versions", id)))
                id = $"{baseId}-{n++}";
            return id;
        }

        public static void WriteMeta(string instanceDir, InstanceMeta meta)
        {
            File.WriteAllText(
                Path.Combine(instanceDir, MetaFileName),
                JsonSerializer.Serialize(meta, JsonOptions));
        }

        public static InstanceMeta? ReadMeta(string instanceDir)
        {
            var path = Path.Combine(instanceDir, MetaFileName);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<InstanceMeta>(File.ReadAllText(path));
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        public static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }

        public sealed class InstanceMeta
        {
            public string? DisplayName { get; set; }
            public string? MinecraftVersion { get; set; }
            public string? Loader { get; set; }
            public string? LoaderVersion { get; set; }
            public bool IndependentSettings { get; set; }
            public long? MemoryMB { get; set; }
            public string? JavaPath { get; set; }
            public bool? AutoMatchJava { get; set; }
            public string? ExtraJvmArgs { get; set; }
            public string? ExtraGameArgs { get; set; }
            public int? WindowWidth { get; set; }
            public int? WindowHeight { get; set; }
            /// <summary>窗口模式：default / fullscreen / maximized / custom（null 视为 default）。</summary>
            public string? WindowMode { get; set; }
            /// <summary>自定义版本图标文件名（null = 按版本类型自动）。</summary>
            public string? CustomIcon { get; set; }
        }

        public static InstanceMeta GetOrCreateMeta(string instanceDir)
            => ReadMeta(instanceDir) ?? new InstanceMeta();

        public static ResolvedLaunchSettings ResolveLaunch(string? versionId = null)
        {
            var global = LauncherSettings.Instance;
            versionId ??= global.SelectedVersion;
            var meta = string.IsNullOrWhiteSpace(versionId)
                ? null
                : ReadMeta(GetDirectory(global.GameDirectory, versionId));

            if (meta is not { IndependentSettings: true })
            {
                return new ResolvedLaunchSettings
                {
                    Independent = false,
                    MemoryMB = global.MemoryMB,
                    JavaPath = global.JavaPath,
                    AutoMatchJava = global.AutoMatchJava
                };
            }

            return new ResolvedLaunchSettings
            {
                Independent = true,
                MemoryMB = meta.MemoryMB is > 0 ? meta.MemoryMB.Value : global.MemoryMB,
                JavaPath = string.IsNullOrWhiteSpace(meta.JavaPath) ? global.JavaPath : meta.JavaPath,
                AutoMatchJava = meta.AutoMatchJava ?? global.AutoMatchJava,
                ExtraJvmArgs = meta.ExtraJvmArgs ?? "",
                ExtraGameArgs = meta.ExtraGameArgs ?? "",
                WindowWidth = meta.WindowWidth is > 0 ? meta.WindowWidth.Value : 854,
                WindowHeight = meta.WindowHeight is > 0 ? meta.WindowHeight.Value : 480,
                WindowMode = NormalizeWindowMode(meta.WindowMode)
            };
        }

        private static string NormalizeWindowMode(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "fullscreen" => "fullscreen",
            "maximized" => "maximized",
            "custom" => "custom",
            _ => "default"
        };
    }

    public sealed class ResolvedLaunchSettings
    {
        public bool Independent { get; init; }
        public long MemoryMB { get; init; }
        public string? JavaPath { get; init; }
        public bool AutoMatchJava { get; init; }
        public string ExtraJvmArgs { get; init; } = "";
        public string ExtraGameArgs { get; init; } = "";
        public int WindowWidth { get; init; } = 854;
        public int WindowHeight { get; init; } = 480;
        /// <summary>窗口模式：default / fullscreen / maximized / custom。</summary>
        public string WindowMode { get; init; } = "default";
    }
}
