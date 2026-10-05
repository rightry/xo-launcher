using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XO_Launcher.Models
{
    public static class FormatHelper
    {
        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return $"{value:0.##} {units[i]}";
        }

        public static string FormatCompactNumber(long value)
        {
            return value switch
            {
                >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
                >= 1_000 => $"{value / 1_000d:0.#}K",
                _ => value.ToString("N0")
            };
        }

        public static string FormatDate(string iso)
        {
            return DateTimeOffset.TryParse(iso, out var d) ? d.ToString("yyyy-MM-dd") : "";
        }

        /// <summary>比较 Minecraft 版本号（如 1.21.5 > 1.20.6）。</summary>
        public static int CompareMcVersion(string a, string b)
        {
            var pa = ParseNumericParts(a);
            var pb = ParseNumericParts(b);
            if (pa is not null && pb is not null)
            {
                var n = Math.Max(pa.Length, pb.Length);
                for (int i = 0; i < n; i++)
                {
                    var x = i < pa.Length ? pa[i] : 0;
                    var y = i < pb.Length ? pb[i] : 0;
                    if (x != y) return x.CompareTo(y);
                }
                return 0;
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>将 Modrinth loader id 转为界面显示名。</summary>
        public static string FormatLoaderName(string loader)
        {
            if (string.IsNullOrWhiteSpace(loader) || loader == "其他加载器")
                return "其他加载器";

            return loader.Trim().ToLowerInvariant() switch
            {
                "fabric" => "Fabric",
                "forge" => "Forge",
                "neoforge" => "NeoForge",
                "quilt" => "Quilt",
                "liteloader" => "LiteLoader",
                "rift" => "Rift",
                "vanilla" => "原版",
                "minecraft" => "Minecraft",
                "bedrock" => "基岩版",
                "bedrock-preview" => "基岩预览版",
                "bedrock-server" => "基岩服务器",
                "paper" => "Paper",
                "spigot" => "Spigot",
                "bukkit" => "Bukkit",
                "purpur" => "Purpur",
                "sponge" => "Sponge",
                "velocity" => "Velocity",
                "bungeecord" => "BungeeCord",
                "waterfall" => "Waterfall",
                _ => char.ToUpperInvariant(loader[0]) + loader[1..]
            };
        }

        /// <summary>加载器栏排序：常见加载器靠前，未知项按名称，无加载器垫底。</summary>
        public static int LoaderSortOrder(string loader)
        {
            return loader.Trim().ToLowerInvariant() switch
            {
                "fabric" => 0,
                "quilt" => 1,
                "forge" => 2,
                "neoforge" => 3,
                "liteloader" => 4,
                "rift" => 5,
                "vanilla" or "minecraft" => 6,
                "其他加载器" => 100,
                _ => 50
            };
        }

        private static int[]? ParseNumericParts(string s)
        {
            var parts = s.Split('.');
            if (!parts.All(p => int.TryParse(p, out _))) return null;
            return parts.Select(int.Parse).ToArray();
        }

        public static bool IsMinecraftRelease(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id == "其他版本") return false;
            return Regex.IsMatch(id.Trim(), @"^\d+\.\d+(?:\.\d+)?$");
        }

        public static bool IsMinecraftSnapshot(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id == "其他版本") return false;
            return !IsMinecraftRelease(id);
        }

        /// <summary>
        /// 把一组游戏版本摘要成资源列表里显示的「首 ~ 末」形式（如 “1.20.1 ~ 1.21.4”），
        /// 与资源库中显示游戏版本的形式保持一致。只保留正式版本（<see cref="IsMinecraftRelease"/>
        /// 天然滤掉快照、rc / pre 预发布版等），去重后按 <see cref="CompareMcVersion"/> 升序取首尾；
        /// 只有一个版本时返回该版本本身；没有可用版本时返回空串。
        /// </summary>
        public static string SummarizeGameVersions(IEnumerable<string>? versions)
        {
            if (versions is null) return "";

            var list = versions
                .Select(v => v?.Trim() ?? "")
                .Where(IsMinecraftRelease)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, Comparer<string>.Create(CompareMcVersion))
                .ToList();

            if (list.Count == 0) return "";

            var oldest = list[0];
            var newest = list[^1];
            return string.Equals(oldest, newest, StringComparison.OrdinalIgnoreCase)
                ? oldest
                : $"{oldest} ~ {newest}";
        }
    }
}