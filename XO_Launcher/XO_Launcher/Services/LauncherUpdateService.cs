using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    public sealed class LauncherUpdateInfo
    {
        public string Version { get; init; } = "";
        public string Notes { get; init; } = "";
        public string PageUrl { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public string FileName { get; init; } = "";
        public long Size { get; init; }
        public bool IsNewer { get; init; }
        /// <summary>服务端要求强制更新（不升级则无法继续使用）。</summary>
        public bool ForceUpdate { get; init; }
        /// <summary>发布页上标注的发布日期，格式为服务端原样字符串。</summary>
        public string ReleaseDate { get; init; } = "";
        public bool CanDownload => !string.IsNullOrWhiteSpace(DownloadUrl);
    }

    /// <summary>从官方更新站点检查并下载启动器更新。</summary>
    public static class LauncherUpdateService
    {
        /// <summary>唯一允许的更新源主机（含子域，均为 HTTPS）。</summary>
        public const string UpdateHost = "update.xolauncher.dpdns.org";

        /// <summary>官方更新站点地址，即更新检查入口（站点根返回更新清单）。</summary>
        public const string UpdateSite = "https://" + UpdateHost + "/";

        /// <summary>更新清单地址，全应用唯一的更新检查入口。</summary>
        public const string ManifestUrl = UpdateSite;

        /// <summary>发布页（仅用于「打开发布页」链接，不参与更新检查）。</summary>
        public const string ReleasesPage = UpdateSite;

        private static readonly HttpClient Client = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string CurrentVersion => LauncherLogService.LauncherVersion;

        public static string UpdatesDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "updates");

        public static string PendingDirectory => Path.Combine(UpdatesDirectory, "pending");

        public static LauncherUpdateInfo? LastCheck { get; private set; }

        public static bool HasPendingInstall =>
            File.Exists(Path.Combine(PendingDirectory, "pending.json"))
            && Directory.EnumerateFiles(PendingDirectory, "XO_Launcher.exe", SearchOption.AllDirectories).Any();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/" + LauncherLogService.LauncherVersion + " (Minecraft launcher)");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        /// <summary>
        /// 判断 URL 是否属于唯一允许的更新源（HTTPS + 精确主机或子域）。
        /// 任何非 update.xolauncher.dpdns.org 的地址都会被拒绝，不发起请求。
        /// </summary>
        public static bool IsTrustedUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;
            var host = uri.Host;
            return host.Equals(UpdateHost, StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith("." + UpdateHost, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>更新清单地址恒为官方站点根，不接受任何外部覆盖。</summary>
        public static string ResolveManifestUrl() => ManifestUrl;

        /// <summary>
        /// 规整用户输入为可用的更新地址。
        /// 非官方站点的输入一律回落到官方站点根，绝不作为请求目标。
        /// </summary>
        public static string NormalizeManifestUrl(string? value)
        {
            var url = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url)) return ManifestUrl;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.TrimStart('/');
            if (!IsTrustedUrl(url)) return ManifestUrl;
            // 站点根即是更新清单入口，无须再拼接 update.json。
            var path = new Uri(url).AbsolutePath.Trim('/');
            return path.Length == 0 ? ManifestUrl : url.TrimEnd('/');
        }

        public static async Task<LauncherUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
        {
            var url = ResolveManifestUrl();
            if (!IsTrustedUrl(url))
                throw new InvalidOperationException("更新源不受信任：" + url);

            var info = await ReadManifestAsync(url, cancellationToken);
            if (info is null)
                throw new InvalidOperationException("更新服务器没有返回有效的版本信息。");
            LastCheck = info;
            return info;
        }

        public static async Task PrepareAsync(LauncherUpdateInfo info, DownloadTask task, CancellationToken cancellationToken)
        {
            if (!info.CanDownload)
                throw new InvalidOperationException("该版本没有可下载的安装包，请打开更新站点手动下载。");
            if (!IsTrustedUrl(info.DownloadUrl))
                throw new InvalidOperationException("更新包地址不受信任，已拒绝下载：" + info.DownloadUrl);

            Directory.CreateDirectory(UpdatesDirectory);
            var packagePath = Path.Combine(UpdatesDirectory, Sanitize(info.FileName));
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Status = "下载中";
                t.Detail = "正在下载启动器 " + info.Version;
                t.Progress = 2;
            });

            if (File.Exists(packagePath))
            {
                try { File.Delete(packagePath); } catch (Exception) { }
            }

            await HttpDownloadHelper.DownloadFileAsync(info.DownloadUrl, packagePath, cancellationToken,
                (read, total) =>
                {
                    DownloadService.Instance.UpdateTask(task, t =>
                    {
                        t.DownloadedBytes = read;
                        if (total > 0)
                        {
                            t.TotalBytes = total;
                            t.Progress = Math.Min(90, read * 90.0 / total);
                        }
                        t.Detail = "正在下载启动器 " + info.Version;
                    });
                });

            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Progress = 92;
                t.Detail = "正在准备安装包…";
            });

            PreparePending(packagePath, info);
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Progress = 100;
                t.Status = "已完成";
                t.Detail = "更新已下载，重启启动器即可安装。";
                t.ProgressHint = "待安装";
            });
        }

        public static void ApplyAndRestart()
        {
            if (!HasPendingInstall)
                throw new InvalidOperationException("没有已下载的更新。");

            var exePath = Environment.ProcessPath
                          ?? Path.Combine(AppContext.BaseDirectory, "XO_Launcher.exe");
            var dest = Path.GetDirectoryName(exePath)
                       ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var exeName = Path.GetFileName(exePath);
            var source = FindPayloadDirectory(PendingDirectory)
                         ?? throw new InvalidOperationException("更新包里没有找到 XO_Launcher.exe");
            var script = Path.Combine(UpdatesDirectory, "apply-update.cmd");
            var pid = Environment.ProcessId;
            var text =
                """
                @echo off
                setlocal
                set "PID={pid}"
                set "SRC={src}"
                set "DST={dst}"
                set "EXE={exe}"
                :wait
                timeout /t 1 /nobreak >nul
                tasklist /FI "PID eq %PID%" 2>nul | findstr /I /C:"%PID%" >nul
                if not errorlevel 1 goto wait
                xcopy /E /Y /Q /I "%SRC%\*" "%DST%\" >nul
                start "" "%DST%\%EXE%"
                """
                .Replace("{pid}", pid.ToString())
                .Replace("{src}", source)
                .Replace("{dst}", dest)
                .Replace("{exe}", exeName);
            File.WriteAllText(script, text, Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = UpdatesDirectory
            });
            LauncherLogService.Info("Update", "已安排安装更新并重启");
        }

        private static void PreparePending(string packagePath, LauncherUpdateInfo info)
        {
            if (Directory.Exists(PendingDirectory))
            {
                try { Directory.Delete(PendingDirectory, recursive: true); } catch (Exception) { }
            }
            Directory.CreateDirectory(PendingDirectory);

            var ext = Path.GetExtension(packagePath);
            if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(packagePath, PendingDirectory, overwriteFiles: true);
            }
            else if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(packagePath, Path.Combine(PendingDirectory, Path.GetFileName(packagePath)), overwrite: true);
            }
            else
            {
                throw new InvalidOperationException("不支持的更新包格式：" + ext);
            }

            if (FindPayloadDirectory(PendingDirectory) is null
                && Directory.EnumerateFiles(PendingDirectory, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) is { } setup)
            {
                File.WriteAllText(Path.Combine(PendingDirectory, "pending.json"),
                    JsonSerializer.Serialize(new { info.Version, Setup = setup }));
                return;
            }

            File.WriteAllText(Path.Combine(PendingDirectory, "pending.json"),
                JsonSerializer.Serialize(new { info.Version }));
        }

        public static bool TryLaunchSetupInstead()
        {
            var marker = Path.Combine(PendingDirectory, "pending.json");
            if (!File.Exists(marker)) return false;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                if (doc.RootElement.TryGetProperty("Setup", out var setup)
                    && setup.GetString() is { Length: > 0 } path
                    && File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        private static string? FindPayloadDirectory(string root)
        {
            var exe = Directory.EnumerateFiles(root, "XO_Launcher.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            return exe is null ? null : Path.GetDirectoryName(exe);
        }

        private static async Task<LauncherUpdateInfo?> ReadManifestAsync(string url, CancellationToken ct)
        {
            if (!IsTrustedUrl(url)) return null;
            using var response = await Client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var data = await JsonSerializer.DeserializeAsync<ManifestDto>(stream, JsonOptions, ct);
            if (data is null || string.IsNullOrWhiteSpace(data.Version)) return null;

            // 下载地址必须是官方站点，否则视为无效清单（防止清单被篡改后指向外部地址）。
            var downloadUrl = (data.DownloadUrl ?? "").Trim();
            if (downloadUrl.Length > 0 && !IsTrustedUrl(downloadUrl))
                downloadUrl = "";

            // 清单不提供文件名时，从下载地址末段推导。
            var file = GuessFileName(downloadUrl);

            // 清单不提供发布页，统一用官方站点。
            var pageUrl = (data.PageUrl ?? "").Trim();
            if (!IsTrustedUrl(pageUrl))
                pageUrl = ReleasesPage;

            // 最低启动器版本：低于它说明本地版本过旧，无法走常规增量更新，按强制更新处理。
            var minVersion = (data.MinLauncherVersion ?? "").Trim();
            var belowMinimum = minVersion.Length > 0
                               && !IsAtLeast(CurrentVersion, minVersion);

            return new LauncherUpdateInfo
            {
                Version = NormalizeVersionLabel(data.Version),
                Notes = data.Notes ?? "",
                PageUrl = pageUrl,
                DownloadUrl = downloadUrl,
                FileName = string.IsNullOrWhiteSpace(file) ? "XO_Launcher.exe" : file,
                Size = data.Size ?? 0,
                IsNewer = IsNewer(data.Version, CurrentVersion),
                ForceUpdate = data.ForceUpdate || belowMinimum,
                ReleaseDate = (data.ReleaseDate ?? "").Trim()
            };
        }

        /// <summary>判断 local 版本是否不低于 required 版本（用于 minLauncherVersion 校验）。</summary>
        private static bool IsAtLeast(string local, string required)
        {
            if (!TryParseVersion(local, out var l)) return false;
            if (!TryParseVersion(required, out var r)) return true;
            return l >= r;
        }

        /// <summary>从下载地址推断文件名；地址非法时返回空串（不抛异常）。</summary>
        private static string GuessFileName(string? downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return "";
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return "";
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }

        public static bool IsNewer(string remote, string local)
        {
            if (!TryParseVersion(remote, out var r)) return false;
            if (!TryParseVersion(local, out var l)) return true;
            return r > l;
        }

        private static bool TryParseVersion(string value, out Version version)
        {
            version = new Version(0, 0, 0);
            var core = (value ?? "").Trim().TrimStart('v', 'V').Split('-', '+')[0];
            if (string.IsNullOrWhiteSpace(core)) return false;
            var dots = core.Count(c => c == '.');
            if (dots == 0) core += ".0.0";
            else if (dots == 1) core += ".0";
            return Version.TryParse(core, out version!);
        }

        private static string NormalizeVersionLabel(string value)
        {
            var text = (value ?? "").Trim();
            if (text.StartsWith('v') || text.StartsWith('V'))
                text = text[1..];
            return string.IsNullOrWhiteSpace(text) ? CurrentVersion : text;
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "XO_Launcher.exe").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "XO_Launcher.exe" : clean;
        }

        /// <summary>
        /// 官方更新清单 DTO，对应服务端返回的 JSON：
        /// { name, version, releaseDate, forceUpdate, minLauncherVersion, downloadUrl }
        /// 另保留 notes / pageUrl / fileName / size 等可选扩展字段，缺失时自动推导。
        /// </summary>
        private sealed class ManifestDto
        {
            public string? Name { get; set; }
            public string? Version { get; set; }
            public string? ReleaseDate { get; set; }
            public bool ForceUpdate { get; set; }
            public string? MinLauncherVersion { get; set; }
            public string? DownloadUrl { get; set; }
            // 可选扩展字段
            public string? Notes { get; set; }
            public string? PageUrl { get; set; }
            public string? FileName { get; set; }
            public long? Size { get; set; }
        }
    }
}
