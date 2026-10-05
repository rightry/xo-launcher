using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 新版本首次启动时写入 options.txt：简体中文，并跳过无障碍引导界面。
    /// 已有语言或引导选项不会被覆盖。
    /// </summary>
    public static class GameOptionsDefaults
    {
        public static void ApplyForNewInstance(string instanceDir, string? minecraftVersion = null)
        {
            Directory.CreateDirectory(instanceDir);
            var path = Path.Combine(instanceDir, "options.txt");
            var lang = UseLegacyLangTag(minecraftVersion) ? "zh_CN" : "zh_cn";

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var split = line.IndexOf(':');
                    if (split <= 0) continue;
                    map[line[..split]] = line[(split + 1)..];
                }
            }

            var changed = false;
            if (!map.ContainsKey("lang"))
            {
                map["lang"] = lang;
                changed = true;
            }
            if (!map.ContainsKey("onboardAccessibility"))
            {
                map["onboardAccessibility"] = "false";
                changed = true;
            }

            if (!changed && File.Exists(path)) return;

            var lines = new List<string>();
            foreach (var pair in map)
                lines.Add($"{pair.Key}:{pair.Value}");
            File.WriteAllLines(path, lines);
        }

        private static bool UseLegacyLangTag(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            var match = Regex.Match(version, @"^(\d+)\.(\d+)");
            if (!match.Success) return version.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                                       || version.Contains("beta", StringComparison.OrdinalIgnoreCase);
            var minor = int.Parse(match.Groups[2].Value);
            return int.Parse(match.Groups[1].Value) == 1 && minor < 13;
        }
    }
}
