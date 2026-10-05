using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Api;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 安装 Modrinth 整合包（.mrpack）：先装对应游戏版本与加载器，再下载包内模组并写入 overrides。
    /// </summary>
    public static class ModpackInstallService
    {
        private const int ModParallelism = 16;

        public static async Task<string> InstallAsync(
            string packTitle,
            ModrinthVersion version,
            string mrpackUrl,
            DownloadTask task,
            CancellationToken cancellationToken = default,
            string? iconUrl = null)
        {
            var work = Path.Combine(Path.GetTempPath(), "xo-modpack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var packPath = Path.Combine(work, "pack.mrpack");
            var steps = new[]
            {
                "下载整合包",
                "解析整合包",
                "安装游戏与加载器",
                "下载模组",
                "写入附加文件",
                "完成安装"
            };
            try
            {
                BeginStep(task, steps, 0, "正在下载整合包…", 2);
                await HttpDownloadHelper.DownloadFileAsync(mrpackUrl, packPath, cancellationToken,
                    status: msg => Report(task, msg, 4));

                BeginStep(task, steps, 1, "正在解析整合包…", 8);
                Report(task, "正在解析整合包…", 8);
                ZipFile.ExtractToDirectory(packPath, work, overwriteFiles: true);
                var indexPath = Path.Combine(work, "modrinth.index.json");
                if (!File.Exists(indexPath))
                    throw new InvalidOperationException("整合包缺少 modrinth.index.json。");

                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken));
                var root = doc.RootElement;
                var deps = root.TryGetProperty("dependencies", out var depEl) && depEl.ValueKind == JsonValueKind.Object
                    ? depEl
                    : default;
                var mcVersion = ReadDep(deps, "minecraft");
                if (string.IsNullOrWhiteSpace(mcVersion))
                    mcVersion = version.GameVersions.FirstOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(mcVersion))
                    throw new InvalidOperationException("整合包没有标明 Minecraft 版本。");

                var (loader, loaderVersion) = ReadLoader(deps, version);
                BeginStep(task, steps, 2,
                    "正在安装 Minecraft " + mcVersion + (string.IsNullOrWhiteSpace(loader) ? "" : " + " + FormatHelper.FormatLoaderName(loader)) + "…",
                    12);

                var vanilla = await ResolveVanillaAsync(mcVersion, cancellationToken);
                var build = await ResolveLoaderBuildAsync(loader, loaderVersion, mcVersion, cancellationToken);
                var instanceName = string.IsNullOrWhiteSpace(packTitle) ? mcVersion : packTitle + " " + mcVersion;
                var installer = new GameInstallService();
                var instanceId = await installer.InstallAsync(
                    vanilla,
                    string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader,
                    build,
                    task,
                    instanceName,
                    installFabricApi: false,
                    completeTask: false,
                    cancellationToken);
                InstalledVersionService.Invalidate();
                LauncherSettings.Instance.SelectedVersion = instanceId;

                var gameDir = LauncherSettings.Instance.GameDirectory;
                var instanceDir = GameInstanceLayout.GetDirectory(gameDir, instanceId);
                var contentRoot = GameInstanceLayout.ResolveContentRoot(gameDir, instanceDir);
                GameInstanceLayout.Ensure(instanceDir);

                var files = ReadPackFiles(root);
                var rootFull = Path.GetFullPath(contentRoot);
                var downloads = new List<(string Url, string Path)>();
                foreach (var file in files)
                {
                    var dest = Path.GetFullPath(Path.Combine(contentRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                        continue;
                    downloads.Add((file.Url, dest));
                }

                var total = Math.Max(1, downloads.Count);
                BeginStep(task, steps, 3,
                    downloads.Count == 0 ? "整合包没有需要下载的模组" : $"正在并行下载 {downloads.Count} 个模组…",
                    70);
                if (downloads.Count > 0)
                {
                    Report(task, $"正在并行下载 {downloads.Count} 个模组…", 70);
                    await HttpDownloadHelper.DownloadManyAsync(
                        downloads,
                        cancellationToken,
                        ModParallelism,
                        (done, count, name) =>
                            Report(task, $"正在下载模组 {done}/{count}：{name}", 70 + done * 25.0 / Math.Max(1, count)));
                }

                BeginStep(task, steps, 4, "正在写入 overrides…", 96);
                Report(task, "正在写入整合包附加文件…", 96);
                CopyOverride(Path.Combine(work, "overrides"), contentRoot);
                CopyOverride(Path.Combine(work, "client-overrides"), contentRoot);

                var iconName = await SavePackIconAsync(instanceDir, iconUrl, work, contentRoot, cancellationToken);
                if (!string.IsNullOrWhiteSpace(iconName))
                {
                    var meta = GameInstanceLayout.GetOrCreateMeta(instanceDir);
                    meta.CustomIcon = iconName;
                    GameInstanceLayout.WriteMeta(instanceDir, meta);
                }

                var library = ModLibraryService.GetDirectory(ContentKind.Modpack);
                Directory.CreateDirectory(library);
                var saved = Path.Combine(library, GameInstanceLayout.Sanitize(packTitle + "-" + version.VersionNumber) + ".mrpack");
                File.Copy(packPath, saved, overwrite: true);

                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Progress = 100;
                    t.Status = "已完成";
                    t.Detail = instanceDir;
                    t.ProgressHint = "整合包已安装";
                    t.ApplyStep(steps, steps.Length);
                });
                LauncherLogService.Info("Modpack", $"已安装 {packTitle}（{mcVersion} / {loader}）→ {instanceId}");
                return instanceId;
            }
            finally
            {
                TryDelete(work);
            }
        }

        /// <summary>
        /// 从「分享链接」还原出的 <see cref="XoPackManifest"/> 安装整合包：
        /// 先装对应游戏版本与加载器，再按清单在线下载模组，最后写回内联的 config。
        /// 与 <see cref="InstallAsync"/>（吃 .mrpack 文件）共用游戏/加载器安装逻辑。
        /// </summary>
        public static async Task<string> InstallFromManifestAsync(
            XoPackManifest manifest,
            DownloadTask task,
            CancellationToken cancellationToken = default)
        {
            var steps = new[]
            {
                "安装游戏与加载器",
                "下载模组",
                "写入配置文件",
                "完成安装"
            };

            var mcVersion = manifest.GameVersion;
            if (string.IsNullOrWhiteSpace(mcVersion))
                throw new InvalidOperationException("清单没有标明 Minecraft 版本。");
            var loader = string.IsNullOrWhiteSpace(manifest.Loader) ? "vanilla" : manifest.Loader;

            BeginStep(task, steps, 0,
                "正在安装 Minecraft " + mcVersion
                + (loader == "vanilla" ? "" : " + " + FormatHelper.FormatLoaderName(loader)) + "…", 10);

            var vanilla = await ResolveVanillaAsync(mcVersion, cancellationToken);
            var build = await ResolveLoaderBuildAsync(loader, manifest.LoaderVersion ?? "", mcVersion, cancellationToken);
            var packTitle = string.IsNullOrWhiteSpace(manifest.Name) ? mcVersion : manifest.Name;
            var instanceName = packTitle + " " + mcVersion;
            var installer = new GameInstallService();
            var instanceId = await installer.InstallAsync(
                vanilla,
                loader,
                build,
                task,
                instanceName,
                installFabricApi: false,
                completeTask: false,
                cancellationToken);
            InstalledVersionService.Invalidate();
            LauncherSettings.Instance.SelectedVersion = instanceId;

            var gameDir = LauncherSettings.Instance.GameDirectory;
            var instanceDir = GameInstanceLayout.GetDirectory(gameDir, instanceId);
            var contentRoot = GameInstanceLayout.ResolveContentRoot(gameDir, instanceDir);
            GameInstanceLayout.Ensure(instanceDir);

            var modsDir = Path.Combine(contentRoot, "mods");
            Directory.CreateDirectory(modsDir);
            var downloads = manifest.Mods
                .Where(m => !string.IsNullOrWhiteSpace(m.Url) && !string.IsNullOrWhiteSpace(m.FileName))
                .Select(m => (m.Url, Path.Combine(modsDir, Path.GetFileName(m.FileName))))
                .ToList();

            BeginStep(task, steps, 1,
                downloads.Count == 0 ? "清单没有需要下载的模组" : $"正在并行下载 {downloads.Count} 个模组…", 60);
            if (downloads.Count > 0)
            {
                await HttpDownloadHelper.DownloadManyAsync(
                    downloads,
                    cancellationToken,
                    ModParallelism,
                    (done, count, name) =>
                        Report(task, $"正在下载模组 {done}/{count}：{name}", 60 + done * 35.0 / Math.Max(1, count)));
            }

            BeginStep(task, steps, 2, "正在写入配置文件…", 96);
            WriteConfig(manifest, contentRoot);

            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Progress = 100;
                t.Status = "已完成";
                t.Detail = instanceDir;
                t.ProgressHint = "整合包已安装";
                t.ApplyStep(steps, steps.Length);
            });
            LauncherLogService.Info("Modpack",
                $"已从链接安装 {packTitle}（{mcVersion} / {loader}，{downloads.Count} 个模组）→ {instanceId}");
            return instanceId;
        }

        /// <summary>把清单里内联的 config（base64）写回内容根目录，带路径穿越防护。</summary>
        private static void WriteConfig(XoPackManifest manifest, string contentRoot)
        {
            if (manifest.Config is null || manifest.Config.Count == 0) return;
            var rootFull = Path.GetFullPath(contentRoot);
            foreach (var kv in manifest.Config)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                try
                {
                    var rel = kv.Key.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var dest = Path.GetFullPath(Path.Combine(rootFull, rel));
                    if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                        continue; // 跳过 ../ 之类的越界路径
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, Convert.FromBase64String(kv.Value));
                }
                catch (Exception ex)
                {
                    LauncherLogService.Error("Modpack", "写入配置文件失败：" + kv.Key, ex);
                }
            }
        }

        private static async Task<MinecraftVersion> ResolveVanillaAsync(string mcVersion, CancellationToken ct)
        {
            var api = new MojangApi();
            var manifest = await api.GetVersionManifestAsync(ct);
            var found = manifest.Versions.FirstOrDefault(v =>
                string.Equals(v.Id, mcVersion, StringComparison.OrdinalIgnoreCase));
            if (found is null || string.IsNullOrWhiteSpace(found.Url))
                throw new InvalidOperationException("找不到游戏版本 " + mcVersion + "。");
            return found;
        }

        private static async Task<LoaderBuild?> ResolveLoaderBuildAsync(
            string loader, string loaderVersion, string mcVersion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(loader) || loader == "vanilla")
                return null;
            var api = new LoaderMetaApi();
            var builds = await api.GetBuildsAsync(loader, mcVersion, ct);
            var match = builds.FirstOrDefault(b =>
                string.Equals(b.Version, loaderVersion, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.MavenVersion, loaderVersion, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            if (!string.IsNullOrWhiteSpace(loaderVersion))
                return SynthesizeLoaderBuild(loader, loaderVersion, mcVersion);
            return builds.FirstOrDefault(b => b.Stable) ?? builds.FirstOrDefault();
        }

        private static LoaderBuild SynthesizeLoaderBuild(string loader, string loaderVersion, string mcVersion)
        {
            var maven = loaderVersion;
            var installer = "";
            if (loader == "forge")
            {
                maven = loaderVersion.StartsWith(mcVersion + "-", StringComparison.Ordinal)
                    ? loaderVersion
                    : mcVersion + "-" + loaderVersion;
                installer = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{maven}/forge-{maven}-installer.jar";
            }
            else if (loader == "neoforge")
            {
                installer = loaderVersion.Contains('-', StringComparison.Ordinal)
                    ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{loaderVersion}/forge-{loaderVersion}-installer.jar"
                    : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
            }

            return new LoaderBuild
            {
                Loader = loader,
                Version = loaderVersion,
                MavenVersion = maven,
                Display = loaderVersion,
                Stable = true,
                InstallerUrl = installer
            };
        }

        private static (string Loader, string Version) ReadLoader(JsonElement deps, ModrinthVersion version)
        {
            if (deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var (key, loader) in new[]
                {
                    ("fabric-loader", "fabric"),
                    ("quilt-loader", "quilt"),
                    ("neoforge", "neoforge"),
                    ("forge", "forge")
                })
                {
                    var value = ReadDep(deps, key);
                    if (!string.IsNullOrWhiteSpace(value))
                        return (loader, value);
                }
            }

            var fromVersion = version.Loaders.FirstOrDefault(l =>
                l is "fabric" or "quilt" or "forge" or "neoforge");
            return (fromVersion ?? "vanilla", "");
        }

        private static string ReadDep(JsonElement deps, string name)
        {
            if (deps.ValueKind != JsonValueKind.Object) return "";
            return deps.TryGetProperty(name, out var value) ? value.GetString()?.Trim() ?? "" : "";
        }

        private static List<(string Path, string Url)> ReadPackFiles(JsonElement root)
        {
            var list = new List<(string Path, string Url)>();
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                if (file.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object
                    && env.TryGetProperty("client", out var client)
                    && string.Equals(client.GetString(), "unsupported", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = file.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                var url = "";
                if (file.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in downloads.EnumerateArray())
                    {
                        url = item.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(url)) break;
                    }
                }
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url))
                    continue;
                list.Add((path, url));
            }
            return list;
        }

        private static async Task<string?> SavePackIconAsync(
            string instanceDir,
            string? iconUrl,
            string workDir,
            string contentRoot,
            CancellationToken cancellationToken)
        {
            foreach (var name in new[] { "icon.png", "pack.png", "icon.jpg", "icon.jpeg" })
            {
                foreach (var folder in new[] { workDir, Path.Combine(workDir, "overrides"), contentRoot, instanceDir })
                {
                    var src = Path.Combine(folder, name);
                    if (!File.Exists(src) || new FileInfo(src).Length < 24) continue;
                    var destName = "pack-icon" + Path.GetExtension(src).ToLowerInvariant();
                    var dest = Path.Combine(instanceDir, destName);
                    if (!string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                        File.Copy(src, dest, overwrite: true);
                    return destName;
                }
            }

            var urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(iconUrl))
            {
                if (iconUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    urls.Add(iconUrl[..^5] + ".png");
                urls.Add(iconUrl);
            }

            foreach (var url in urls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var response = await HttpDownloadHelper.Shared.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode) continue;
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var ext = DetectImageExtension(bytes);
                    if (ext is null) continue;
                    var destName = "pack-icon" + ext;
                    await File.WriteAllBytesAsync(Path.Combine(instanceDir, destName), bytes, cancellationToken);
                    return destName;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                }
            }

            return VersionIconService.FindInstanceIcon(instanceDir) is { } existing
                ? Path.GetFileName(existing)
                : null;
        }

        private static string? DetectImageExtension(byte[] bytes)
        {
            if (bytes.Length < 24) return null;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return ".png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return ".jpg";
            if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
                return ".gif";
            // WebP（RIFF....WEBP）：保存后由 RemoteIconLoader.TryCreateFromFile 解码
            //（本机 WIC 支持 WebP 时显示，否则回退占位符）。
            if (bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
                return ".webp";
            return null;
        }

        private static void CopyOverride(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(from, file);
                var dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        private static void BeginStep(DownloadTask task, IReadOnlyList<string> steps, int index, string extra, double progress)
        {
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Status = "下载中";
                t.Progress = Math.Clamp(progress, 0, 99);
                t.ApplyStep(steps, index, extra);
            });
        }

        private static void Report(DownloadTask task, string detail, double progress)
        {
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Status = "下载中";
                t.Detail = detail;
                t.Progress = Math.Clamp(progress, 0, 99);
                t.ProgressHint = detail;
                t.RefreshStepExtra(detail);
            });
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception) { }
        }
    }
}
