using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 资源下载库。模组、光影与资源包先保存至此，再安装到具体游戏版本。
    /// 根目录由用户在设置中指定，默认 D:\XO_Launcher\Download。
    /// </summary>
    public static class ModLibraryService
    {
        public const string DefaultDirectory = @"D:\XO_Launcher\Download";

        public static string RootDirectory
        {
            get
            {
                var dir = ResolveDirectory(LauncherSettings.Instance.DownloadDirectory);
                EnsureLayout(dir);
                return dir;
            }
        }

        public static string GetDirectory(string? projectType = ContentKind.Mod)
        {
            var dir = Path.Combine(RootDirectory, ContentKind.LibraryFolder(projectType));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string ResolveDirectory(string? configured)
        {
            var preferred = string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : configured.Trim();
            try
            {
                Directory.CreateDirectory(preferred);
                return preferred;
            }
            catch (Exception)
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "XO_Launcher", "Download");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        public static void EnsureLayout(string? root = null)
        {
            var dir = string.IsNullOrWhiteSpace(root) ? RootDirectory : root;
            Directory.CreateDirectory(dir);
            foreach (var type in new[] { ContentKind.Mod, ContentKind.Shader, ContentKind.ResourcePack, ContentKind.Modpack })
                Directory.CreateDirectory(Path.Combine(dir, ContentKind.LibraryFolder(type)));
        }

        public static IReadOnlyList<InstanceModItem> Scan(string? projectType = ContentKind.Mod)
        {
            var dir = GetDirectory(projectType);
            var items = new List<InstanceModItem>();
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                if (!IsContentFile(name, projectType)) continue;
                var info = new FileInfo(file);
                if (info.Length <= 0) continue;
                var (modId, display, version, gameVersions) = ModNameTranslationService.DescribeFileFull(file, name);
                var formats = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? ModMetadataReader.ReadPackFormats(file)
                    : "";
                // 扫盘顺手把 jar 里的元数据写进资源库索引：按版本分组、去重、
                // 「在库里更新再同步到实例」都靠这些字段，光看文件名是推不出来的。
                LibraryIndexService.RecordScan(projectType ?? ContentKind.Mod, name, modId, display, version, gameVersions);
                items.Add(new InstanceModItem
                {
                    FilePath = file,
                    Name = name,
                    DisplayName = display,
                    Version = version,
                    GameVersions = gameVersions,
                    PackFormats = formats,
                    IsEnabled = true,
                    IsLibraryItem = true,
                    SizeText = FormatSize(info.Length)
                });
            }

            if (projectType is ContentKind.Shader or ContentKind.ResourcePack)
            {
                foreach (var folder in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(folder);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                    items.Add(new InstanceModItem
                    {
                        FilePath = folder,
                        Name = name,
                        DisplayName = ModNameTranslationService.Translate(name, name),
                        IsEnabled = true,
                        IsDirectory = true,
                        IsLibraryItem = true,
                        SizeText = "文件夹"
                    });
                }
            }

            return items
                .OrderByDescending(m => File.GetLastWriteTime(m.FilePath))
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static (int Installed, int Skipped) InstallToInstance(
            string instanceDir,
            string? projectType,
            IReadOnlyList<InstanceModItem> selected)
        {
            var destDir = Path.Combine(instanceDir, ContentKind.LibraryFolder(projectType));
            Directory.CreateDirectory(destDir);
            var installed = 0;
            var skipped = 0;
            foreach (var item in selected)
            {
                if (string.IsNullOrWhiteSpace(item.FilePath)) continue;
                var exists = item.IsDirectory ? Directory.Exists(item.FilePath) : File.Exists(item.FilePath);
                if (!exists) continue;
                var dest = Path.Combine(destDir, item.Name);
                if (ExistsAtDestination(dest))
                {
                    skipped++;
                    continue;
                }
                CopyEntry(item, dest);
                installed++;
                // 记下「这条库文件装到了这个实例」：资源库里更新模组时要反查它同步过去。
                LibraryIndexService.RecordInstall(projectType ?? ContentKind.Mod, item.Name, instanceDir, item.Name);
            }
            return (installed, skipped);
        }

        /// <summary>
        /// 把已安装到某个版本的内容反向收进资源库（「加入资源库」）。
        /// 同名文件已存在时跳过，不覆盖。
        /// </summary>
        /// <returns>(是否已加入, 是否因同名被跳过)。</returns>
        public static (bool Added, bool Skipped) AddToLibrary(
            InstanceModItem item, string? projectType = ContentKind.Mod)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) || string.IsNullOrWhiteSpace(item.Name))
                return (false, false);
            var exists = item.IsDirectory ? Directory.Exists(item.FilePath) : File.Exists(item.FilePath);
            if (!exists) return (false, false);

            var dest = Path.Combine(GetDirectory(projectType), item.Name);
            if (ExistsAtDestination(dest)) return (false, true);
            CopyEntry(item, dest);
            return (true, false);
        }

        /// <summary>
        /// 从资源库中删除一条已下载的内容（不触碰任何版本目录）。
        /// <paramref name="projectType"/> 决定索引里该忘掉哪条记录，省略时按模组处理。
        /// </summary>
        public static void DeleteFromLibrary(InstanceModItem item, string? projectType = null)
        {
            if (item.IsDirectory)
            {
                if (Directory.Exists(item.FilePath))
                    Directory.Delete(item.FilePath, recursive: true);
            }
            else if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
            LibraryIndexService.Forget(projectType ?? ContentKind.Mod, item.Name);
        }

        /// <summary>
        /// 按 mod id 找出哪些 Java 实例装过这个模组（含被改过文件名、被禁用的情况）。
        /// 索引只记「通过资源库安装」的动作，用户手动拷进去的模组没有记录，
        /// 所以在资源库里更新模组时要靠这一步把实例扫出来，才能在弹窗里让用户确认同步目标。
        /// </summary>
        public static List<(InstalledVersion Version, string ContentDir, List<string> Files)> FindInstancesWithMod(
            string kind, string modId)
        {
            var hits = new List<(InstalledVersion, string, List<string>)>();
            if (string.IsNullOrWhiteSpace(modId)) return hits;
            if (!ContentKind.IsInstallable(kind)) return hits;

            var gameDir = LauncherSettings.Instance.GameDirectory;
            foreach (var version in InstalledVersionService.Scan().Where(v => !v.IsBedrock))
            {
                var contentDir = GameInstanceLayout.ResolveContentRoot(
                    gameDir, GameInstanceLayout.GetDirectory(gameDir, version.Id));
                var folder = Path.Combine(contentDir, ContentKind.LibraryFolder(kind));
                if (!Directory.Exists(folder)) continue;

                var files = new List<string>();
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    // 被禁用的模组文件叫 xxx.jar.disabled，ModMetadataReader 读时会自己剥掉后缀
                    var (id, _, _, _) = ModNameTranslationService.DescribeFileFull(file, name);
                    if (string.Equals(id, modId, StringComparison.OrdinalIgnoreCase)) files.Add(name);
                }
                if (files.Count > 0) hits.Add((version, contentDir, files));
            }
            return hits;
        }

        private static void CopyEntry(InstanceModItem item, string dest)
        {
            if (item.IsDirectory)
            {
                CopyDirectory(item.FilePath, dest);
                return;
            }
            File.Copy(item.FilePath, dest, overwrite: false);
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
            foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }

        private static bool ExistsAtDestination(string dest)
            => File.Exists(dest) || File.Exists(dest + ".disabled")
               || Directory.Exists(dest) || Directory.Exists(dest + ".disabled");

        private static bool IsContentFile(string name, string? projectType)
        {
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                return false;
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase))
                return true;
            return projectType is ContentKind.Shader or ContentKind.ResourcePack;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        }
    }
}
