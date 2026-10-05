using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 只保留当前 Windows 架构需要的库。Linux / macOS / 其它 Windows 位数的 natives 不下载、不进 classpath，
    /// 否则 LWJGL 可能加载错误架构的 DLL，游戏窗口闪一下就退出。
    /// </summary>
    public static class WindowsLibraryFilter
    {
        public static bool Allows(JsonElement library)
        {
            if (!RulesAllow(library)) return false;
            var name = library.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var path = "";
            if (library.TryGetProperty("downloads", out var downloads)
                && downloads.TryGetProperty("artifact", out var artifact)
                && artifact.TryGetProperty("path", out var pathEl))
                path = pathEl.GetString() ?? "";
            return IsCurrentWindowsArtifact(name, path);
        }

        public static bool IsCurrentWindowsArtifact(string? name, string? path)
        {
            var blob = $"{name}\n{path}".ToLowerInvariant();
            if (blob.Contains("natives-linux")
                || blob.Contains("natives-macos")
                || blob.Contains("natives-osx")
                || blob.Contains("linux-x86_64")
                || blob.Contains("linux-aarch")
                || blob.Contains("linux-arm")
                || blob.Contains("osx-x86")
                || blob.Contains("osx-aarch")
                || blob.Contains("osx-arm"))
                return false;

            var arch = RuntimeInformation.OSArchitecture;
            if (blob.Contains("natives-windows-arm64") || blob.Contains("-windows-arm64"))
                return arch == Architecture.Arm64;
            if (blob.Contains("natives-windows-x86") || blob.Contains("-windows-x86"))
                return arch == Architecture.X86;
            return true;
        }

        public static bool RulesAllow(JsonElement element)
        {
            if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return true;
            var allow = false;
            foreach (var rule in rules.EnumerateArray())
            {
                var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
                if (!OsMatches(rule)) continue;
                allow = action == "allow";
            }
            return allow;
        }

        public static bool OsMatches(JsonElement rule)
        {
            if (rule.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
            {
                foreach (var feature in features.EnumerateObject())
                {
                    if (feature.Value.ValueKind == JsonValueKind.True) return false;
                }
            }

            if (!rule.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.Object)
                return true;

            if (os.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } name
                && !string.Equals(name, "windows", StringComparison.OrdinalIgnoreCase))
                return false;

            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x86_64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => "x86_64"
            };
            if (os.TryGetProperty("arch", out var archProp) && archProp.GetString() is { } archRule
                && !string.Equals(archRule, arch, StringComparison.OrdinalIgnoreCase))
                return false;

            if (os.TryGetProperty("versionRange", out var range) && range.ValueKind == JsonValueKind.Object)
            {
                var current = Environment.OSVersion.Version;
                if (range.TryGetProperty("min", out var minEl)
                    && Version.TryParse(minEl.GetString(), out var min)
                    && current < min)
                    return false;
                if (range.TryGetProperty("max", out var maxEl)
                    && Version.TryParse(maxEl.GetString(), out var max)
                    && current >= max)
                    return false;
            }

            return true;
        }

        public static void EnsureNativesLayout(string nativesDir)
        {
            Directory.CreateDirectory(nativesDir);
            foreach (var sub in new[] { "java", "jna", "lwjgl", "netty" })
                Directory.CreateDirectory(Path.Combine(nativesDir, sub));
        }

        public static string NativeExtractDir(string nativesDir, string jarPath)
        {
            var name = Path.GetFileName(jarPath).ToLowerInvariant();
            if (name.Contains("lwjgl")) return Path.Combine(nativesDir, "lwjgl");
            if (name.Contains("jna")) return Path.Combine(nativesDir, "jna");
            if (name.Contains("netty")) return Path.Combine(nativesDir, "netty");
            return Path.Combine(nativesDir, "java");
        }
    }
}
