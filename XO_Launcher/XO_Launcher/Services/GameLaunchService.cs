using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// Minecraft 游戏启动器。
    /// 负责解析版本元数据（version JSON）、补齐客户端与库文件、按系统规则筛选库、
    /// 解压原生库，最后以正确的 JVM 参数启动游戏进程。
    /// </summary>
    public sealed class GameLaunchService
    {
        private const int BufferSize = 81920;
        private const string LauncherName = "XO_Launcher";
        private const string LauncherVersion = LauncherLogService.LauncherVersion;
        private readonly IProgress<string>? _progress;

        public GameLaunchService(IProgress<string>? progress = null)
        {
            _progress = progress;
        }

        /// <summary>
        /// 自动查找可用的 Java 可执行文件。
        /// 依次检查：Mojang 官方运行时、常见 JDK 安装目录、PATH 中的 java。
        /// </summary>
        public static string? FindJavaPath()
            => JavaRuntimeService.ScanAll().FirstOrDefault()?.Path;

        /// <summary>按已安装或 Mojang 清单中的版本启动 Minecraft。</summary>
        public Task<Process> LaunchAsync(
            MinecraftVersion version,
            string javaPath,
            string gameDirectory,
            string username,
            long memoryMB,
            CancellationToken cancellationToken = default)
            => LaunchAsync(version.Id, javaPath, gameDirectory, username, memoryMB, version.Url, cancellationToken: cancellationToken);

        public async Task<Process> LaunchAsync(
            string versionId,
            string? javaPath,
            string gameDirectory,
            string username,
            long memoryMB,
            string? manifestUrl = null,
            DownloadTask? javaTask = null,
            bool? autoMatchJava = null,
            string? extraJvmArgs = null,
            string? extraGameArgs = null,
            int? windowWidth = null,
            int? windowHeight = null,
            string windowMode = "default",
            string? authUuid = null,
            string? authAccessToken = null,
            string? userType = null,
            string? authXuid = null,
            string? clientId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentNullException(nameof(versionId));

            var instanceDir = GameInstanceLayout.GetDirectory(gameDirectory, versionId);
            GameInstanceLayout.EnsureLaunchSupport(instanceDir);
            if (GameInstanceLayout.UsesIsolatedContent(instanceDir))
                GameInstanceLayout.Ensure(instanceDir);
            var runtimeDir = GameInstanceLayout.ResolveContentRoot(gameDirectory, instanceDir);
            var nativesDir = Path.Combine(instanceDir, "natives");
            var librariesDir = Path.Combine(gameDirectory, "libraries");
            var assetsDir = Path.Combine(gameDirectory, "assets");
            WindowsLibraryFilter.EnsureNativesLayout(nativesDir);
            Directory.CreateDirectory(librariesDir);
            Directory.CreateDirectory(Path.Combine(assetsDir, "indexes"));

            Report($"正在读取 {versionId} 版本配置...");
            var chain = await LoadVersionChainAsync(versionId, manifestUrl, gameDirectory, cancellationToken);

            JavaRequirement? javaRequirement = null;
            foreach (var doc in chain)
            {
                if (doc.RootElement.TryGetProperty("javaVersion", out _))
                    javaRequirement = JavaRuntimeService.ReadRequirement(doc.RootElement);
            }
            javaRequirement ??= new JavaRequirement("java-runtime-delta", 21);

            Report($"正在准备 Java {javaRequirement.Value.MajorVersion} 虚拟机...");
            javaPath = await JavaRuntimeService.Instance.EnsureAsync(
                javaRequirement.Value, gameDirectory, _progress, javaTask, javaPath, autoMatchJava, cancellationToken);
            if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
                throw new InvalidOperationException("未能安装 Java 虚拟机");
            var parent = chain[0];

            var vanillaId = parent.RootElement.TryGetProperty("id", out var pid) ? pid.GetString() ?? versionId : versionId;
            var clientJar = await ResolveClientJarAsync(chain, versionId, instanceDir, gameDirectory, vanillaId, cancellationToken);
            if (!File.Exists(clientJar) || new FileInfo(clientJar).Length == 0)
                throw new InvalidOperationException($"找不到游戏本体 {Path.GetFileName(clientJar)}，请重新安装该版本");

            Report("正在检查游戏库文件...");
            var classpath = new List<string>();

            foreach (var doc in chain)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("libraries", out var libraries)) continue;
                foreach (var lib in libraries.EnumerateArray())
                {
                    if (!WindowsLibraryFilter.Allows(lib)) continue;
                    var hasDownloads = lib.TryGetProperty("downloads", out var libDownloads);

                    if (hasDownloads && libDownloads.TryGetProperty("artifact", out var artifact))
                    {
                        var relativePath = artifact.GetProperty("path").GetString();
                        var url = artifact.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrEmpty(relativePath))
                        {
                            var jarPath = Path.Combine(librariesDir, relativePath);
                            classpath.Add(jarPath);
                            if (url is not null)
                                await DownloadFileIfMissingAsync(url, jarPath, cancellationToken);
                            if (IsNativesJar(jarPath))
                                await ExtractNativesAsync(jarPath, nativesDir, lib, cancellationToken);
                        }
                    }
                    else if (lib.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { } mavenName)
                    {
                        var relative = MavenPath(mavenName);
                        var baseUrl = lib.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : "https://libraries.minecraft.net/";
                        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://libraries.minecraft.net/";
                        if (!baseUrl.EndsWith('/')) baseUrl += "/";
                        var jarPath = Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                        classpath.Add(jarPath);
                        await DownloadFileIfMissingAsync(baseUrl + relative, jarPath, cancellationToken);
                        if (IsNativesJar(jarPath))
                            await ExtractNativesAsync(jarPath, nativesDir, lib, cancellationToken);
                    }

                    if (hasDownloads
                        && lib.TryGetProperty("natives", out var natives)
                        && GetNativeClassifier(natives) is { } classifier
                        && libDownloads.TryGetProperty("classifiers", out var classifiers)
                        && classifiers.TryGetProperty(classifier, out var nativeArtifact))
                    {
                        var relativePath = nativeArtifact.GetProperty("path").GetString();
                        var url = nativeArtifact.TryGetProperty("url", out var u) ? u.GetString() : null;
                        if (!string.IsNullOrEmpty(relativePath) && url is not null)
                        {
                            var nativeJar = Path.Combine(librariesDir, relativePath);
                            await DownloadFileIfMissingAsync(url, nativeJar, cancellationToken);
                            await ExtractNativesAsync(nativeJar, nativesDir, lib, cancellationToken);
                        }
                    }
                }
            }

            classpath.Add(clientJar);
            classpath = classpath.Where(File.Exists).Distinct().ToList();
            if (!classpath.Contains(clientJar))
                throw new InvalidOperationException($"找不到游戏本体 {Path.GetFileName(clientJar)}，请重新安装该版本");

            string assetIndexName = vanillaId;
            foreach (var doc in chain)
            {
                if (doc.RootElement.TryGetProperty("assetIndex", out var assetIndex)
                    && assetIndex.TryGetProperty("id", out var assetId))
                    assetIndexName = assetId.GetString() ?? assetIndexName;
            }

            var mainClass = "";
            var type = "release";
            foreach (var doc in chain)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("mainClass", out var mc)) mainClass = mc.GetString() ?? mainClass;
                if (root.TryGetProperty("type", out var t)) type = t.GetString() ?? type;
            }
            if (string.IsNullOrWhiteSpace(mainClass))
                throw new InvalidOperationException("版本 JSON 缺少 mainClass");

            JsonElement assetSource = default;
            foreach (var doc in chain)
            {
                if (doc.RootElement.TryGetProperty("assetIndex", out _))
                    assetSource = doc.RootElement;
            }
            if (assetSource.ValueKind == JsonValueKind.Object)
            {
                Report("正在检查游戏资源...");
                await AssetDownloadService.EnsureAsync(assetSource, gameDirectory, _progress, null, cancellationToken);
            }

            Report("正在准备启动参数...");
            var heapMb = ClampHeapMb(memoryMB, javaPath);
            var minMb = Math.Min(Math.Max(512, heapMb / 4), heapMb);
            var tokens = new Dictionary<string, string>
            {
                ["natives_directory"] = nativesDir,
                ["launcher_name"] = LauncherName,
                ["launcher_version"] = LauncherVersion,
                ["classpath"] = string.Join(Path.PathSeparator, classpath.Distinct()),
                ["game_directory"] = runtimeDir,
                ["assets_root"] = assetsDir,
                ["assets_index_name"] = assetIndexName,
                ["auth_uuid"] = string.IsNullOrWhiteSpace(authUuid) ? "00000000-0000-0000-0000-000000000000" : authUuid,
                ["auth_access_token"] = string.IsNullOrWhiteSpace(authAccessToken) ? "0" : authAccessToken,
                ["auth_player_name"] = username,
                ["user_type"] = string.IsNullOrWhiteSpace(userType) ? "legacy" : userType,
                ["version_name"] = versionId,
                ["version_id"] = versionId,
                ["version_type"] = type,
                ["max_memory"] = heapMb + "M",
                ["min_memory"] = minMb + "M",
                ["clientid"] = string.IsNullOrWhiteSpace(clientId) ? "XO_Launcher" : clientId,
                ["auth_xuid"] = string.IsNullOrWhiteSpace(authXuid) ? "0" : authXuid,
                ["user_properties"] = "{}",
                ["library_directory"] = librariesDir,
                ["classpath_separator"] = Path.PathSeparator.ToString(),
                ["resolution_width"] = (windowWidth is > 0 ? windowWidth.Value : 854).ToString(),
                ["resolution_height"] = (windowHeight is > 0 ? windowHeight.Value : 480).ToString()
            };

            var jvmArgs = new List<string>();
            var gameArgs = new List<string>();
            foreach (var doc in chain)
            {
                jvmArgs.AddRange(ParseArguments(doc.RootElement, "jvm", tokens, out _));
                gameArgs.AddRange(ParseArguments(doc.RootElement, "game", tokens, out _));
                if (doc.RootElement.TryGetProperty("minecraftArguments", out var legacy)
                    && legacy.GetString() is { } legacyArgs)
                {
                    gameArgs.AddRange(SplitLegacyArgs(ExpandTokens(legacyArgs, tokens)));
                }
            }

            jvmArgs.RemoveAll(arg => IsUnsafeOrDuplicateJvmArg(arg, JavaRuntimeService.QueryMajorVersion(javaPath)));
            AppendExtraArgs(jvmArgs, extraJvmArgs, jvm: true);
            AppendExtraArgs(gameArgs, extraGameArgs, jvm: false);
            if (windowMode == "custom")
                EnsureWindowSize(gameArgs, windowWidth, windowHeight);
            else if (windowMode == "fullscreen" && !HasFlag(gameArgs, "--fullscreen"))
                gameArgs.Add("--fullscreen");
            if (assetSource.ValueKind == JsonValueKind.Object
                && AssetDownloadService.Log4jArgument(assetSource, gameDirectory) is { } log4j)
                jvmArgs.Insert(0, log4j);
            if (!jvmArgs.Exists(a => a == "-cp" || a == "-classpath"))
            {
                jvmArgs.Add($"-Djava.library.path={nativesDir}");
                jvmArgs.Add("-cp");
                jvmArgs.Add(tokens["classpath"]);
            }

            TryWriteLaunchLog(runtimeDir, javaPath, jvmArgs, mainClass, gameArgs, $"-Xms{minMb}M", $"-Xmx{heapMb}M");
            GameOptionsDefaults.ApplyForNewInstance(runtimeDir, versionId);

            Report("正在启动 Minecraft...");
            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                WorkingDirectory = runtimeDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add($"-Xms{minMb}M");
            startInfo.ArgumentList.Add($"-Xmx{heapMb}M");
            foreach (var arg in jvmArgs)
                startInfo.ArgumentList.Add(arg);
            startInfo.ArgumentList.Add(mainClass);
            foreach (var arg in gameArgs)
                startInfo.ArgumentList.Add(arg);

            foreach (var doc in chain) doc.Dispose();

            var process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("无法创建 Java 进程");
            return process;
        }

        private async Task<List<JsonDocument>> LoadVersionChainAsync(
            string versionId,
            string? manifestUrl,
            string gameDirectory,
            CancellationToken ct)
        {
            var chain = new List<JsonDocument>();
            var currentId = versionId;
            var currentUrl = manifestUrl;
            var guard = 0;
            while (guard++ < 6)
            {
                var jsonPath = Path.Combine(gameDirectory, "versions", currentId, currentId + ".json");
                JsonDocument doc;
                if (File.Exists(jsonPath))
                {
                    doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, ct));
                }
                else if (!string.IsNullOrWhiteSpace(currentUrl))
                {
                    var json = await HttpDownloadHelper.GetStringAsync(currentUrl, ct);
                    Directory.CreateDirectory(Path.Combine(gameDirectory, "versions", currentId));
                    await File.WriteAllTextAsync(jsonPath, json, ct);
                    doc = JsonDocument.Parse(json);
                }
                else
                {
                    throw new InvalidOperationException($"找不到版本 {currentId}，请先在「下载」页安装");
                }

                chain.Insert(0, doc);
                if (!doc.RootElement.TryGetProperty("inheritsFrom", out var parentEl)
                    || parentEl.GetString() is not { Length: > 0 } parentId)
                    break;
                currentId = parentId;
                currentUrl = null;
            }
            return chain;
        }

        private async Task<string> ResolveClientJarAsync(
            List<JsonDocument> chain,
            string versionId,
            string instanceDir,
            string gameDirectory,
            string vanillaId,
            CancellationToken ct)
        {
            var instanceJar = Path.Combine(instanceDir, versionId + ".jar");
            if (File.Exists(instanceJar) && new FileInfo(instanceJar).Length > 0)
                return instanceJar;

            string? jarId = null;
            string? clientUrl = null;
            foreach (var doc in chain)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("jar", out var jarEl) && jarEl.GetString() is { Length: > 0 } named)
                    jarId = named;
                if (root.TryGetProperty("downloads", out var downloads)
                    && downloads.TryGetProperty("client", out var clientInfo)
                    && clientInfo.TryGetProperty("url", out var clientUrlElement))
                    clientUrl = clientUrlElement.GetString();
            }

            foreach (var candidate in new[]
            {
                jarId is null ? null : Path.Combine(gameDirectory, "versions", jarId, jarId + ".jar"),
                Path.Combine(gameDirectory, "versions", vanillaId, vanillaId + ".jar")
            })
            {
                if (candidate is null || !File.Exists(candidate) || new FileInfo(candidate).Length == 0) continue;
                File.Copy(candidate, instanceJar, overwrite: true);
                return instanceJar;
            }

            if (!string.IsNullOrWhiteSpace(clientUrl))
                await DownloadFileIfMissingAsync(clientUrl, instanceJar, ct);

            return instanceJar;
        }

        private static bool IsNativesJar(string path)
        {
            var name = Path.GetFileName(path);
            return name.Contains("natives-windows", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryWriteLaunchLog(
            string instanceDir,
            string javaPath,
            IReadOnlyList<string> jvmArgs,
            string mainClass,
            IReadOnlyList<string> gameArgs,
            params string[] prefixArgs)
        {
            try
            {
                var logs = Path.Combine(instanceDir, "logs");
                Directory.CreateDirectory(logs);
                var lines = new List<string>
                {
                    DateTime.Now.ToString("O"),
                    javaPath
                };
                lines.AddRange(prefixArgs);
                lines.AddRange(jvmArgs);
                lines.Add(mainClass);
                lines.AddRange(gameArgs);
                File.WriteAllLines(Path.Combine(logs, "xo-launch.log"), lines);
            }
            catch (Exception)
            {
                // 启动日志写失败不影响游戏启动
            }
        }

        private static void AppendExtraArgs(List<string> target, string? extra, bool jvm)
        {
            foreach (var arg in SplitQuoted(extra))
            {
                if (jvm && (arg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
                            || arg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase)))
                    continue;
                target.Add(arg);
            }
        }

        private static void EnsureWindowSize(List<string> gameArgs, int? width, int? height)
        {
            if (width is > 0 && !HasFlag(gameArgs, "--width"))
            {
                gameArgs.Add("--width");
                gameArgs.Add(width.Value.ToString());
            }
            if (height is > 0 && !HasFlag(gameArgs, "--height"))
            {
                gameArgs.Add("--height");
                gameArgs.Add(height.Value.ToString());
            }
        }

        private static bool HasFlag(List<string> args, string flag)
            => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<string> SplitQuoted(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;
            var current = new System.Text.StringBuilder();
            var quote = '\0';
            foreach (var ch in value)
            {
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    else current.Append(ch);
                    continue;
                }
                if (ch is '"' or '\'')
                {
                    quote = ch;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    if (current.Length == 0) continue;
                    yield return current.ToString();
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) yield return current.ToString();
        }

        private static bool IsUnsafeOrDuplicateJvmArg(string arg, int javaMajor)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.Contains("${", StringComparison.Ordinal))
                return true;
            if (arg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase))
                return true;
            if (arg.Equals("-XstartOnFirstThread", StringComparison.OrdinalIgnoreCase)
                && !OperatingSystem.IsMacOS())
                return true;
            if (javaMajor is > 0 and < 22 && arg.StartsWith("--enable-native-access", StringComparison.OrdinalIgnoreCase))
                return true;
            if (javaMajor is > 0 and < 25 && arg.Contains("UseCompactObjectHeaders", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static long ClampHeapMb(long requested, string javaPath)
        {
            var heap = requested <= 0 ? 2048 : requested;
            var physical = GetPhysicalMemoryMb();
            if (physical > 0)
            {
                var usable = Math.Max(1024, physical - 2048);
                heap = Math.Min(heap, usable);
            }

            if (javaPath.Contains("windows-x86", StringComparison.OrdinalIgnoreCase)
                || javaPath.Contains("\\x86\\", StringComparison.OrdinalIgnoreCase))
                heap = Math.Min(heap, 1200);

            return Math.Clamp(heap, 512, 32 * 1024);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static long GetPhysicalMemoryMb()
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return 0;
                return (long)(status.TotalPhys / (1024 * 1024));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string MavenPath(string name)
        {
            var parts = name.Split(':');
            if (parts.Length < 3) return name.Replace('.', '/') + ".jar";
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 ? "-" + parts[3] : "";
            return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
        }

        private static IEnumerable<string> SplitLegacyArgs(string value)
        {
            return value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        // ==================== 版本 JSON ====================

        // ==================== 下载 ====================

        private Task DownloadFileIfMissingAsync(string? url, string destination, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return Task.CompletedTask;
            if (File.Exists(destination) && new FileInfo(destination).Length > 0) return Task.CompletedTask;
            Report($"正在下载 {Path.GetFileName(destination)} ...");
            return HttpDownloadHelper.DownloadFileAsync(url, destination, ct);
        }

        // ==================== 规则判定 ====================

        private static bool RulesAllow(JsonElement element)
        {
            if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return true;

            // 与官方启动器 / wiki.vg 相同：存在 rules 时默认拒绝，再按规则覆盖。
            // 之前默认允许，会把 macOS 的 -XstartOnFirstThread 带进 Windows，导致无法创建 JVM。
            var allow = false;
            foreach (var rule in rules.EnumerateArray())
            {
                var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "allow";
                if (!OsMatches(rule)) continue;
                allow = action == "allow";
            }
            return allow;
        }

        private static bool OsMatches(JsonElement rule)
        {
            var features = rule.TryGetProperty("features", out var f) ? f : default;
            if (features.ValueKind == JsonValueKind.Object)
            {
                // 目前不支持自定义分辨率 / 演示账户等特性
                foreach (var feature in features.EnumerateObject())
                {
                    var required = feature.Value.GetBoolean();
                    if (required) return false;
                }
            }

            if (!rule.TryGetProperty("os", out var os) || os.ValueKind != JsonValueKind.Object)
                return true;

            var osName = Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => "windows",
                PlatformID.Unix => OperatingSystem.IsMacOS() ? "osx" : "linux",
                _ => "windows"
            };
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x86_64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                _ => "x86_64"
            };

            if (os.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } name && !string.Equals(name, osName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.TryGetProperty("arch", out var archProp) && archProp.GetString() is { } archRule && !string.Equals(archRule, arch, StringComparison.OrdinalIgnoreCase))
                return false;
            if (os.TryGetProperty("version", out var verProp) && verProp.GetString() is { } versionRegex)
            {
                var version = Environment.OSVersion.Version.ToString();
                if (!Regex.IsMatch(version, versionRegex)) return false;
            }
            return true;
        }

        private static string? GetNativeClassifier(JsonElement natives)
        {
            var key = Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => "windows",
                PlatformID.Unix => OperatingSystem.IsMacOS() ? "osx" : "linux",
                _ => "windows"
            };
            return natives.TryGetProperty(key, out var value) ? value.GetString() : null;
        }

        // ==================== 参数组装 ====================

        private static List<string> ParseArguments(JsonElement root, string section, IReadOnlyDictionary<string, string> tokens, out string? error)
        {
            error = null;
            var result = new List<string>();
            if (!root.TryGetProperty("arguments", out var args) || !args.TryGetProperty(section, out var list))
                return result;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = ExpandTokens(item.GetString() ?? "", tokens);
                    if (!string.IsNullOrEmpty(value)) result.Add(value);
                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array && !WindowsLibraryFilter.RulesAllow(item))
                    continue;

                if (item.TryGetProperty("value", out var argumentValue))
                {
                    if (argumentValue.ValueKind == JsonValueKind.String)
                    {
                        var expanded = ExpandTokens(argumentValue.GetString() ?? "", tokens);
                        if (!string.IsNullOrEmpty(expanded)) result.Add(expanded);
                    }
                    else if (argumentValue.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in argumentValue.EnumerateArray())
                        {
                            var expanded = ExpandTokens(v.GetString() ?? "", tokens);
                            if (!string.IsNullOrEmpty(expanded)) result.Add(expanded);
                        }
                    }
                }
            }
            return result;
        }

        private static string ExpandTokens(string input, IReadOnlyDictionary<string, string> tokens)
        {
            return Regex.Replace(input, @"\$\{([^}]+)\}", match =>
            {
                var key = match.Groups[1].Value;
                return tokens.TryGetValue(key, out var value) ? value : "";
            });
        }

        // ==================== 原生库解压 ====================

        private static async Task ExtractNativesAsync(string nativeJar, string nativesDir, JsonElement library, CancellationToken ct)
        {
            var targets = new[]
            {
                nativesDir,
                WindowsLibraryFilter.NativeExtractDir(nativesDir, nativeJar)
            }.Distinct(StringComparer.OrdinalIgnoreCase);

            var excludes = new List<string> { "META-INF/" };
            if (library.TryGetProperty("extract", out var extract) && extract.TryGetProperty("exclude", out var excludeArr))
            {
                excludes.AddRange(excludeArr.EnumerateArray().Select(e => e.GetString() ?? ""));
            }

            using var archive = ZipFile.OpenRead(nativeJar);
            foreach (var destRoot in targets)
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (excludes.Any(x => entry.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                    var ext = Path.GetExtension(entry.Name);
                    if (ext is not ".dll" and not ".so" and not ".dylib" and not ".jnilib") continue;

                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.Combine(destRoot, Path.GetFileName(relative));
                    if (File.Exists(target) && new FileInfo(target).Length == entry.Length) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var source = entry.Open();
                    await using var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                    await source.CopyToAsync(dest, ct);
                }
            }
        }

        private void Report(string message)
        {
            _progress?.Report(message);
        }
    }
}
