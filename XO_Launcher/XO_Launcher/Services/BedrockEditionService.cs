using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Windows.ApplicationModel;
using XO_Launcher.Api;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// Windows 基岩版：按 BedrockBoot 流程下载 APPX、以开发者模式注册，并在启动时切换已解压的版本。
    /// 专用服务器仍走 Mojang 官方 ZIP。客户端需要已购买的微软账号。
    /// </summary>
    public static class BedrockEditionService
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        public static bool IsBedrock(InstalledVersion? version)
            => version is not null && BedrockIds.IsBedrock(version.LoaderId);

        public static IReadOnlyList<InstalledVersion> ListInstalledClients(string gameDirectory)
        {
            var list = new List<InstalledVersion>();
            var versionsDir = Path.Combine(gameDirectory, "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(versionsDir))
                {
                    var id = Path.GetFileName(dir);
                    if (!BedrockIds.IsClient(id) || BedrockIds.IsServer(id)) continue;
                    if (!File.Exists(Path.Combine(dir, "AppxManifest.xml"))) continue;
                    list.Add(ReadSideloadVersion(dir, id));
                }
            }

            foreach (var pkg in QueryPackages())
            {
                if (list.Exists(v => string.Equals(v.Id, pkg.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!string.IsNullOrWhiteSpace(pkg.InstallLocation)
                    && Directory.Exists(versionsDir)
                    && pkg.InstallLocation.StartsWith(versionsDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                var instanceDir = GameInstanceLayout.GetDirectory(gameDirectory, pkg.Id);
                var meta = Directory.Exists(instanceDir) ? GameInstanceLayout.ReadMeta(instanceDir) : null;
                list.Add(new InstalledVersion
                {
                    Id = pkg.Id,
                    MinecraftVersion = pkg.Version,
                    LoaderId = pkg.Preview ? "bedrock-preview" : "bedrock",
                    LoaderVersion = pkg.Version,
                    Type = pkg.Preview ? "snapshot" : "release",
                    JsonPath = Path.Combine(instanceDir, pkg.Id + ".json"),
                    CustomName = meta?.DisplayName,
                    CustomIcon = meta?.CustomIcon
                });
            }
            return list;
        }

        public static BedrockPackageInfo? FindPackage(bool preview)
            => QueryPackages().FirstOrDefault(p => p.Preview == preview);

        public static bool IsClientInstalled(string versionId)
        {
            if (BedrockIds.IsStoreClient(versionId))
            {
                var preview = BedrockIds.IsPreviewChannel(versionId);
                return FindPackage(preview) is not null;
            }
            return IsSideloadInstalled(versionId);
        }

        public static bool IsSideloadInstalled(string versionId, string? gameDirectory = null)
        {
            var manifest = FindSideloadManifest(versionId, gameDirectory);
            return manifest is not null && File.Exists(manifest);
        }

        public static string? FindSideloadManifest(string versionId, string? gameDirectory = null)
        {
            if (!BedrockIds.IsClient(versionId) || BedrockIds.IsServer(versionId)) return null;
            var root = gameDirectory ?? LauncherSettings.Instance.GameDirectory;
            var path = Path.Combine(GameInstanceLayout.GetDirectory(root, versionId), "AppxManifest.xml");
            return File.Exists(path) ? path : null;
        }

        public static bool IsServerInstalled(string versionId, string? gameDirectory = null)
        {
            if (!BedrockIds.IsServer(versionId)) return false;
            var exe = FindServerExecutable(versionId, gameDirectory);
            return exe is not null && File.Exists(exe);
        }

        public static string? FindServerExecutable(string versionId, string? gameDirectory = null)
        {
            var root = gameDirectory ?? LauncherSettings.Instance.GameDirectory;
            var dir = GameInstanceLayout.GetDirectory(root, versionId);
            return FindServerExecutableIn(dir);
        }

        public static string? GetComMojangPath(bool preview)
        {
            var family = BedrockIds.FamilyFor(preview);
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", family, "LocalState", "games", "com.mojang");
            return Directory.Exists(path) ? path : null;
        }

        public static bool IsDeveloperModeEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
                return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int flag && flag == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task OpenDeveloperSettingsAsync()
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers")))
                Process.Start(new ProcessStartInfo("ms-settings:developers") { UseShellExecute = true });
        }

        public static async Task LaunchClientAsync(string versionId, IProgress<string>? progress = null)
        {
            if (BedrockIds.IsSideloadClient(versionId) || IsSideloadInstalled(versionId))
            {
                await LaunchSideloadAsync(versionId, progress);
                return;
            }
            await LaunchStoreClientAsync(BedrockIds.IsPreviewChannel(versionId), progress);
        }

        public static Task LaunchClientAsync(bool preview, IProgress<string>? progress = null)
            => LaunchStoreClientAsync(preview, progress);

        public static async Task LaunchSideloadAsync(string versionId, IProgress<string>? progress = null)
        {
            var dir = GameInstanceLayout.GetDirectory(LauncherSettings.Instance.GameDirectory, versionId);
            progress?.Report("正在注册该基岩版...");
            await RegisterPackageAsync(dir);
            progress?.Report("正在启动游戏...");
            var preview = BedrockIds.IsPreviewChannel(versionId);
            // 注册阶段可能已把包标识改写成 XO 独立系列（与商店版共存），
            // 启动目标必须跟着清单走，否则会拉起商店版或激活失败。
            var family = GetRegisteredFamily(dir, preview);
            var launched = await Windows.System.Launcher.LaunchUriAsync(
                new Uri(preview ? "minecraft-preview:" : "minecraft:"),
                new Windows.System.LauncherOptions
                {
                    TargetApplicationPackageFamilyName = family
                });
            if (!launched)
                throw new InvalidOperationException("已注册该版本，但未能启动。请确认开发者模式已开启。");
        }

        public static async Task LaunchStoreClientAsync(bool preview, IProgress<string>? progress = null)
        {
            progress?.Report(preview ? "正在启动 Minecraft Preview..." : "正在启动 Minecraft 基岩版...");
            var family = BedrockIds.FamilyFor(preview);
            try
            {
                var pm = new PackageManager();
                var package = pm.FindPackagesForUser(string.Empty, family).FirstOrDefault();
                if (package is not null)
                {
                    var entries = await package.GetAppListEntriesAsync();
                    var entry = entries.FirstOrDefault();
                    if (entry is not null && await entry.LaunchAsync())
                        return;
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "WinRT 启动基岩版失败，改用协议/资源管理器", ex);
            }

            var appId = preview
                ? @"shell:AppsFolder\Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe!App"
                : @"shell:AppsFolder\Microsoft.MinecraftUWP_8wekyb3d8bbwe!App";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = appId,
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "explorer 启动基岩版失败", ex);
            }

            var uri = preview ? "minecraft-preview:" : "minecraft:";
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)))
                throw new InvalidOperationException("未能启动基岩版。请先下载该版本，或从 Microsoft Store 安装正版客户端。");
        }

        public static Process LaunchServer(string versionId, IProgress<string>? progress = null)
        {
            var exe = FindServerExecutable(versionId)
                ?? throw new FileNotFoundException("未找到 bedrock_server.exe，请先下载专用服务器。");
            progress?.Report("正在启动基岩专用服务器...");
            var start = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = false
            };
            return Process.Start(start)
                ?? throw new InvalidOperationException("无法启动 bedrock_server.exe");
        }

        public static async Task OpenStoreAsync(bool preview)
        {
            var productId = preview ? BedrockIds.PreviewStoreId : BedrockIds.ReleaseStoreId;
            var uri = new Uri($"ms-windows-store://pdp/?ProductId={productId}");
            if (await Windows.System.Launcher.LaunchUriAsync(uri))
                return;

            var web = preview
                ? "https://apps.microsoft.com/detail/9P5X4QVLC2XR"
                : "https://apps.microsoft.com/detail/9NBLGGH2JHXJ";
            Process.Start(new ProcessStartInfo(web) { UseShellExecute = true });
        }

        public static async Task InstallClientAsync(
            BedrockCatalogItem item,
            DownloadTask task,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(item.Version))
                throw new InvalidOperationException("缺少版本号");
            EnsureDeveloperMode();

            var preview = string.Equals(item.Kind, "preview", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(item.Channel, "preview", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(item.Channel, "beta", StringComparison.OrdinalIgnoreCase);
            var id = string.IsNullOrWhiteSpace(item.Id) ? BedrockIds.ClientId(item.Version, preview) : item.Id;
            var gameDir = LauncherSettings.Instance.GameDirectory;
            var instanceDir = GameInstanceLayout.GetDirectory(gameDir, id);
            Directory.CreateDirectory(instanceDir);

            // 上次若在注册阶段失败，实例目录里已经解压好了游戏文件；
            // 重试时直接补注册即可，不必重新下载几百 MB 的包。
            if (File.Exists(Path.Combine(instanceDir, "AppxManifest.xml")))
            {
                Report(task, "检测到已解压的游戏文件，跳过下载...", 90);
                WriteClientMeta(instanceDir, id, item, preview);

                Report(task, "正在注册到 Windows...", 92);
                var coexistFast = await RegisterPackageAsync(instanceDir, cancellationToken);

                Report(task, "安装完成", 100);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "已完成";
                    t.Progress = 100;
                    t.Detail = $"已安装到 versions\\{id}" + (coexistFast ? "（已与商店版共存安装）" : "");
                    t.ProgressHint = "安装完成";
                });
                return;
            }

            Report(task, "正在解析 Store 下载地址...", 2);
            var url = item.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (string.IsNullOrWhiteSpace(item.UpdateId))
                    throw new InvalidOperationException("没有可用的客户端下载地址");
                url = await BedrockStoreProtocol.GetPackageUrlAsync(item.UpdateId, "1", cancellationToken);
            }

            var ext = GuessPackageExtension(url);
            // 文件名不带随机后缀：下载中断后 .tmp 留在临时目录，
            // 用户重试同一版本时可以从断点继续（几 GB 的包重下太浪费）。
            var packagePath = Path.Combine(Path.GetTempPath(), $"xo-bedrock-{item.Version}{ext}");
            try
            {
                Report(task, "正在下载基岩版客户端...", 4);
                await DownloadFileAsync(url, packagePath, task, cancellationToken, progressMax: 78);
                cancellationToken.ThrowIfCancellationRequested();

                Report(task, "正在解压游戏包...", 82);
                await Task.Run(() => ExtractAppx(packagePath, instanceDir), cancellationToken);
                if (!File.Exists(Path.Combine(instanceDir, "AppxManifest.xml")))
                    throw new InvalidOperationException("解压完成但未找到 AppxManifest.xml");

                WriteClientMeta(instanceDir, id, item, preview);

                Report(task, "正在注册到 Windows...", 92);
                var coexist = await RegisterPackageAsync(instanceDir, cancellationToken);

                Report(task, "安装完成", 100);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "已完成";
                    t.Progress = 100;
                    t.Detail = $"已安装到 versions\\{id}" + (coexist ? "（已与商店版共存安装）" : "");
                    t.ProgressHint = "安装完成";
                });
            }
            finally
            {
                try { if (File.Exists(packagePath)) File.Delete(packagePath); } catch (Exception) { }
            }
        }

        /// <summary>注册解压目录为 UWP 包。返回是否因商店版冲突改写了包标识。</summary>
        public static async Task<bool> RegisterPackageAsync(string instanceDir, CancellationToken cancellationToken = default)
        {
            EnsureDeveloperMode();
            var manifest = Path.Combine(instanceDir, "AppxManifest.xml");
            if (!File.Exists(manifest))
                throw new FileNotFoundException("未找到 AppxManifest.xml，请先下载该版本。", manifest);

            // 商店打包版与解包注册共用包标识时 Windows 直接拒绝部署
            // （「当前用户已安装此应用的打包版本。未打包版本无法替换该版本」），
            // 先按需改写 Identity Name 换独立包系列（BedrockBoot 同款解法）。
            var patched = PatchIdentityIfStoreConflict(instanceDir);

            var signature = Path.Combine(instanceDir, "AppxSignature.p7x");
            try { if (File.Exists(signature)) File.Delete(signature); } catch (Exception) { }

            var manager = new PackageManager();
            var uri = new Uri(Path.GetFullPath(manifest));
            var options = DeploymentOptions.DevelopmentMode | DeploymentOptions.ForceUpdateFromAnyVersion;
            var result = await manager.RegisterPackageAsync(uri, null, options).AsTask(cancellationToken);
            var error = result?.ErrorText;
            if (!string.IsNullOrWhiteSpace(error)
                && result?.ExtendedErrorCode is not null
                && result.ExtendedErrorCode.HResult != 0)
            {
                throw new InvalidOperationException(
                    "注册 UWP 包失败：" + error + DescribeRegisterHint(error));
            }
            return patched;
        }

        private static string DescribeRegisterHint(string errorText)
        {
            // 给常见冲突/环境问题补可读提示，避免用户只看到一串 HRESULT。
            if (errorText.Contains("打包版本", StringComparison.Ordinal)
                || errorText.Contains("禁止重新安装", StringComparison.Ordinal))
                return "\n已尝试改用独立包标识与商店版共存安装；若仍失败，可先卸载商店版 Minecraft 后重试。";
            if (errorText.Contains("开发者模式", StringComparison.Ordinal)
                || errorText.Contains("development mode", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("0x80073CFA", StringComparison.OrdinalIgnoreCase))
                return "\n需要先在系统设置中开启开发者模式。";
            return "";
        }

        /// <summary>
        /// 商店版（打包）与解包注册共用同一个包标识时，Windows 报
        /// 「当前用户已安装此应用的打包版本。未打包版本无法替换该版本」。
        /// 解法（BedrockBoot / 各基岩启动器同款）：改写 Identity Name 换一个包系列，
        /// 发布者不动（PublisherId 哈希不变），让解包版与商店版共存。
        /// 只在确实装着商店版时才改写：无冲突的机器保持原标识，
        /// LocalState（存档 / com.mojang）继续落在原包系列目录。
        /// </summary>
        private static bool PatchIdentityIfStoreConflict(string instanceDir)
        {
            var manifest = Path.Combine(instanceDir, "AppxManifest.xml");
            if (!File.Exists(manifest)) return false;
            try
            {
                var text = File.ReadAllText(manifest);
                bool preview;
                string storeName;
                if (text.Contains("Name=\"" + BedrockIds.ReleasePackageName + "\"", StringComparison.Ordinal))
                {
                    storeName = BedrockIds.ReleasePackageName;
                    preview = false;
                }
                else if (text.Contains("Name=\"" + BedrockIds.PreviewPackageName + "\"", StringComparison.Ordinal))
                {
                    storeName = BedrockIds.PreviewPackageName;
                    preview = true;
                }
                else
                {
                    return false; // 已是 XO 标识（重复注册）或其它自定义标识
                }

                if (!HasPackageForUser(BedrockIds.FamilyFor(preview))) return false;

                var patched = text.Replace(
                    "Name=\"" + storeName + "\"",
                    "Name=\"" + BedrockIds.SideloadIdentityFor(preview) + "\"");
                if (string.Equals(patched, text, StringComparison.Ordinal)) return false;
                File.WriteAllText(manifest, patched);
                LauncherLogService.Info("Bedrock",
                    "检测到商店版 " + storeName + " 已安装，已将解包版标识改写为 "
                    + BedrockIds.SideloadIdentityFor(preview) + " 以共存");
                return true;
            }
            catch (Exception ex)
            {
                // 改写失败不影响注册本身：按原标识注册，由注册结果报出原始冲突信息。
                LauncherLogService.Error("Bedrock", "改写 AppxManifest 包标识失败", ex);
                return false;
            }
        }

        private static bool HasPackageForUser(string family)
        {
            try
            {
                var pm = new PackageManager();
                foreach (var pkg in pm.FindPackagesForUser(string.Empty, family))
                    return pkg is not null;
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Bedrock", "枚举已安装包失败：" + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 从实例目录的 AppxManifest 读出实际注册的包系列名。
        /// 发布者固定为 Microsoft → PublisherId 哈希恒为 8wekyb3d8bbwe。
        /// </summary>
        private static string GetRegisteredFamily(string instanceDir, bool preview)
        {
            try
            {
                var manifest = Path.Combine(instanceDir, "AppxManifest.xml");
                if (File.Exists(manifest))
                {
                    var text = File.ReadAllText(manifest);
                    var match = Regex.Match(text, "<Identity[^>]*Name=\"([^\"]+)\"");
                    if (match.Success)
                        return match.Groups[1].Value + "_8wekyb3d8bbwe";
                }
            }
            catch (Exception)
            {
            }
            return BedrockIds.FamilyFor(preview);
        }

        public static async Task InstallServerAsync(
            BedrockCatalogItem item,
            DownloadTask task,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(item.DownloadUrl))
                throw new InvalidOperationException("没有可用的服务器下载地址");

            var gameDir = LauncherSettings.Instance.GameDirectory;
            var id = string.IsNullOrWhiteSpace(item.Id) ? BedrockIds.ServerId(item.Version) : item.Id;
            var instanceDir = GameInstanceLayout.GetDirectory(gameDir, id);
            Directory.CreateDirectory(instanceDir);

            Report(task, "正在下载基岩专用服务器...", 4);
            // 同客户端：稳定文件名支持跨任务断点续传
            var zipPath = Path.Combine(Path.GetTempPath(), $"xo-bedrock-server-{item.Version}.zip");
            try
            {
                await DownloadFileAsync(item.DownloadUrl, zipPath, task, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                Report(task, "正在解压服务器文件...", 86);
                ZipFile.ExtractToDirectory(zipPath, instanceDir, overwriteFiles: true);

                if (FindServerExecutableIn(instanceDir) is null)
                    throw new InvalidOperationException("解压完成但未找到 bedrock_server.exe");

                var jsonPath = Path.Combine(instanceDir, id + ".json");
                var json = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["type"] = item.Channel == "preview" ? "snapshot" : "release",
                    ["xoEdition"] = "bedrock-server",
                    ["mainClass"] = "bedrock_server"
                };
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(json, JsonOptions));

                var meta = GameInstanceLayout.GetOrCreateMeta(instanceDir);
                meta.MinecraftVersion = item.Version;
                meta.Loader = "bedrock-server";
                meta.LoaderVersion = item.Version;
                meta.DisplayName ??= item.Title + " " + item.Version;
                GameInstanceLayout.WriteMeta(instanceDir, meta);

                Report(task, "安装完成", 100);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "已完成";
                    t.Progress = 100;
                    t.Detail = $"已安装到 versions\\{id}";
                    t.ProgressHint = "安装完成";
                });
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (Exception) { }
            }
        }

        private static void EnsureDeveloperMode()
        {
            if (IsDeveloperModeEnabled()) return;
            throw new InvalidOperationException(
                "需要开启 Windows 开发人员模式才能并排安装基岩版。请打开设置 → 系统 → 开发者选项，启用「开发人员模式」后重试。");
        }

        private static void WriteClientMeta(string instanceDir, string id, BedrockCatalogItem item, bool preview)
        {
            var loader = preview ? "bedrock-preview" : "bedrock";
            var jsonPath = Path.Combine(instanceDir, id + ".json");
            var json = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["type"] = preview ? "snapshot" : "release",
                ["xoEdition"] = loader,
                ["updateId"] = item.UpdateId
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(json, JsonOptions));

            var meta = GameInstanceLayout.GetOrCreateMeta(instanceDir);
            meta.MinecraftVersion = item.Version;
            meta.Loader = loader;
            meta.LoaderVersion = item.Version;
            meta.DisplayName ??= item.Title + " " + item.Version;
            GameInstanceLayout.WriteMeta(instanceDir, meta);
        }

        private static InstalledVersion ReadSideloadVersion(string dir, string id)
        {
            var meta = GameInstanceLayout.ReadMeta(dir);
            var preview = BedrockIds.IsPreviewChannel(id);
            var loader = preview ? "bedrock-preview" : "bedrock";
            var version = meta?.MinecraftVersion
                          ?? id.Replace("bedrock-preview-", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("bedrock-", "", StringComparison.OrdinalIgnoreCase);
            return new InstalledVersion
            {
                Id = id,
                MinecraftVersion = version,
                LoaderId = meta?.Loader ?? loader,
                LoaderVersion = meta?.LoaderVersion ?? version,
                Type = preview ? "snapshot" : "release",
                JsonPath = Path.Combine(dir, id + ".json"),
                CustomName = meta?.DisplayName,
                CustomIcon = meta?.CustomIcon
            };
        }

        private static void ExtractAppx(string packagePath, string dest)
        {
            var ext = Path.GetExtension(packagePath);
            if (ext.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase))
            {
                var temp = Path.Combine(Path.GetTempPath(), "xo-bedrock-bundle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    ZipFile.ExtractToDirectory(packagePath, temp, overwriteFiles: true);
                    var inner = FindPreferredAppx(temp)
                        ?? throw new InvalidOperationException("APPX 包中没有可用的 x64 游戏包。");
                    ZipFile.ExtractToDirectory(inner, dest, overwriteFiles: true);
                }
                finally
                {
                    try { Directory.Delete(temp, true); } catch (Exception) { }
                }
            }
            else
            {
                ZipFile.ExtractToDirectory(packagePath, dest, overwriteFiles: true);
            }

            var signature = Path.Combine(dest, "AppxSignature.p7x");
            try { if (File.Exists(signature)) File.Delete(signature); } catch (Exception) { }
            HideFromStartMenu(dest);
        }

        private static string? FindPreferredAppx(string directory)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.appx", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(directory, "*.msix", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
                return null;
            }

            var list = files.ToList();
            return list.FirstOrDefault(f => f.Contains("x64", StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(f => f.Contains("neutral", StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault();
        }

        private static void HideFromStartMenu(string dest)
        {
            var manifest = Path.Combine(dest, "AppxManifest.xml");
            if (!File.Exists(manifest)) return;
            try
            {
                var text = File.ReadAllText(manifest);
                if (text.Contains("AppListEntry=", StringComparison.Ordinal))
                    text = Regex.Replace(text, "AppListEntry=\"[^\"]*\"", "AppListEntry=\"none\"");
                File.WriteAllText(manifest, text);
            }
            catch (Exception)
            {
                // 清单改写失败不影响安装
            }
        }

        private static string GuessPackageExtension(string url)
        {
            var path = url.Split('?', '#')[0];
            foreach (var ext in new[] { ".appxbundle", ".msixbundle", ".appx", ".msix" })
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return ext;
            }
            return ".appx";
        }

        private static async Task DownloadFileAsync(
            string url,
            string destination,
            DownloadTask task,
            CancellationToken ct,
            double progressMax = 82)
        {
            // 基岩版包 ~550MB，Store CDN 的 TLS 连接容易中途断流。这里走
            // HttpDownloadHelper：带 4 次重试 + Range 断点续传 + 停滞超时，
            // 不再用本地裸流拷贝（之前零重试，断一次整个任务就报废）。
            await HttpDownloadHelper.DownloadFileAsync(
                url,
                destination,
                ct,
                progress: (copied, total) =>
                {
                    DownloadService.Instance.UpdateTask(task, t =>
                    {
                        t.Status = "下载中";
                        t.DownloadedBytes = copied;
                        if (total > 0)
                        {
                            t.TotalBytes = total;
                            t.Progress = Math.Min(progressMax, 4 + copied * (progressMax - 4) / total);
                        }
                        t.Detail = total > 0
                            ? $"{FormatHelper.FormatBytes(copied)} / {FormatHelper.FormatBytes(total)}"
                            : FormatHelper.FormatBytes(copied);
                    });
                });
        }

        private static void Report(DownloadTask task, string detail, double progress)
        {
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Status = progress >= 100 ? "已完成" : "下载中";
                t.Progress = progress;
                t.Detail = detail;
                t.ProgressHint = detail;
            });
        }

        private static string? FindServerExecutableIn(string directory)
        {
            if (!Directory.Exists(directory)) return null;
            var direct = Path.Combine(directory, "bedrock_server.exe");
            if (File.Exists(direct)) return direct;
            try
            {
                return Directory.EnumerateFiles(directory, "bedrock_server.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IReadOnlyList<BedrockPackageInfo>? _packageCache;
        private static DateTime _packageCacheAt;

        public static void InvalidatePackageCache() => _packageCache = null;

        private static IReadOnlyList<BedrockPackageInfo> QueryPackages()
        {
            if (_packageCache is not null && DateTime.UtcNow - _packageCacheAt < TimeSpan.FromSeconds(30))
                return _packageCache;

            var list = new List<BedrockPackageInfo>();
            try
            {
                var pm = new PackageManager();
                AddPackage(list, pm, BedrockIds.ReleaseFamily, false);
                AddPackage(list, pm, BedrockIds.PreviewFamily, true);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "PackageManager 枚举失败，改用 PowerShell", ex);
            }

            if (list.Count == 0)
                list.AddRange(QueryPackagesViaPowerShell());
            _packageCache = list;
            _packageCacheAt = DateTime.UtcNow;
            return list;
        }

        private static void AddPackage(List<BedrockPackageInfo> list, PackageManager pm, string family, bool preview)
        {
            foreach (var pkg in pm.FindPackagesForUser(string.Empty, family))
            {
                list.Add(FromPackage(pkg, preview));
                return;
            }
        }

        private static BedrockPackageInfo FromPackage(Package package, bool preview)
        {
            var v = package.Id.Version;
            var version = v.Revision == 0
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            var location = "";
            try { location = package.InstalledLocation?.Path ?? ""; }
            catch (Exception) { }
            return new BedrockPackageInfo
            {
                FamilyName = package.Id.FamilyName,
                PackageName = package.Id.Name,
                Version = version,
                InstallLocation = location,
                Preview = preview
            };
        }

        private static IReadOnlyList<BedrockPackageInfo> QueryPackagesViaPowerShell()
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-AppxPackage | Where-Object { $_.Name -eq 'Microsoft.MinecraftUWP' -or $_.Name -eq 'Microsoft.MinecraftWindowsBeta' } | Select-Object Name,Version,PackageFamilyName,InstallLocation | ConvertTo-Json\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(start);
                if (process is null) return Array.Empty<BedrockPackageInfo>();
                var json = process.StandardOutput.ReadToEnd();
                process.WaitForExit(8000);
                if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BedrockPackageInfo>();

                using var doc = JsonDocument.Parse(json);
                var list = new List<BedrockPackageInfo>();
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        TryAddPsPackage(list, el);
                }
                else
                {
                    TryAddPsPackage(list, doc.RootElement);
                }
                return list;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "PowerShell 检测基岩版失败", ex);
                return Array.Empty<BedrockPackageInfo>();
            }
        }

        private static void TryAddPsPackage(List<BedrockPackageInfo> list, JsonElement el)
        {
            var name = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            var preview = name.Contains("Beta", StringComparison.OrdinalIgnoreCase)
                          || name.Contains("Preview", StringComparison.OrdinalIgnoreCase);
            if (!name.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) return;
            list.Add(new BedrockPackageInfo
            {
                PackageName = name,
                Version = el.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "",
                FamilyName = el.TryGetProperty("PackageFamilyName", out var f) ? f.GetString() ?? "" : "",
                InstallLocation = el.TryGetProperty("InstallLocation", out var loc) ? loc.GetString() ?? "" : "",
                Preview = preview || string.Equals(name, BedrockIds.PreviewPackageName, StringComparison.OrdinalIgnoreCase)
            });
        }
    }
}
