using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    public readonly record struct JavaRequirement(string Component, int MajorVersion);

    /// <summary>
    /// 按 Mojang 官方 Java Runtime 清单下载并管理 JVM（与官方启动器 / HMCL / Prism 相同）：
    /// https://launchermeta.mojang.com/v1/products/java-runtime/.../all.json
    /// 安装到游戏目录 runtime/{component}/{platform}/{component}/。
    /// </summary>
    public sealed class JavaRuntimeService
    {
        private const string ProductUrl =
            "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
        private const int BufferSize = 81920;
        private const int MaxParallel = 8;
        private static readonly HttpClient Client = CreateClient();

        public static JavaRuntimeService Instance { get; } = new();
        private static readonly Dictionary<string, int> VersionCache = new(StringComparer.OrdinalIgnoreCase);
        private static List<JavaInstallation>? ScanCache;
        private static string? ScanCacheKey;
        private static DateTime ScanCacheAt;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        public static string PlatformKey => RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "windows-arm64",
            Architecture.X86 => "windows-x86",
            _ => "windows-x64"
        };

        public static JavaRequirement ReadRequirement(JsonElement root)
        {
            if (root.TryGetProperty("javaVersion", out var java)
                && java.ValueKind == JsonValueKind.Object)
            {
                var component = java.TryGetProperty("component", out var c) ? c.GetString() : null;
                var major = java.TryGetProperty("majorVersion", out var m) && m.TryGetInt32(out var n) ? n : 0;
                if (string.IsNullOrWhiteSpace(component))
                    component = ComponentForMajor(major);
                if (major <= 0) major = MajorForComponent(component);
                return new JavaRequirement(component, major);
            }

            return new JavaRequirement("jre-legacy", 8);
        }

        public static JavaRequirement ReadRequirement(string versionJson)
        {
            using var doc = JsonDocument.Parse(versionJson);
            return ReadRequirement(doc.RootElement);
        }

        public static string ComponentForMajor(int major) => major switch
        {
            <= 8 => "jre-legacy",
            <= 16 => "java-runtime-alpha",
            <= 17 => "java-runtime-gamma",
            <= 21 => "java-runtime-delta",
            _ => "java-runtime-epsilon"
        };

        public static int MajorForComponent(string component) => component switch
        {
            "jre-legacy" => 8,
            "java-runtime-alpha" => 16,
            "java-runtime-beta" or "java-runtime-gamma" or "java-runtime-gamma-snapshot" => 17,
            "java-runtime-delta" => 21,
            "java-runtime-epsilon" => 25,
            _ => 21
        };

        public static string? FindInstalled(string gameDir, string component)
        {
            foreach (var root in RuntimeRoots(gameDir))
            {
                var platform = PlatformKey;
                var expected = Path.Combine(root, component, platform, component, "bin", "javaw.exe");
                if (File.Exists(expected)) return expected;

                var java = Path.Combine(root, component, platform, component, "bin", "java.exe");
                if (File.Exists(java)) return java;

                var componentRoot = Path.Combine(root, component);
                if (!Directory.Exists(componentRoot)) continue;
                try
                {
                    var found = Directory.GetFiles(componentRoot, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found is not null) return found;
                }
                catch (Exception) { }
            }
            return null;
        }

        public static string? FindAnyInstalled(string? gameDir = null)
            => ScanAll(gameDir).FirstOrDefault()?.Path;

        public static void InvalidateScanCache()
        {
            ScanCache = null;
            ScanCacheKey = null;
        }

        public static IReadOnlyList<JavaInstallation>? TryGetCached(string? gameDir = null)
        {
            gameDir ??= LauncherSettings.Instance.GameDirectory;
            if (ScanCache is not null
                && string.Equals(ScanCacheKey, gameDir, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - ScanCacheAt < TimeSpan.FromSeconds(30))
                return ScanCache;
            return null;
        }

        public static IReadOnlyList<JavaInstallation> ScanAll(string? gameDir = null, bool force = false)
        {
            gameDir ??= LauncherSettings.Instance.GameDirectory;
            if (!force
                && ScanCache is not null
                && string.Equals(ScanCacheKey, gameDir, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - ScanCacheAt < TimeSpan.FromSeconds(30))
                return ScanCache;

            var paths = new List<string>();

            void AddTree(string root)
            {
                try
                {
                    if (Directory.Exists(root))
                        paths.AddRange(Directory.GetFiles(root, "javaw.exe", SearchOption.AllDirectories));
                }
                catch (Exception) { }
            }

            foreach (var root in RuntimeRoots(gameDir))
                AddTree(root);
            AddTree(@"C:\Program Files\Java");
            AddTree(@"C:\Program Files\Eclipse Adoptium");
            AddTree(@"C:\Program Files\Eclipse Foundation");
            AddTree(@"C:\Program Files\Zulu");
            AddTree(@"C:\Program Files\Amazon Corretto");
            AddTree(@"C:\Program Files\BellSoft");
            AddTree(@"C:\Program Files\Liberica");
            AddTree(@"C:\Program Files\Oracle");
            AddTree(@"C:\Program Files\AdoptOpenJDK");
            AddTree(@"C:\Program Files (x86)\Java");

            try
            {
                var microsoft = @"C:\Program Files\Microsoft";
                if (Directory.Exists(microsoft))
                {
                    foreach (var dir in Directory.GetDirectories(microsoft, "jdk*"))
                        AddTree(dir);
                }
            }
            catch (Exception) { }

            try
            {
                var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
                if (Directory.Exists(programs))
                {
                    foreach (var dir in Directory.GetDirectories(programs))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.Contains("java", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("jdk", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("jre", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("adopt", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("temurin", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("zulu", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("microsoft", StringComparison.OrdinalIgnoreCase))
                            AddTree(dir);
                    }
                }
            }
            catch (Exception) { }

            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                var homeJavaw = Path.Combine(javaHome, "bin", "javaw.exe");
                if (File.Exists(homeJavaw)) paths.Add(homeJavaw);
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var javaw = Path.Combine(dir.Trim(), "javaw.exe");
                if (File.Exists(javaw)) paths.Add(javaw);
            }

            var preferred = LauncherSettings.Instance.JavaPath;
            if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
                paths.Add(preferred);

            var result = new List<JavaInstallation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string key;
                try { key = Path.GetFullPath(path); }
                catch (Exception) { continue; }
                if (!seen.Add(key)) continue;
                var major = QueryMajorVersion(path);
                if (major <= 0) continue;
                result.Add(new JavaInstallation
                {
                    Path = path,
                    Major = major,
                    Vendor = GuessVendor(path),
                    Source = GuessSource(path, gameDir)
                });
            }

            ScanCache = result
                .OrderByDescending(j => j.Major)
                .ThenBy(j => j.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ScanCacheKey = gameDir;
            ScanCacheAt = DateTime.UtcNow;
            return ScanCache;
        }

        public static string? MatchFor(JavaRequirement requirement, string? gameDir = null, string? preferredPath = null, bool? autoMatch = null)
        {
            var useAuto = autoMatch ?? LauncherSettings.Instance.AutoMatchJava;
            if (!useAuto
                && !string.IsNullOrWhiteSpace(preferredPath)
                && File.Exists(preferredPath))
                return preferredPath;

            var match = ScanAll(gameDir)
                .Where(j => MeetsRequirement(j.Path, requirement.MajorVersion))
                .OrderBy(j => j.Major)
                .ThenByDescending(j => j.Source.Contains("官方", StringComparison.Ordinal))
                .FirstOrDefault();
            return match?.Path;
        }

        public async Task<string> EnsureAsync(
            JavaRequirement requirement,
            string gameDir,
            IProgress<string>? progress = null,
            DownloadTask? task = null,
            string? preferredPath = null,
            bool? autoMatchJava = null,
            CancellationToken cancellationToken = default)
        {
            var existing = TryResolveExisting(requirement, gameDir, preferredPath, autoMatchJava);
            if (existing is not null)
            {
                Complete(task, existing, "Java 已就绪");
                return existing;
            }

            progress?.Report($"正在下载 Java {requirement.MajorVersion} 虚拟机...");
            Report(task, $"正在获取 Java {requirement.MajorVersion} 清单...", 2);

            // 清单与版本 JSON 按下载镜像策略依次尝试官方与 BMCLAPI 镜像
            var root = await GetStringWithMirrorsAsync(ProductUrl, cancellationToken)
                       ?? throw new InvalidOperationException("获取 Java 运行时清单失败");
            using var product = JsonDocument.Parse(root);
            var manifestUrl = FindManifestUrl(product.RootElement, requirement.Component)
                              ?? FindManifestUrl(product.RootElement, ComponentForMajor(requirement.MajorVersion))
                              ?? throw new InvalidOperationException($"找不到适用于 {PlatformKey} 的 Java {requirement.MajorVersion} 运行时");

            var manifestJson = await GetStringWithMirrorsAsync(manifestUrl, cancellationToken)
                               ?? throw new InvalidOperationException("Java 运行时清单无效");
            var filesNode = JsonNode.Parse(manifestJson)?["files"]?.AsObject()
                            ?? throw new InvalidOperationException("Java 运行时清单无效");

            var installDir = Path.Combine(gameDir, "runtime", requirement.Component, PlatformKey, requirement.Component);
            Directory.CreateDirectory(installDir);

            var pending = new List<(string Url, string Path, long Size)>();
            foreach (var kv in filesNode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = Path.Combine(installDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                var type = kv.Value?["type"]?.GetValue<string>();
                if (type == "directory")
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                if (type != "file") continue;

                var raw = kv.Value?["downloads"]?["raw"];
                var url = raw?["url"]?.GetValue<string>();
                var size = raw?["size"]?.GetValue<long>() ?? 0;
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (File.Exists(dest) && (size <= 0 || new FileInfo(dest).Length == size)) continue;
                pending.Add((url, dest, size));
            }

            await DownloadAllAsync(pending, task, progress, requirement, cancellationToken);

            var javaw = Path.Combine(installDir, "bin", "javaw.exe");
            if (!File.Exists(javaw))
                javaw = Directory.GetFiles(installDir, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidOperationException("Java 运行时下载完成，但未找到 javaw.exe");

            InvalidateScanCache();
            Complete(task, javaw, $"Java {requirement.MajorVersion} 已安装");
            return javaw;
        }

        private static string? TryResolveExisting(JavaRequirement requirement, string gameDir, string? preferredPath, bool? autoMatchJava = null)
        {
            var matched = MatchFor(requirement, gameDir, preferredPath, autoMatchJava);
            if (matched is not null) return matched;
            return FindInstalled(gameDir, requirement.Component);
        }

        private static string GuessVendor(string path)
        {
            var blob = path.ToLowerInvariant();
            if (blob.Contains("eclipse") || blob.Contains("adoptium") || blob.Contains("temurin")) return "Eclipse Temurin";
            if (blob.Contains("zulu")) return "Azul Zulu";
            if (blob.Contains("corretto")) return "Amazon Corretto";
            if (blob.Contains("microsoft")) return "Microsoft";
            if (blob.Contains("bellsoft") || blob.Contains("liberica")) return "BellSoft Liberica";
            if (blob.Contains("oracle")) return "Oracle";
            if (blob.Contains("java-runtime") || blob.Contains(".minecraft") && blob.Contains("runtime")) return "Mojang";
            if (blob.Contains("azul")) return "Azul";
            return "Java";
        }

        private static string GuessSource(string path, string gameDir)
        {
            if (path.StartsWith(Path.Combine(gameDir, "runtime"), StringComparison.OrdinalIgnoreCase))
                return "本游戏目录官方运行时";
            if (path.Contains(Path.Combine(".minecraft", "runtime"), StringComparison.OrdinalIgnoreCase))
                return "Minecraft 官方运行时";
            if (path.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
                return "系统安装";
            return "已检测";
        }

        public static bool MeetsRequirement(string javaPath, int major)
        {
            var actual = QueryMajorVersion(javaPath);
            if (actual <= 0) return false;
            if (major <= 8) return actual == 8;
            return actual >= major;
        }

        public static int QueryMajorVersion(string javaPath)
        {
            try
            {
                var cacheKey = Path.GetFullPath(javaPath);
                lock (VersionCache)
                {
                    if (VersionCache.TryGetValue(cacheKey, out var cached))
                        return cached;
                }
                var exe = javaPath;
                if (exe.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var java = Path.Combine(Path.GetDirectoryName(exe)!, "java.exe");
                    if (File.Exists(java)) exe = java;
                }

                var start = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var process = Process.Start(start);
                if (process is null) return 0;
                var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                process.WaitForExit(8000);
                var major = ParseMajor(text);
                lock (VersionCache) VersionCache[cacheKey] = major;
                return major;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static int ParseMajor(string text)
        {
            var match = Regex.Match(text, @"version\s+""1\.(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var legacy))
                return legacy;
            match = Regex.Match(text, @"version\s+""(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var major))
                return major;
            return 0;
        }

        private static string? FindManifestUrl(JsonElement product, string component)
        {
            foreach (var platform in new[] { PlatformKey, "windows-x64", "windows-x86" })
            {
                if (!product.TryGetProperty(platform, out var plat) || plat.ValueKind != JsonValueKind.Object)
                    continue;
                if (!plat.TryGetProperty(component, out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.TryGetProperty("manifest", out var manifest)
                        && manifest.TryGetProperty("url", out var url)
                        && url.GetString() is { Length: > 0 } href)
                        return href;
                }
            }
            return null;
        }

        private static IEnumerable<string> RuntimeRoots(string gameDir)
        {
            yield return Path.Combine(gameDir, "runtime");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".minecraft", "runtime");
        }

        private async Task DownloadAllAsync(
            List<(string Url, string Path, long Size)> pending,
            DownloadTask? task,
            IProgress<string>? progress,
            JavaRequirement requirement,
            CancellationToken ct)
        {
            if (pending.Count == 0) return;

            var total = pending.Count;
            var done = 0;
            using var gate = new SemaphoreSlim(MaxParallel);
            var errors = new List<Exception>();

            async Task DownloadOne((string Url, string Path, long Size) file)
            {
                await gate.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await DownloadFileAsync(file.Url, file.Path, ct);
                    var current = Interlocked.Increment(ref done);
                    var name = Path.GetFileName(file.Path);
                    Report(task, $"正在下载 {name}", 8 + current * 88.0 / total, $"Java {requirement.MajorVersion}  {current} / {total}");
                    if (current == 1 || current % 20 == 0 || current == total)
                        progress?.Report($"正在下载 Java {requirement.MajorVersion}  ({current}/{total})");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    lock (errors) errors.Add(ex);
                }
                finally
                {
                    gate.Release();
                }
            }

            try
            {
                await Task.WhenAll(pending.Select(DownloadOne));
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (errors.Count > 0)
                throw new InvalidOperationException($"Java 运行时下载失败：{errors[0].Message}");
        }

        private static Task DownloadFileAsync(string url, string destination, CancellationToken ct)
            => HttpDownloadHelper.DownloadFileAsync(url, destination, ct);

        /// <summary>按下载镜像策略依次尝试官方与镜像源，取第一个成功的响应文本。</summary>
        private static async Task<string?> GetStringWithMirrorsAsync(string url, CancellationToken ct)
        {
            Exception? last = null;
            foreach (var candidate in HttpDownloadHelper.ExpandUrls(url))
            {
                try
                {
                    return await Client.GetStringAsync(candidate, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            if (last is not null) LauncherLogService.Warn("Java", $"Java 清单下载失败：{last.Message}");
            return null;
        }

        private static void Report(DownloadTask? task, string detail, double progress, string? hint = null)
        {
            if (task is null) return;
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Status = "下载中";
                t.Detail = detail;
                t.Progress = Math.Clamp(progress, 0, 99);
                if (hint is not null) t.ProgressHint = hint;
            });
        }

        private static void Complete(DownloadTask? task, string path, string hint)
        {
            if (task is null) return;
            DownloadService.Instance.UpdateTask(task, t =>
            {
                t.Progress = 100;
                t.Status = "已完成";
                t.Detail = path;
                t.ProgressHint = hint;
            });
        }
    }
}
