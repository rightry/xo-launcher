using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Api;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>一个待下载到资源库的文件（本体或前置）。</summary>
    public sealed record ModDownloadPlan(
        string ProjectTitle,
        ModrinthVersion Version,
        string Url,
        string Filename,
        long Size,
        bool IsDependency);

    /// <summary>一键下载/入队的结果统计。</summary>
    public sealed record LibraryEnqueueResult(int Added, int InQueue, int InLibrary)
    {
        public int Skipped => InQueue + InLibrary;

        /// <summary>
        /// 本次真正新建出来的下载任务。调用方（如 AI 工具层）可以等它们跑完再回复，
        /// 而不是只拿到「已加入队列」这种还没发生的结果。
        /// </summary>
        public List<DownloadTask> Tasks { get; init; } = new();
    }

    /// <summary>
    /// 资源库现有文件的指纹集合（文件名 + SHA-1 内容哈希）。
    /// 用于判断某个前置模组是否「已经在库里」，避免重复下载。
    /// </summary>
    public sealed class LibraryFingerprint
    {
        private readonly HashSet<string> _set;

        internal LibraryFingerprint(HashSet<string> set) => _set = set;

        /// <summary>库里是否已有与该计划「同一个文件」的内容（文件名相同或 SHA-1 相同）。</summary>
        public bool Contains(ModDownloadPlan plan)
        {
            if (!string.IsNullOrWhiteSpace(plan.Filename) && _set.Contains(plan.Filename))
                return true;
            var sha1 = Sha1Of(plan);
            return sha1 is not null && _set.Contains(sha1);
        }

        private static string? Sha1Of(ModDownloadPlan plan)
        {
            var hashes = plan.Version.PrimaryFile?.Hashes;
            if (hashes is null) return null;
            return hashes.TryGetValue("sha1", out var h) && !string.IsNullOrWhiteSpace(h)
                ? h.Trim().ToLowerInvariant()
                : null;
        }
    }

    /// <summary>
    /// 解析 Modrinth 模组的前置依赖并一键下载进「资源下载库」。
    /// 仅处理 dependency_type == required 的前置；optional/embedded/incompatible 跳过。
    /// 按 project 去重（同一模组只取一个版本），限量防止依赖环导致爆量。
    /// </summary>
    public static class ModDependencyService
    {
        private const int MaxPlans = 80;

        /// <summary>从根版本出发递归解析出「本体 + 全部必需前置」的下载计划。</summary>
        public static async Task<List<ModDownloadPlan>> ResolveAsync(
            ModrinthApi api,
            ModrinthVersion root,
            string gameVersion,
            string loader,
            CancellationToken ct = default)
        {
            var plans = new List<ModDownloadPlan>();
            var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var titleCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            async Task AddAsync(ModrinthVersion version, bool isDependency, string? projectIdHint)
            {
                if (plans.Count >= MaxPlans) return;

                var pid = !string.IsNullOrWhiteSpace(version.ProjectId) ? version.ProjectId : projectIdHint ?? "";
                if (!string.IsNullOrEmpty(pid) && !seenProjects.Add(pid)) return;

                var file = version.PrimaryFile;
                if (file is not null && !string.IsNullOrWhiteSpace(file.Url) && !string.IsNullOrWhiteSpace(file.Filename))
                {
                    var title = await GetTitleAsync(api, pid, titleCache, ct);
                    plans.Add(new ModDownloadPlan(
                        string.IsNullOrWhiteSpace(title) ? version.Name : title,
                        version, file.Url, file.Filename, file.Size, isDependency));
                }

                foreach (var dep in version.Dependencies.Where(d => d.IsRequired))
                {
                    ct.ThrowIfCancellationRequested();
                    if (plans.Count >= MaxPlans) break;

                    ModrinthVersion? depVersion = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(dep.VersionId))
                            depVersion = await api.GetVersionAsync(dep.VersionId!, ct);
                        if (depVersion is null && !string.IsNullOrWhiteSpace(dep.ProjectId)
                            && !string.IsNullOrWhiteSpace(gameVersion) && !string.IsNullOrWhiteSpace(loader))
                            depVersion = await api.GetLatestCompatibleAsync(dep.ProjectId!, gameVersion, loader, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LauncherLogService.Warn("ModDep", $"解析前置失败 project={dep.ProjectId} version={dep.VersionId}：{ex.Message}");
                    }

                    if (depVersion is not null)
                        await AddAsync(depVersion, true, dep.ProjectId);
                }
            }

            await AddAsync(root, false, null);
            return plans;
        }

        /// <summary>
        /// 扫描资源库现有文件，建立「文件名 + SHA-1」指纹集合。
        /// 建议在弹窗前调用一次，然后复用给 <see cref="EnqueueToLibrary"/>，避免重复哈希。
        /// </summary>
        public static LibraryFingerprint ScanLibrary(string projectType = ContentKind.Mod)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dir = ModLibraryService.GetDirectory(projectType);
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    set.Add(name);
                    var sha1 = TrySha1(file);
                    if (sha1 is not null) set.Add(sha1);
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("ModDep", "扫描资源库失败：" + ex.Message);
            }
            return new LibraryFingerprint(set);
        }

        /// <summary>
        /// 把下载计划入队到资源下载库。库里已存在的（文件名或 SHA-1 命中）与已在下载队列中的都会跳过。
        /// </summary>
        public static LibraryEnqueueResult EnqueueToLibrary(
            IReadOnlyList<ModDownloadPlan> plans,
            string projectType = ContentKind.Mod,
            LibraryFingerprint? fingerprint = null)
        {
            var dir = ModLibraryService.GetDirectory(projectType);
            fingerprint ??= ScanLibrary(projectType);
            var added = 0;
            var inQueue = 0;
            var inLibrary = 0;
            var tasks = new List<DownloadTask>();
            foreach (var p in plans)
            {
                if (string.IsNullOrWhiteSpace(p.Url) || string.IsNullOrWhiteSpace(p.Filename)) continue;
                if (fingerprint.Contains(p)) { inLibrary++; continue; }
                if (DownloadService.Instance.IsDuplicate(p.Filename)) { inQueue++; continue; }
                tasks.Add(DownloadService.Instance.Enqueue(p.ProjectTitle, p.Filename, p.Url, p.Size, dir, projectType));
                added++;
                // 下载时就把它说是哪些 Minecraft 版本的、Modrinth 溯源信息写进索引：
                // 事后光看文件名推不出「适配 26.3」，也没法回头查新版本。
                var sha1 = p.Version.PrimaryFile?.Hashes is { } hashes
                    && hashes.TryGetValue("sha1", out var s) ? s : "";
                var loader = p.Version.Loaders.FirstOrDefault() ?? "";
                LibraryIndexService.RecordDownload(projectType, p.Filename,
                    string.IsNullOrWhiteSpace(p.Version.ProjectId) ? "" : p.Version.ProjectId,
                    p.Version.Id, loader, p.Version.Loaders, p.ProjectTitle, p.Version.VersionNumber,
                    p.Version.GameVersions, sha1, p.Url);
            }
            return new LibraryEnqueueResult(added, inQueue, inLibrary) { Tasks = tasks };
        }

        private static string? TrySha1(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var hash = SHA1.HashData(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<string> GetTitleAsync(
            ModrinthApi api,
            string projectId,
            Dictionary<string, string> cache,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(projectId)) return "";
            if (cache.TryGetValue(projectId, out var cached)) return cached;
            var title = "";
            try
            {
                var project = await api.GetProjectAsync(projectId, ct);
                title = project?.Title ?? "";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* 标题取不到不影响下载 */ }
            cache[projectId] = title;
            return title;
        }
    }
}
