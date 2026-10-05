using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// Terracotta | 陶瓦联机内核。通过未修改的官方二进制 + 本地 HTTP API 交互（AGPL 例外条款）。
    /// </summary>
    public sealed class TerracottaService
    {
        public const string KernelVersion = "0.4.2";
        public const string ProjectUrl = "https://github.com/burningtnt/Terracotta";
        public const string Copyright = "Terracotta | 陶瓦联机 © burningtnt 与贡献者，基于 EasyTier，使用 GNU AGPL v3（启动器通过 HTTP API 调用未修改内核）。";

        private const string NodeListUrl = "https://terracotta.glavo.site/nodes";
        private static readonly HttpClient Client = HttpDownloadHelper.Shared;

        public static TerracottaService Instance { get; } = new();

        private readonly object _gate = new();
        private Process? _process;
        private int _port;
        private CancellationTokenSource? _pollCts;
        private Task? _ensureTask;
        private IReadOnlyList<string> _publicNodes = Array.Empty<string>();

        public TerracottaSnapshot Current { get; private set; } = TerracottaSnapshot.Uninitialized();
        public event Action<TerracottaSnapshot>? Changed;

        public bool IsReady => _port > 0;

        public static string InstallDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "terracotta", KernelVersion);

        public static string Classifier
        {
            get
            {
                var arch = RuntimeInformation.OSArchitecture;
                if (arch == Architecture.Arm64) return "windows-arm64";
                if (arch == Architecture.X64) return "windows-x86_64";
                throw new InvalidOperationException("当前系统架构不支持陶瓦联机内核。");
            }
        }

        public string ExecutablePath => Path.Combine(InstallDirectory, $"terracotta-{KernelVersion}-{Classifier}.exe");

        public bool IsInstalled => File.Exists(ExecutablePath);

        public Task EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_ensureTask is { IsCompleted: false })
                    return _ensureTask;
                _ensureTask = EnsureReadyCoreAsync(progress, cancellationToken);
                return _ensureTask;
            }
        }

        private async Task EnsureReadyCoreAsync(IProgress<string>? progress, CancellationToken cancellationToken)
        {
            if (_port > 0 && await ProbeAsync(_port, cancellationToken).ConfigureAwait(false))
            {
                StartPolling();
                await RefreshStateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!IsInstalled)
                await InstallAsync(progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("正在启动陶瓦联机内核…");
            await StartKernelAsync(cancellationToken).ConfigureAwait(false);
            _ = FetchNodesAsync();
            StartPolling();
            await RefreshStateAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(InstallDirectory);
            var packageName = $"terracotta-{KernelVersion}-{Classifier}-pkg.tar.gz";
            var pkgPath = Path.Combine(Path.GetTempPath(), "xo-" + packageName);
            var urls = BuildDownloadUrls();

            DownloadTask? task = null;
            await UiDispatch.RunAsync(() =>
            {
                task = DownloadService.Instance.StartInstall("陶瓦联机内核", packageName);
            }).ConfigureAwait(false);

            progress?.Report("正在下载陶瓦联机内核…");
            Exception? last = null;
            var downloaded = false;
            foreach (var url in urls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(pkgPath))
                        File.Delete(pkgPath);
                    await DownloadToFileAsync(url, pkgPath, task, cancellationToken).ConfigureAwait(false);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    LauncherLogService.Warn("Online", "陶瓦内核下载源失败：" + url + " / " + ex.Message);
                }
            }

            if (!downloaded)
            {
                var message = "无法下载陶瓦联机内核：" + (last?.Message ?? "所有镜像均失败");
                if (task is not null)
                {
                    DownloadService.Instance.UpdateTask(task, t =>
                    {
                        t.Status = "下载失败";
                        t.Detail = message;
                        t.ProgressHint = "失败";
                    });
                }
                throw new InvalidOperationException(message);
            }

            var expectedPkg = PackageHash();
            var actualPkg = HashFile(pkgPath);
            if (!string.Equals(expectedPkg, actualPkg, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("陶瓦联机安装包校验失败，请稍后重试。");

            progress?.Report("正在安装陶瓦联机内核…");
            ExtractPackage(pkgPath);
            TryDelete(pkgPath);

            if (!File.Exists(ExecutablePath))
                throw new InvalidOperationException("安装完成但未找到陶瓦联机可执行文件。");

            if (task is not null)
            {
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "已完成";
                    t.Progress = 100;
                    t.Detail = "已安装陶瓦联机 " + KernelVersion;
                    t.ProgressHint = "完成";
                });
            }
            LauncherLogService.Info("Online", "已安装陶瓦联机内核 " + KernelVersion);
        }

        public async Task HostAsync(CancellationToken cancellationToken = default)
        {
            await EnsureReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            try { await RefreshStateAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { }

            if (Current.IsHosting)
                return;

            await EnsureIdleAsync(cancellationToken).ConfigureAwait(false);
            await FetchNodesAsync().ConfigureAwait(false);
            var query = BuildQuery(("player", PlayerName()));
            await GetAsync("/state/scanning", query, cancellationToken).ConfigureAwait(false);
            Publish(Current.WithKind("host-scanning"));
        }

        public async Task JoinAsync(string room, CancellationToken cancellationToken = default)
        {
            room = RequireValidRoom(room);
            await EnsureReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            try { await RefreshStateAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { }

            if (Current.IsHosting)
            {
                if (TrySameRoom(Current.Room, room))
                    throw new InvalidOperationException("你已经是这个房间的房主。把房间码发给好友，让对方点「加入」。本机再加入会断开自己的房间。");
                throw new InvalidOperationException("当前正在主持房间 " + (Current.Room ?? "") + "。请先点「结束联机」，再加入别人的房间。");
            }

            if (Current.IsJoining && TrySameRoom(Current.Room, room))
                return;

            await EnsureIdleAsync(cancellationToken).ConfigureAwait(false);
            await FetchNodesAsync().ConfigureAwait(false);
            var guest = GuestPlayerName();
            LastGuestPlayerName = guest;
            var query = BuildQuery(("room", room), ("player", guest));
            LauncherLogService.Info("Online", "正在加入房间 " + room + "（联机名 " + guest + "）");
            await GetAsync("/state/guesting", query, cancellationToken).ConfigureAwait(false);
            Publish(Current.WithKind("guest-connecting"));
        }

        public async Task StopSessionAsync(CancellationToken cancellationToken = default)
        {
            if (!IsReady) return;
            try
            {
                await GetAsync("/state/ide", null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Online", "结束联机会话失败：" + ex.Message);
            }
            Publish(Current.WithKind("waiting"));
        }

        public void StartPolling()
        {
            lock (_gate)
            {
                if (_pollCts is not null) return;
                _pollCts = new CancellationTokenSource();
                var token = _pollCts.Token;
                _ = Task.Run(() => PollLoopAsync(token), token);
            }
        }

        public void StopPolling()
        {
            lock (_gate)
            {
                try { _pollCts?.Cancel(); }
                catch (Exception) { }
                _pollCts?.Dispose();
                _pollCts = null;
            }
        }

        public void Shutdown()
        {
            StopPolling();
            lock (_gate)
            {
                try
                {
                    if (_process is { HasExited: false })
                        _process.Kill(entireProcessTree: true);
                }
                catch (Exception) { }
                _process?.Dispose();
                _process = null;
                _port = 0;
            }
            KillKernelProcesses();
        }

        private async Task StartKernelAsync(CancellationToken cancellationToken)
        {
            if (_port > 0 && await ProbeAsync(_port, cancellationToken).ConfigureAwait(false))
                return;

            var exe = ExecutablePath;
            if (!File.Exists(exe))
                throw new InvalidOperationException("尚未安装陶瓦联机内核。");

            var dir = Path.Combine(Path.GetTempPath(), "xo-terracotta-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var portFile = Path.Combine(dir, "http");
            var discovered = 0;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--hmcl");
            psi.ArgumentList.Add(portFile);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                if (ShouldLogKernelLine(line))
                    LauncherLogService.Info("Terracotta", line);
                var parsed = ParsePort(line);
                if (parsed > 0)
                    Interlocked.Exchange(ref discovered, parsed);
            }
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            if (!process.Start())
                throw new InvalidOperationException("无法启动陶瓦联机内核。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(30);
            var exitedAt = DateTime.MinValue;
            var port = 0;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                port = Volatile.Read(ref discovered);
                if (port <= 0)
                    port = TryReadPortFile(portFile);
                if (port > 0 && await ProbeAsync(port, cancellationToken).ConfigureAwait(false))
                    break;

                if (process.HasExited)
                {
                    if (exitedAt == DateTime.MinValue)
                        exitedAt = DateTime.UtcNow;
                    else if (DateTime.UtcNow - exitedAt > TimeSpan.FromSeconds(3) && port <= 0)
                        throw new InvalidOperationException(
                            "陶瓦联机内核已退出，退出码 " + process.ExitCode + "。请查看启动器日志中的 Terracotta 输出。");
                }
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            if (port <= 0 || !await ProbeAsync(port, cancellationToken).ConfigureAwait(false))
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception) { }
                throw new InvalidOperationException("陶瓦联机内核未在时限内就绪。");
            }

            lock (_gate)
            {
                _process = process.HasExited ? null : process;
                _port = port;
            }
            LauncherLogService.Info("Online", "陶瓦联机内核已就绪，端口 " + port);
        }

        private static int TryReadPortFile(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var port) && port > 0)
                    return port;
            }
            catch (Exception)
            {
                // 文件可能仍在写入
            }
            return 0;
        }

        private static int ParsePort(string line)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                line, @"(?:127\.0\.0\.1:|\[::1\]:|port\s*=\s*)(\d{2,5})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : 0;
        }

        private static async Task<bool> ProbeAsync(int port, CancellationToken cancellationToken)
        {
            if (port <= 0) return false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await Client.GetAsync(LocalUri(port, "/state", null), cts.Token)
                    .ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void KillKernelProcesses()
        {
            var name = Path.GetFileNameWithoutExtension(ExecutablePath);
            if (string.IsNullOrWhiteSpace(name)) return;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception) { }
            }
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshStateAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LauncherLogService.Warn("Online", "刷新陶瓦状态失败：" + ex.Message);
                }

                var delay = Current.HasSession || Current.Kind is "exception" or "host-scanning" or "host-starting"
                    ? 500
                    : 2500;
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RefreshStateAsync(CancellationToken cancellationToken)
        {
            if (!IsReady) return;
            var json = await GetAsync("/state", null, cancellationToken).ConfigureAwait(false);
            var snapshot = TerracottaSnapshot.Parse(json, _port);
            Publish(snapshot);
        }

        private async Task EnsureIdleAsync(CancellationToken cancellationToken)
        {
            try
            {
                await RefreshStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            if (string.Equals(Current.Kind, "waiting", StringComparison.OrdinalIgnoreCase))
                return;

            await GetAsync("/state/ide", null, cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> GetAsync(string path, string? query, CancellationToken cancellationToken)
        {
            var port = _port;
            if (port <= 0)
                throw new InvalidOperationException("陶瓦联机内核尚未就绪。");
            using var request = new HttpRequestMessage(HttpMethod.Get, LocalUri(port, path, query))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LauncherLogService.Warn("Online",
                    "陶瓦接口 HTTP " + (int)response.StatusCode + " " + path
                    + (string.IsNullOrWhiteSpace(query) ? "" : "?" + query));
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, path, body));
            }
            return body;
        }

        private static Uri LocalUri(int port, string path, string? query)
        {
            if (!path.StartsWith('/'))
                path = "/" + path;
            var raw = "http://127.0.0.1:" + port + path;
            if (!string.IsNullOrWhiteSpace(query))
                raw += "?" + query.TrimStart('?');
            return new Uri(raw, new UriCreationOptions
            {
                DangerousDisablePathAndQueryCanonicalization = true
            });
        }

        private string BuildQuery(params (string Key, string Value)[] pairs)
        {
            var sb = new StringBuilder();
            void Add(string key, string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            }
            foreach (var (key, value) in pairs)
                Add(key, value);
            foreach (var node in _publicNodes)
                Add("public_nodes", node);
            return sb.ToString();
        }

        private static bool TrySameRoom(string? left, string right)
        {
            try
            {
                return string.Equals(RequireValidRoom(left), right, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string RequireValidRoom(string? room)
        {
            var text = NormalizeRoom(room);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("请输入房间码。");
            text = text.ToUpperInvariant();
            if (TryExtractRoom(text, out var code))
                return code;
            if (!text.StartsWith("U/", StringComparison.Ordinal))
            {
                var prefixed = text.StartsWith('U') ? "U/" + text[1..] : "U/" + text;
                if (TryExtractRoom(prefixed, out code))
                    return code;
            }

            throw new InvalidOperationException("房间码无效。请使用完整房间码，例如 U/1KB4-30GH-3U1K-9AVB。");
        }

        private static string NormalizeRoom(string? room)
        {
            if (string.IsNullOrWhiteSpace(room)) return "";
            var text = room.Trim().Replace('／', '/').Replace('\\', '/');
            text = text.Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
            return text.Trim('"', '\'', '“', '”');
        }

        private static bool TryExtractRoom(string text, out string code)
        {
            const int length = 21;
            code = "";
            if (string.IsNullOrEmpty(text) || text.Length < length)
                return false;
            for (var i = 0; i <= text.Length - length; i++)
            {
                if (text[i] != 'U' || text[i + 1] != '/') continue;
                if (!IsRoomBody(text.AsSpan(i + 2, 19))) continue;
                code = text.Substring(i, length);
                return true;
            }
            return false;
        }

        private static bool IsRoomBody(ReadOnlySpan<char> body)
        {
            if (body.Length != 19) return false;
            for (var i = 0; i < body.Length; i++)
            {
                if (i is 4 or 9 or 14)
                {
                    if (body[i] != '-') return false;
                    continue;
                }

                var ch = body[i];
                if (ch is < '0' or > '9' and not (>= 'A' and <= 'Z'))
                    return false;
                if (ch is 'I' or 'O') continue;
            }
            return true;
        }

        private static string FormatApiError(int status, string path, string body)
        {
            if (status == 400)
            {
                if (path.Contains("guesting", StringComparison.OrdinalIgnoreCase))
                    return "无法加入房间：房间码无效，或当前已有联机会话。请确认房间码完整（含 U/ 前缀），先点「结束会话」后再试。";
                if (path.Contains("scanning", StringComparison.OrdinalIgnoreCase))
                    return "无法创建房间：当前已有联机会话。请先结束会话后再创建。";
                return "陶瓦联机拒绝了这次请求。请先结束当前会话后再试。";
            }

            var text = (body ?? "").Trim();
            if (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
                return "陶瓦接口返回 HTTP " + status + "。";
            return "陶瓦接口返回 HTTP " + status + "：" + Trim(text);
        }

        private static bool ShouldLogKernelLine(string line)
        {
            if (line.Contains("[State]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[Core]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[UI]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[RoomExperiment]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Rocket has launched", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Exception", StringComparison.Ordinal))
            {
                if (line.Contains("connect to peer error", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("dns lookup", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }

            return line.Contains("Starting easytier", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Killing EasyTier", StringComparison.OrdinalIgnoreCase);
        }

        private void Publish(TerracottaSnapshot snapshot)
        {
            if (snapshot.SameAs(Current))
                return;
            Current = snapshot;
            var handler = Changed;
            if (handler is null) return;
            UiDispatch.Run(() => handler(snapshot));
        }

        private async Task FetchNodesAsync()
        {
            try
            {
                using var response = await Client.GetAsync(NodeListUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return;
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
                var list = new List<string>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("url", out var urlEl)) continue;
                    var url = urlEl.GetString();
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var region = item.TryGetProperty("region", out var regionEl) ? regionEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(region) && !string.Equals(region, "CN", StringComparison.OrdinalIgnoreCase))
                        continue;
                    list.Add(url);
                }
                _publicNodes = list;
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Online", "获取陶瓦公共节点失败：" + ex.Message);
            }
        }

        /// <summary>最近一次加入房间时使用的联机名。与房主同名时会带机器后缀，避免局域网提示名称被占用。</summary>
        public string LastGuestPlayerName { get; private set; } = "";

        private static string PlayerName()
        {
            var name = AccountStore.Instance.Selected.Username;
            return string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        }

        /// <summary>加入房间时使用的陶瓦显示名：账号名 + 本机短后缀，避免与房主撞名。</summary>
        public static string GuestPlayerName()
            => AttachMachineTag(PlayerName());

        /// <summary>启动游戏加入房间时使用的玩家名。与房主同名则改用带后缀的名字。</summary>
        public string ResolveJoinPlayName()
        {
            if (Current.IsJoining && !string.IsNullOrWhiteSpace(LastGuestPlayerName))
                return LastGuestPlayerName;

            var name = PlayerName();
            var clash = Current.Players.Any(player =>
                !string.Equals(player.Kind, "LOCAL", StringComparison.OrdinalIgnoreCase)
                && string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase));
            return clash ? AttachMachineTag(name) : name;
        }

        public static bool NamesDiffer(string? left, string? right)
            => !string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static string AttachMachineTag(string name)
        {
            var tag = MachineTag();
            var suffix = "_" + tag;
            const int max = 16;
            if (name.Length + suffix.Length <= max)
                return name + suffix;
            var keep = Math.Max(1, max - suffix.Length);
            return name[..keep] + suffix;
        }

        private static string MachineTag()
        {
            var raw = Environment.MachineName + "|" + Environment.UserName;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
            return string.Concat(alphabet[hash[0] % alphabet.Length], alphabet[hash[1] % alphabet.Length]);
        }

        private static string[] BuildDownloadUrls()
        {
            var v = KernelVersion;
            var c = Classifier;
            var file = $"terracotta-{v}-{c}-pkg.tar.gz";
            return new[]
            {
                $"https://gitee.com/burningtnt/Terracotta/releases/download/v{v}/{file}",
                $"https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{v}/{file}",
                $"https://alist.8mi.tech/d/mirror/HMCL-Terracotta/Auto/v{v}/{file}",
                $"https://github.com/burningtnt/Terracotta/releases/download/v{v}/{file}"
            };
        }

        private static async Task DownloadToFileAsync(string url, string destination, DownloadTask? task, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (task is null) continue;
                var copied = read;
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载中";
                    t.DownloadedBytes = copied;
                    t.TotalBytes = total;
                    t.Progress = total > 0 ? Math.Min(99, copied * 100.0 / total) : 0;
                    t.Detail = "陶瓦联机内核 " + KernelVersion;
                });
            }
        }

        private void ExtractPackage(string pkgPath)
        {
            Directory.CreateDirectory(InstallDirectory);
            using var file = File.OpenRead(pkgPath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
                    continue;
                var name = Path.GetFileName(entry.Name.Replace('\\', '/').TrimEnd('/'));
                if (string.IsNullOrWhiteSpace(name)) continue;
                var dest = Path.Combine(InstallDirectory, name);
                using var output = File.Create(dest);
                entry.DataStream?.CopyTo(output);
            }
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
        }

        private static string PackageHash() => Classifier switch
        {
            "windows-arm64" => "fc1077247014ac0c712469498bde2ef7f6d881d5fcb7bdd5e11ebe20218fed365be19afdb8d453a79d77b729f866058522b910741767f4df947faa891434b463",
            _ => "6a98f524d4f00373696517306af8aa50d01d55ce4eadb27e9e4bc2f882707a0b5f20d5d4c33371d1459dcf5bf144ffed9beb414202d9ccf32b11dbbfcf19d650"
        };

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        private static string Trim(string text)
            => text.Length <= 180 ? text : text[..180] + "…";
    }

    public sealed class TerracottaSnapshot
    {
        public string Kind { get; init; } = "uninitialized";
        public int Port { get; init; }
        public string? Room { get; init; }
        public string? Url { get; init; }
        public string? Difficulty { get; init; }
        public int ExceptionType { get; init; } = -1;
        public IReadOnlyList<TerracottaPlayer> Players { get; init; } = Array.Empty<TerracottaPlayer>();

        public bool IsHosting => Kind is "host-scanning" or "host-starting" or "host-ok";
        public bool IsJoining => Kind is "guest-connecting" or "guest-starting" or "guest-ok";
        public bool HasSession => IsHosting || IsJoining;

        public bool SameAs(TerracottaSnapshot other)
        {
            if (!string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                || Port != other.Port
                || ExceptionType != other.ExceptionType
                || !string.Equals(Room, other.Room, StringComparison.Ordinal)
                || !string.Equals(Url, other.Url, StringComparison.Ordinal)
                || !string.Equals(Difficulty, other.Difficulty, StringComparison.Ordinal))
                return false;
            if (Players.Count != other.Players.Count) return false;
            for (var i = 0; i < Players.Count; i++)
            {
                if (!string.Equals(Players[i].Name, other.Players[i].Name, StringComparison.Ordinal)
                    || !string.Equals(Players[i].Kind, other.Players[i].Kind, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public TerracottaSnapshot WithKind(string kind) => new()
        {
            Kind = kind,
            Port = Port,
            Room = Room,
            Url = Url,
            Difficulty = Difficulty,
            ExceptionType = ExceptionType,
            Players = Players
        };

        public static TerracottaSnapshot Uninitialized() => new() { Kind = "uninitialized" };

        public static TerracottaSnapshot Parse(string json, int port)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("state", out var state) ? state.GetString() ?? "waiting" : "waiting";
            var room = ReadString(root, "room");
            var url = ReadString(root, "url");
            var difficulty = ReadString(root, "difficulty");
            var exception = -1;
            if (root.TryGetProperty("type", out var type) && type.TryGetInt32(out var t))
                exception = t;
            var players = new List<TerracottaPlayer>();
            if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in profiles.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    players.Add(new TerracottaPlayer(
                        ReadString(item, "name") ?? "玩家",
                        ReadString(item, "kind") ?? "",
                        ReadString(item, "vendor") ?? ""));
                }
            }

            return new TerracottaSnapshot
            {
                Kind = kind,
                Port = port,
                Room = room,
                Url = url,
                Difficulty = difficulty,
                ExceptionType = exception,
                Players = players
            };
        }

        public string Headline() => Kind switch
        {
            "uninitialized" => "尚未安装陶瓦联机内核",
            "waiting" => "就绪，可以创建或加入房间",
            "host-scanning" => "正在扫描本机局域网世界…",
            "host-starting" => "正在创建房间…",
            "host-ok" => "房间已创建，把房间码发给好友即可",
            "guest-connecting" => "正在连接房间…",
            "guest-starting" => "正在建立 P2P 通道…",
            "guest-ok" => "已连接到房间",
            "exception" => ExceptionText(),
            _ => "陶瓦联机运行中"
        };

        public string Detail()
        {
            if (Kind == "guest-starting" && !string.IsNullOrWhiteSpace(Difficulty))
            {
                var level = Difficulty.ToLowerInvariant() switch
                {
                    "easiest" => "很容易打洞",
                    "simple" => "较容易打洞",
                    "medium" => "打洞难度中等",
                    "tough" => "打洞较困难，可能走中继",
                    _ => "正在评估网络质量"
                };
                return level;
            }
            if (Kind == "host-ok" && !string.IsNullOrWhiteSpace(Room))
                return "房间码 " + Room + "。本机已经在房间里，把房间码发给好友即可，不要在本机再点加入。";
            if (Kind == "guest-ok" && !string.IsNullOrWhiteSpace(Url))
                return "请在多人游戏中加入 " + Url;
            if (Kind is "host-scanning" or "host-starting")
                return "请先启动游戏，进入存档后打开「对局域网开放」。";
            return Players.Count > 0 ? "在线 " + Players.Count + " 人" : "";
        }

        private string ExceptionText() => ExceptionType switch
        {
            0 => "找不到房主。请确认对方已创建房间、游戏已对局域网开放，并且仍停留在联机页。若你自己是房主，把房间码发给好友即可，不要在本机再点加入。",
            1 => "与房主的连接被重置，请重试。",
            2 => "加入端网络组件异常退出，请结束联机后重试。",
            3 => "房主端网络组件异常退出，请结束联机后重试。",
            4 => "游戏服务器连接被重置。",
            5 => "联机协议响应无效，可能与对方启动器版本不兼容。",
            _ => "联机出现异常，请结束会话后重试。"
        };

        private static string? ReadString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    public readonly record struct TerracottaPlayer(string Name, string Kind, string Vendor);
}
