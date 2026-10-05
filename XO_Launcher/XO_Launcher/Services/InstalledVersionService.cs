using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 扫描游戏目录 versions 文件夹，列出已下载的原版与模组加载器版本。
    /// 同一 Minecraft 版本的本体和加载器会排在一起。
    /// </summary>
    public static class InstalledVersionService
    {
        private static readonly object CacheGate = new();
        private static IReadOnlyList<InstalledVersion>? _cache;
        private static string? _cacheKey;
        private static DateTime _cacheAt;

        public static void Invalidate()
        {
            lock (CacheGate)
            {
                _cache = null;
                _cacheKey = null;
            }
        }

        public static IReadOnlyList<InstalledVersion> Scan(string? gameDirectory = null)
        {
            var root = gameDirectory ?? LauncherSettings.Instance.GameDirectory;
            lock (CacheGate)
            {
                if (_cache is not null
                    && string.Equals(_cacheKey, root, StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(8))
                    return _cache;
            }

            var list = ScanCore(root);
            lock (CacheGate)
            {
                _cache = list;
                _cacheKey = root;
                _cacheAt = DateTime.UtcNow;
            }
            return list;
        }

        private static IReadOnlyList<InstalledVersion> ScanCore(string root)
        {
            var list = new List<InstalledVersion>();
            try
            {
                list.AddRange(BedrockEditionService.ListInstalledClients(root));
            }
            catch (Exception)
            {
                // Store 包查询失败时仍继续扫描 Java 版本
            }

            var versionsDir = Path.Combine(root, "versions");
            if (!Directory.Exists(versionsDir))
                return Sort(list);

            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var jsonPath = Path.Combine(dir, id + ".json");
                if (!File.Exists(jsonPath)) continue;

                try
                {
                    var parsed = Parse(id, File.ReadAllText(jsonPath), jsonPath);
                    if (parsed is not null
                        && list.TrueForAll(v => !string.Equals(v.Id, parsed.Id, StringComparison.OrdinalIgnoreCase)))
                        list.Add(parsed);
                }
                catch (Exception)
                {
                    // 损坏的版本 JSON 跳过
                }
            }

            var inheritParents = new HashSet<string>(
                list.Select(v => v.InheritsFrom).OfType<string>().Where(id => id.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            return Sort(list
                .Where(v => !IsHiddenInheritParent(v, inheritParents))
                .ToList());
        }

        private static IReadOnlyList<InstalledVersion> Sort(IReadOnlyList<InstalledVersion> list)
            => list
                .OrderBy(v => LoaderOrder(v.LoaderId))
                .ThenByDescending(v => v.MinecraftVersion, Comparer<string>.Create(FormatHelper.CompareMcVersion))
                .ThenByDescending(v => v.LoaderVersion, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static InstalledVersion? Find(string versionId, string? gameDirectory = null)
            => Scan(gameDirectory).FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

        public static bool Exists(string versionId, string? gameDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(versionId)) return false;
            if (BedrockIds.IsClient(versionId))
                return BedrockEditionService.IsClientInstalled(versionId)
                       || BedrockEditionService.IsSideloadInstalled(versionId, gameDirectory);
            if (BedrockIds.IsServer(versionId))
                return BedrockEditionService.IsServerInstalled(versionId, gameDirectory);
            var root = gameDirectory ?? LauncherSettings.Instance.GameDirectory;
            var jsonPath = Path.Combine(root, "versions", versionId, versionId + ".json");
            return File.Exists(jsonPath);
        }

        private static InstalledVersion? Parse(string folderId, string json, string jsonPath)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("xoEdition", out var editionEl))
            {
                var edition = editionEl.GetString() ?? "";
                if (BedrockIds.IsBedrock(edition) || edition.StartsWith("bedrock", StringComparison.OrdinalIgnoreCase))
                    return ParseBedrock(folderId, jsonPath, root, edition);
            }
            var jsonId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? folderId : folderId;
            var inherits = root.TryGetProperty("inheritsFrom", out var inh) ? inh.GetString() : null;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            var mainClass = root.TryGetProperty("mainClass", out var mainEl) ? mainEl.GetString() ?? "" : "";

            var meta = GameInstanceLayout.ReadMeta(Path.GetDirectoryName(jsonPath) ?? "");
            var (loaderId, loaderVersion) = DetectLoader(folderId, jsonId, mainClass);
            if (!string.IsNullOrWhiteSpace(meta?.Loader) && loaderId == "vanilla")
            {
                loaderId = meta.Loader;
                loaderVersion = meta.LoaderVersion ?? loaderVersion;
            }
            else if (string.IsNullOrWhiteSpace(loaderVersion) && !string.IsNullOrWhiteSpace(meta?.LoaderVersion))
            {
                loaderVersion = meta.LoaderVersion;
            }

            var minecraft = !string.IsNullOrWhiteSpace(meta?.MinecraftVersion)
                ? meta.MinecraftVersion
                : !string.IsNullOrWhiteSpace(inherits)
                    ? inherits
                    : loaderId == "vanilla"
                        ? (LooksLikeMinecraftId(jsonId) ? jsonId : folderId)
                        : GuessMinecraftVersion(folderId, jsonId) ?? jsonId;

            var customName = meta?.DisplayName;
            if (string.IsNullOrWhiteSpace(customName)
                && !string.Equals(folderId, minecraft, StringComparison.OrdinalIgnoreCase)
                && !LooksLikeMinecraftId(folderId))
                customName = folderId;

            return new InstalledVersion
            {
                Id = folderId,
                MinecraftVersion = minecraft ?? folderId,
                LoaderId = loaderId,
                LoaderVersion = loaderVersion,
                Type = type,
                JsonPath = jsonPath,
                InheritsFrom = inherits,
                CustomName = customName,
                CustomIcon = meta?.CustomIcon
            };
        }

        private static InstalledVersion ParseBedrock(string folderId, string jsonPath, JsonElement root, string edition)
        {
            var meta = GameInstanceLayout.ReadMeta(Path.GetDirectoryName(jsonPath) ?? "");
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            var loader = string.IsNullOrWhiteSpace(meta?.Loader) ? edition : meta!.Loader;
            var version = meta?.MinecraftVersion
                          ?? (folderId.StartsWith("bedrock-server-", StringComparison.OrdinalIgnoreCase)
                              ? folderId["bedrock-server-".Length..]
                              : folderId);
            return new InstalledVersion
            {
                Id = folderId,
                MinecraftVersion = version,
                LoaderId = loader,
                LoaderVersion = meta?.LoaderVersion ?? version,
                Type = string.IsNullOrWhiteSpace(type)
                    ? (loader.Contains("preview", StringComparison.OrdinalIgnoreCase) ? "snapshot" : "release")
                    : type,
                JsonPath = jsonPath,
                    CustomName = meta?.DisplayName,
                    CustomIcon = meta?.CustomIcon
            };
        }

        private static bool IsHiddenInheritParent(InstalledVersion version, HashSet<string> inheritParents)
        {
            if (version.LoaderId != "vanilla") return false;
            if (!inheritParents.Contains(version.Id) && !inheritParents.Contains(version.MinecraftVersion))
                return false;
            var dir = Path.GetDirectoryName(version.JsonPath);
            return dir is not null && !File.Exists(Path.Combine(dir, "options.txt"));
        }

        private static (string LoaderId, string LoaderVersion) DetectLoader(string folderId, string jsonId, string mainClass)
        {
            var blob = $"{folderId} {jsonId} {mainClass}";
            if (Contains(blob, "fabric"))
                return ("fabric", ExtractVersion(folderId, jsonId, "fabric-loader-", "-fabric-") ?? "");
            if (Contains(blob, "quilt"))
                return ("quilt", ExtractVersion(folderId, jsonId, "quilt-loader-", "-quilt-") ?? "");
            if (Contains(blob, "neoforge"))
                return ("neoforge", ExtractVersion(folderId, jsonId, "neoforge-", "-neoforge-") ?? "");
            if (Contains(blob, "minecraftforge") || Contains(blob, "forge"))
                return ("forge", ExtractVersion(folderId, jsonId, "forge-", "-forge-") ?? "");
            return ("vanilla", "");
        }

        private static bool Contains(string text, string value)
            => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        private static string? ExtractVersion(string folderId, string jsonId, params string[] markers)
        {
            foreach (var source in new[] { folderId, jsonId })
            {
                foreach (var marker in markers)
                {
                    var index = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    var start = index + marker.Length;
                    var rest = source[start..];
                    var end = rest.IndexOfAny(new[] { '-', '_' });
                    var token = end < 0 ? rest : rest[..end];
                    if (Regex.IsMatch(token, @"^\d")) return token.Trim();
                }
            }
            return null;
        }

        private static bool LooksLikeMinecraftId(string id)
            => Regex.IsMatch(id, @"^\d+\.\d+(?:\.\d+)?(?:-[\w.]+)?$")
               || Regex.IsMatch(id, @"^(?:rd|inf|c|a|b|pre|rc)", RegexOptions.IgnoreCase);

        private static string? GuessMinecraftVersion(string folderId, string jsonId)
        {
            foreach (var source in new[] { folderId, jsonId })
            {
                var match = Regex.Match(source, @"\d+\.\d+(?:\.\d+)?");
                if (match.Success) return match.Value;
            }
            return null;
        }

        private static int LoaderOrder(string loaderId) => loaderId switch
        {
            "bedrock" => -2,
            "bedrock-preview" => -1,
            "vanilla" => 0,
            "fabric" => 1,
            "quilt" => 2,
            "forge" => 3,
            "neoforge" => 4,
            "bedrock-server" => 5,
            _ => 10
        };
    }
}
