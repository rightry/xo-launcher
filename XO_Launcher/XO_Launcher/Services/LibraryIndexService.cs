using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>某个库文件被复制到了哪个实例目录。</summary>
    public sealed class LibraryInstallRecord
    {
        [JsonPropertyName("instanceDir")] public string InstanceDir { get; set; } = "";
        [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
        [JsonPropertyName("installedAt")] public DateTimeOffset InstalledAt { get; set; }
    }

    /// <summary>
    /// 资源库里一个文件的元数据：下载时记下的游戏版本与 Modrinth 溯源、
    /// 扫盘补上的 mod id / 显示名 / 模组版本，以及它被装到了哪些实例。
    /// </summary>
    public sealed class LibraryFileRecord
    {
        /// <summary>相对资源库根的路径，统一用正斜杠，如 <c>mods/fabric-api-0.161.0+26.3.jar</c>。</summary>
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = ContentKind.Mod;
        [JsonPropertyName("modId")] public string ModId { get; set; } = "";
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
        [JsonPropertyName("modVersion")] public string ModVersion { get; set; } = "";
        [JsonPropertyName("gameVersionText")] public string GameVersionText { get; set; } = "";
        [JsonPropertyName("gameVersions")] public List<string> GameVersions { get; set; } = new();
        [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";

        /// <summary>加载器列表。光影就是靠它分卡片（Iris / OptiFine），一个包常同时支持两者。</summary>
        [JsonPropertyName("loaders")] public List<string> Loaders { get; set; } = new();

        /// <summary>主加载器，查新版本时用第一个。</summary>
        [JsonPropertyName("loader")] public string Loader { get; set; } = "";

        /// <summary>Modrinth 的 project id 或 slug；没有它就没法查新版本。</summary>
        [JsonPropertyName("projectRef")] public string ProjectRef { get; set; } = "";
        [JsonPropertyName("versionId")] public string VersionId { get; set; } = "";
        [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("downloadedAt")] public DateTimeOffset DownloadedAt { get; set; }

        /// <summary>被哪个新文件取代（在资源库里更新后写入）；过了宽限期才真删。</summary>
        [JsonPropertyName("supersededBy")] public string SupersededBy { get; set; } = "";
        [JsonPropertyName("supersededAt")] public DateTimeOffset? SupersededAt { get; set; }

        [JsonPropertyName("installs")] public List<LibraryInstallRecord> Installs { get; set; } = new();

        [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
        [JsonIgnore] public bool CanCheckUpdate => !string.IsNullOrWhiteSpace(ProjectRef);
        [JsonIgnore] public string AbsolutePath => System.IO.Path.Combine(
            ModLibraryService.RootDirectory, Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 资源库元数据索引，落在资源库根目录的 <c>.xo-library.json</c>。
    ///
    /// 为什么要有它：资源库原本只是「扫目录」，光看文件名既不知道某个 jar 适配哪些
    /// Minecraft 版本、是不是同一个模组的重份，也不知道它被装进过哪些实例——
    /// 于是「按游戏版本分类」「删重复」「在库里更新并同步到实例」都无从谈起。
    /// 文件名以 <c>.</c> 开头，ModLibraryService / InstanceModService / 库指纹扫描
    /// 都会跳过它，不会被当成资源。
    /// </summary>
    public static class LibraryIndexService
    {
        public const string IndexFileName = ".xo-library.json";

        /// <summary>被取代的旧版本文件在库里保留多久，之后才自动删除。</summary>
        public static readonly TimeSpan SupersededRetention = TimeSpan.FromDays(3);

        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static Dictionary<string, LibraryFileRecord>? _records;

        public static string IndexPath => System.IO.Path.Combine(ModLibraryService.RootDirectory, IndexFileName);

        // ==================== 读写 ====================

        private static Dictionary<string, LibraryFileRecord> Load()
        {
            if (_records is not null) return _records;
            var map = new Dictionary<string, LibraryFileRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(IndexPath))
                {
                    var loaded = JsonSerializer.Deserialize<List<LibraryFileRecord>>(
                        File.ReadAllText(IndexPath), JsonOptions);
                    foreach (var rec in loaded ?? new List<LibraryFileRecord>())
                        if (!string.IsNullOrWhiteSpace(rec.Path)) map[rec.Path] = rec;
                }
            }
            catch (Exception ex)
            {
                // 索引坏了不能拖累资源库本身：重置，之后扫盘会把能读到的信息重新补上。
                LauncherLogService.Error("LibraryIndex", "读取资源库索引失败，已重置", ex);
            }
            _records = map;
            return map;
        }

        private static void SaveLocked()
        {
            if (_records is null) return;
            var path = IndexPath;
            var tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllText(tmp, JsonSerializer.Serialize(_records.Values.ToList(), JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("LibraryIndex", "写入资源库索引失败", ex);
                try { File.Delete(tmp); } catch (Exception) { /* 临时文件删不掉不影响功能 */ }
            }
        }

        public static string RelativePath(string? kind, string fileName)
            => ContentKind.LibraryFolder(kind) + "/" + fileName;

        // ==================== 查询 ====================

        public static LibraryFileRecord? Find(string? kind, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            lock (Gate)
            {
                return Load().GetValueOrDefault(RelativePath(kind, fileName));
            }
        }

        public static IReadOnlyList<LibraryFileRecord> Enumerate(string? kind)
        {
            var folder = ContentKind.LibraryFolder(kind) + "/";
            lock (Gate)
            {
                return Load().Values
                    .Where(r => r.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        // ==================== 写入 ====================

        private static LibraryFileRecord TouchLocked(string relPath, string kind)
        {
            var map = Load();
            if (!map.TryGetValue(relPath, out var rec))
            {
                rec = new LibraryFileRecord { Path = relPath, Kind = kind, DownloadedAt = DateTimeOffset.Now };
                map[relPath] = rec;
            }
            return rec;
        }

        /// <summary>下载入队时记下当时能拿到的信息（游戏版本、Modrinth 溯源、sha1）。</summary>
        public static void RecordDownload(
            string kind,
            string fileName,
            string projectRef,
            string versionId,
            string loader,
            IReadOnlyList<string>? loaders,
            string title,
            string modVersion,
            IReadOnlyList<string> gameVersions,
            string sha1,
            string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            lock (Gate)
            {
                var rec = TouchLocked(RelativePath(kind, fileName), kind);
                if (!string.IsNullOrWhiteSpace(projectRef)) rec.ProjectRef = projectRef;
                if (!string.IsNullOrWhiteSpace(versionId)) rec.VersionId = versionId;
                if (!string.IsNullOrWhiteSpace(loader)) rec.Loader = loader;
                if (loaders is { Count: > 0 })
                {
                    rec.Loaders = loaders
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (string.IsNullOrWhiteSpace(rec.Loader))
                        rec.Loader = rec.Loaders.FirstOrDefault() ?? "";
                }
                if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(rec.DisplayName))
                    rec.DisplayName = title;
                if (!string.IsNullOrWhiteSpace(modVersion)) rec.ModVersion = modVersion;
                if (!string.IsNullOrWhiteSpace(sha1)) rec.Sha1 = sha1;
                if (!string.IsNullOrWhiteSpace(downloadUrl)) rec.DownloadUrl = downloadUrl;
                if (gameVersions is { Count: > 0 })
                {
                    var list = gameVersions
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (list.Count > 0)
                    {
                        rec.GameVersions = list;
                        rec.GameVersionText = string.Join(", ", list);
                    }
                }
                SaveLocked();
            }
        }

        /// <summary>扫盘读到 jar 元数据后补齐 modId / 显示名 / 版本；不覆盖下载时已记下的字段。</summary>
        public static void RecordScan(
            string kind,
            string fileName,
            string modId,
            string displayName,
            string modVersion,
            string gameVersionText)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            lock (Gate)
            {
                var rec = TouchLocked(RelativePath(kind, fileName), kind);
                var dirty = false;

                if (!string.IsNullOrWhiteSpace(modId) && rec.ModId != modId)
                {
                    rec.ModId = modId;
                    dirty = true;
                }
                if (!string.IsNullOrWhiteSpace(displayName) && rec.DisplayName != displayName)
                {
                    rec.DisplayName = displayName;
                    dirty = true;
                }
                if (!string.IsNullOrWhiteSpace(modVersion) && rec.ModVersion != modVersion)
                {
                    rec.ModVersion = modVersion;
                    dirty = true;
                }
                if (!string.IsNullOrWhiteSpace(gameVersionText) && rec.GameVersionText != gameVersionText)
                {
                    rec.GameVersionText = gameVersionText;
                    dirty = true;
                }
                if (rec.GameVersions.Count == 0 && !string.IsNullOrWhiteSpace(gameVersionText))
                {
                    var parsed = ParseVersions(gameVersionText);
                    if (parsed.Count > 0)
                    {
                        rec.GameVersions = parsed;
                        dirty = true;
                    }
                }
                if (dirty) SaveLocked();
            }
        }

        /// <summary>安装到实例成功后记一笔（更新时靠它知道要同步去哪些版本）。</summary>
        public static void RecordInstall(string kind, string fileName, string instanceDir, string installedFileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(instanceDir)) return;
            lock (Gate)
            {
                var rec = TouchLocked(RelativePath(kind, fileName), kind);
                var norm = instanceDir.TrimEnd('\\', '/');
                var existing = rec.Installs.FirstOrDefault(i =>
                    string.Equals(i.InstanceDir.TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(i.FileName, installedFileName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.InstalledAt = DateTimeOffset.Now;
                }
                else
                {
                    rec.Installs.Add(new LibraryInstallRecord
                    {
                        InstanceDir = norm,
                        FileName = installedFileName,
                        InstalledAt = DateTimeOffset.Now
                    });
                }
                SaveLocked();
            }
        }

        /// <summary>库文件被删：连带丢掉它的记录，并把指向它的取代关系断开。</summary>
        public static void Forget(string kind, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            var rel = RelativePath(kind, fileName);
            lock (Gate)
            {
                var map = Load();
                if (!map.Remove(rel)) return;
                foreach (var other in map.Values.Where(o => o.SupersededBy == rel).ToList())
                {
                    other.SupersededBy = "";
                    other.SupersededAt = null;
                }
                SaveLocked();
            }
        }

        /// <summary>更新后把旧库文件标记为「被取代」，进入宽限期。</summary>
        public static void MarkSuperseded(string kind, string oldFileName, string newFileName)
        {
            if (string.IsNullOrWhiteSpace(oldFileName) || string.IsNullOrWhiteSpace(newFileName)) return;
            lock (Gate)
            {
                var old = TouchLocked(RelativePath(kind, oldFileName), kind);
                old.SupersededBy = RelativePath(kind, newFileName);
                old.SupersededAt = DateTimeOffset.Now;
                SaveLocked();
            }
        }

        // ==================== 去重与清理 ====================

        /// <summary>
        /// 同一个 modId + 同一个模组版本 + 同一份适配游戏版本 = 重复。
        /// 只认拿到 modId 的条目（光影 / 资源包 / 整合包读不出元数据，不参与去重），
        /// 已标记被取代的旧文件也不算（那是留给回滚的）。
        /// </summary>
        public static IReadOnlyList<List<LibraryFileRecord>> FindDuplicates(string kind)
        {
            lock (Gate)
            {
                return Load().Values
                    .Where(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(r.ModId)
                                && string.IsNullOrEmpty(r.SupersededBy)
                                && File.Exists(r.AbsolutePath))
                    .GroupBy(r => string.Join('\n',
                        r.ModId,
                        r.ModVersion,
                        string.Join(",", r.GameVersions.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))),
                        StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.OrderByDescending(r => WriteTime(r)).ToList())
                    .ToList();
            }
        }

        /// <summary>
        /// 删掉过了宽限期的旧版本文件（资源库里更新后保留的那份）。
        /// 由资源库页面在每次刷新时调用，所以「三天后删除」不需要后台定时器。
        /// </summary>
        public static IReadOnlyList<string> PruneExpiredSuperseded()
        {
            var deleted = new List<string>();
            List<LibraryFileRecord> due;
            lock (Gate)
            {
                var cutoff = DateTimeOffset.Now - SupersededRetention;
                due = Load().Values
                    .Where(r => r.SupersededAt is { } at && at <= cutoff)
                    .ToList();
            }

            foreach (var rec in due)
            {
                try
                {
                    if (File.Exists(rec.AbsolutePath))
                    {
                        File.Delete(rec.AbsolutePath);
                        deleted.Add(rec.FileName);
                    }
                }
                catch (Exception ex)
                {
                    LauncherLogService.Warn("LibraryIndex", "清理过期旧版失败：" + rec.FileName + " · " + ex.Message);
                    continue;
                }
                Forget(rec.Kind, rec.FileName);
            }
            return deleted;
        }

        /// <summary>库里已经没有这个文件了（用户在资源管理器里删的）：丢掉残留记录。</summary>
        public static int DropMissing(string kind)
        {
            lock (Gate)
            {
                var stale = Load().Values
                    .Where(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)
                                && !File.Exists(r.AbsolutePath)
                                && !Directory.Exists(r.AbsolutePath))
                    .Select(r => r.Path)
                    .ToList();
                foreach (var key in stale) Load().Remove(key);
                if (stale.Count > 0) SaveLocked();
                return stale.Count;
            }
        }

        /// <summary>把 "1.20.1"、"1.20.1 ~ 1.21"、"26.2, 26.3" 这类文本拆成版本列表。</summary>
        public static List<string> ParseVersions(string? text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (System.Text.RegularExpressions.Match match
                     in System.Text.RegularExpressions.Regex.Matches(text, @"\d+\.\d+(\.\d+)?"))
            {
                var value = match.Value;
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
            }
            return list;
        }

        private static DateTime WriteTime(LibraryFileRecord rec)
        {
            try { return File.GetLastWriteTime(rec.AbsolutePath); }
            catch (Exception) { return DateTime.MinValue; }
        }
    }
}
