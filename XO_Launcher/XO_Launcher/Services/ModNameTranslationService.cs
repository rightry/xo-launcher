using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 模组中文名称。内置常用译名，并会下载 MC 百科整理的公开词表作为补充。
    /// </summary>
    public static class ModNameTranslationService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(string Chinese, string English, string Slug)> SearchIndex = new();
        private static bool _seeded;
        private static bool _loaded;

        private static readonly string[] RemoteSources =
        {
            "https://cdn.jsdelivr.net/gh/HMCL-dev/HMCL@d0be7efc/HMCL/src/main/resources/assets/mod_data.txt",
            "https://raw.githubusercontent.com/HMCL-dev/HMCL/d0be7efc/HMCL/src/main/resources/assets/mod_data.txt"
        };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (mod name translations)");
            return client;
        }

        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "mod_translations.txt");

        public static async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
        {
            EnsureSeed();
            if (_loaded) return;
            await Gate.WaitAsync(cancellationToken);
            try
            {
                if (_loaded) return;
                var path = CachePath;
                var stale = !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(14);
                if (stale)
                    await DownloadCacheAsync(cancellationToken);
                if (File.Exists(path))
                    ParseIntoMap(await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken));
                _loaded = true;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("ModName", "加载模组译名失败", ex);
                _loaded = true;
            }
            finally
            {
                Gate.Release();
            }
        }

        public static string Translate(string original, params string?[] keys)
        {
            EnsureSeed();
            if (!LauncherSettings.Instance.TranslateModNames)
                return original;
            if (string.IsNullOrWhiteSpace(original) && (keys.Length == 0 || keys.All(string.IsNullOrWhiteSpace)))
                return original ?? "";

            foreach (var key in keys)
            {
                var hit = Lookup(key);
                if (hit is not null) return hit;
            }

            return Lookup(original) ?? original;
        }

        public static string ForFile(string filePath, string fileName)
            => DescribeFile(filePath, fileName).DisplayName;

        /// <summary>一次读盘同时拿到中文显示名、模组版本号与适配的 Minecraft 版本，避免重复解压 jar。</summary>
        public static (string DisplayName, string Version, string GameVersions) DescribeFile(string filePath, string fileName)
        {
            var (id, display, version, gameVersions) = DescribeFileFull(filePath, fileName);
            _ = id;
            return (display, version, gameVersions);
        }

        /// <summary>
        /// 同上，但把 jar 元数据里的 mod id 一起交出来。
        /// 资源库索引要用它判断「两个文件名不同的 jar 其实是同一个模组的同一个版本」，
        /// 而显示名是翻译过的中文，拿来做去重键会错。
        /// </summary>
        public static (string Id, string DisplayName, string Version, string GameVersions) DescribeFileFull(
            string filePath, string fileName)
        {
            var (id, metaName, version, gameVersions) = ModMetadataReader.Read(filePath);
            var fallback = string.IsNullOrWhiteSpace(metaName) ? fileName : metaName;
            var display = Translate(fallback, id, metaName, fileName, Path.GetFileNameWithoutExtension(fileName));
            return (id, display, version, gameVersions);
        }

        public static string ResolveSearchQuery(string query)
        {
            EnsureSeed();
            if (string.IsNullOrWhiteSpace(query) || !ContainsCjk(query))
                return query;

            EnsureSearchIndex();

            var exact = SearchIndex.FirstOrDefault(e =>
                string.Equals(e.Chinese, query.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact.English))
                return exact.English;

            var partial = SearchIndex
                .Where(e => e.Chinese.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Chinese.Length)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(partial.English))
                return partial.English;
            if (!string.IsNullOrWhiteSpace(partial.Slug))
                return partial.Slug;
            return query;
        }

        private static void AddSearchRow(string key, string chinese, string english)
        {
            if (string.IsNullOrWhiteSpace(chinese) || string.IsNullOrWhiteSpace(english)) return;
            SearchIndex.Add((chinese, english, key));
        }

        /// <summary>
        /// 休眠时丢掉「中文 → 英文」反查索引：它是词表行数的三倍副本，只有中文搜索才用得到。
        /// 下次真按中文搜索时 EnsureSearchIndex 会从缓存文件重建，用户看不出差别。
        /// </summary>
        public static void TrimSearchIndex()
        {
            if (SearchIndex.Count == 0) return;
            SearchIndex.Clear();
        }

        private static void EnsureSearchIndex()
        {
            if (SearchIndex.Count > 0) return;
            foreach (var (key, zh, en) in Seed)
                AddSearchRow(key, zh, en);
            try
            {
                if (!File.Exists(CachePath)) return;
                foreach (var line in File.ReadLines(CachePath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    var parts = trimmed.Split(';');
                    if (parts.Length < 5) continue;
                    var zh = parts[3].Trim();
                    var en = parts[4].Trim();
                    AddSearchRow(parts[0].Trim(), zh, en);
                    foreach (var id in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        AddSearchRow(id, zh, en);
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("ModName", "重建模组译名反查索引失败", ex);
            }
        }

        private static string? Lookup(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var trimmed = key.Trim();
            if (Map.TryGetValue(Normalize(trimmed), out var direct))
                return direct;

            foreach (var token in Tokens(trimmed))
            {
                if (Map.TryGetValue(token, out var hit))
                    return hit;
            }
            return null;
        }

        private static void EnsureSeed()
        {
            if (_seeded) return;
            lock (Map)
            {
                if (_seeded) return;
                foreach (var (key, zh, en) in Seed)
                    Add(key, zh, en);
                _seeded = true;
            }
        }

        private static async Task DownloadCacheAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            foreach (var url in RemoteSources)
            {
                try
                {
                    using var response = await Http.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode) continue;
                    var text = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (text.Length < 1000 || !text.Contains(';')) continue;
                    await File.WriteAllTextAsync(CachePath, text, Encoding.UTF8, cancellationToken);
                    return;
                }
                catch (Exception)
                {
                    // 尝试下一个源
                }
            }
        }

        private static void ParseIntoMap(string text)
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(';');
                if (parts.Length < 5) continue;
                var slug = parts[0].Trim();
                var ids = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var zh = parts[3].Trim();
                var en = parts[4].Trim();
                if (string.IsNullOrWhiteSpace(zh)) continue;
                Add(slug, zh, en);
                Add(en, zh, en);
                foreach (var id in ids)
                    Add(id, zh, en);
            }
        }

        private static void Add(string? key, string chinese, string english)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(chinese)) return;
            var normalized = Normalize(key);
            if (normalized.Length < 2) return;
            if (!Map.ContainsKey(normalized))
                Map[normalized] = chinese;
            AddSearchRow(key ?? "", chinese, english);
        }

        private static IEnumerable<string> Tokens(string value)
        {
            var stem = Path.GetFileNameWithoutExtension(value);
            yield return Normalize(stem);
            var cut = Regex.Replace(stem, @"[+_\-]?mc\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
            cut = Regex.Replace(cut, @"\d+(\.\d+)+", "");
            cut = Regex.Replace(cut, @"(fabric|forge|neoforge|quilt|bukkit)", "", RegexOptions.IgnoreCase);
            yield return Normalize(cut);
        }

        private static string Normalize(string value)
        {
            var chars = value.Trim().ToLowerInvariant()
                .Replace(" ", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace("_", "", StringComparison.Ordinal)
                .Replace("'", "", StringComparison.Ordinal)
                .Replace(".", "", StringComparison.Ordinal);
            return chars;
        }

        private static bool ContainsCjk(string text)
            => text.Any(c => c is >= '\u4e00' and <= '\u9fff');

        private static readonly (string Key, string Zh, string En)[] Seed =
        {
            ("sodium", "钠", "Sodium"),
            ("lithium", "锂", "Lithium"),
            ("iris", "光影 Iris", "Iris"),
            ("iris-shaders", "光影 Iris", "Iris Shaders"),
            ("fabric-api", "Fabric API", "Fabric API"),
            ("fabricapi", "Fabric API", "Fabric API"),
            ("modmenu", "模组菜单", "Mod Menu"),
            ("cloth-config", "Cloth Config", "Cloth Config"),
            ("jei", "JEI 物品管理器", "Just Enough Items"),
            ("just-enough-items", "JEI 物品管理器", "Just Enough Items"),
            ("rei", "REI 物品管理器", "Roughly Enough Items"),
            ("emi", "EMI 物品管理器", "EMI"),
            ("create", "机械动力", "Create"),
            ("botania", "植物魔法", "Botania"),
            ("appleskin", "苹果皮", "AppleSkin"),
            ("xaeros-minimap", "Xaero 的小地图", "Xaero's Minimap"),
            ("xaeros-world-map", "Xaero 的世界地图", "Xaero's World Map"),
            ("journeymap", "旅行地图", "JourneyMap"),
            ("simple-voice-chat", "简单语音聊天", "Simple Voice Chat"),
            ("voicechat", "简单语音聊天", "Simple Voice Chat"),
            ("indium", "铟", "Indium"),
            ("sodium-extra", "钠 Extra", "Sodium Extra"),
            ("entityculling", "实体剔除", "Entity Culling"),
            ("immediatelyfast", "立即优化", "ImmediatelyFast"),
            ("ferritecore", "铁芯", "FerriteCore"),
            ("starlight", "星光", "Starlight"),
            ("lithium", "锂", "Lithium"),
            ("optifine", "高清修复", "OptiFine"),
            ("complementary", "互补光影", "Complementary Shaders"),
            ("complementary-reimagined", "互补光影：再想象", "Complementary Reimagined"),
            ("bsl-shaders", "BSL 光影", "BSL Shaders"),
            ("distant-horizons", "远景", "Distant Horizons"),
            ("origins", "起源", "Origins"),
            ("architectury-api", "Architectury API", "Architectury API"),
            ("owo-lib", "oωo", "oωo"),
            ("jade", "玉", "Jade"),
            ("wthit", "WTHIT", "WTHIT"),
            ("litematica", "投影", "Litematica"),
            ("malilib", "MaLiLib", "MaLiLib"),
            ("tweakeroo", "Tweakeroo", "Tweakeroo"),
            ("mini-hud", "MiniHUD", "MiniHUD"),
            ("worldedit", "世界编辑", "WorldEdit"),
            ("essential", "Essential", "Essential"),
            ("replaymod", "Replay Mod", "Replay Mod"),
            ("iris", "光影 Iris", "Iris"),
            ("oculus", "Oculus 光影", "Oculus"),
            ("rubidium", "铷", "Rubidium"),
            ("embeddium", "嵌入钠", "Embeddium"),
            ("modernfix", "现代修复", "ModernFix"),
            ("krypton", "氪", "Krypton"),
            ("lazydfu", "LazyDFU", "LazyDFU"),
            ("smoothboot", "平滑启动", "Smooth Boot"),
            ("continuity", "连续纹理", "Continuity"),
            ("indium", "铟", "Indium"),
            ("yungs-better-dungeons", "YUNG 的更好地牢", "YUNG's Better Dungeons"),
            ("terralith", "大地结构", "Terralith"),
            ("biomes-o-plenty", "多样生物群系", "Biomes O' Plenty"),
            ("twilightforest", "暮色森林", "The Twilight Forest"),
            ("tinkers-construct", "匠魂", "Tinkers' Construct"),
            ("applied-energistics-2", "应用能源 2", "Applied Energistics 2"),
            ("mekanism", "通用机械", "Mekanism"),
            ("thermal-expansion", "热力膨胀", "Thermal Expansion"),
            ("immersive-engineering", "沉浸工程", "Immersive Engineering"),
            ("quark", "夸克", "Quark"),
            ("supplementaries", "补充物", "Supplementaries"),
            ("farmers-delight", "农夫乐事", "Farmer's Delight"),
            ("sophisticated-backpacks", "精妙背包", "Sophisticated Backpacks"),
            ("storage-drawers", "储物抽屉", "Storage Drawers"),
            ("iron-chests", "铁箱子", "Iron Chests"),
            ("waystones", "传送石碑", "Waystones"),
            ("comforts", "舒适用品", "Comforts"),
            ("sleep-tight", "安睡", "Sleep Tight")
        };
    }
}
