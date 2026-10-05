using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 按 HMCL / Prism 的方式安装 Forge / NeoForge：解析安装器里的 profile，
    /// 用启动器自己的镜像下库并本地运行 processors，避免官方安装器弹出窗口或卡在 Maven。
    /// </summary>
    internal static class ForgeInstallerRunner
    {
        public static bool LooksLikeInstaller(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                return zip.GetEntry("install_profile.json") is not null
                       || zip.GetEntry("version.json") is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task<string> InstallAsync(
            string installerPath,
            string java,
            string gameDir,
            string minecraftId,
            string vanillaJson,
            string vanillaJar,
            Action<string> report,
            CancellationToken ct)
        {
            if (!File.Exists(installerPath) || !LooksLikeInstaller(installerPath))
                throw new InvalidOperationException("Forge 安装包无效或已损坏，请删除后重试");
            if (!File.Exists(vanillaJar) || new FileInfo(vanillaJar).Length == 0)
                throw new InvalidOperationException("原版客户端不存在，无法安装 Forge");

            var librariesDir = Path.Combine(gameDir, "libraries");
            Directory.CreateDirectory(librariesDir);
            var dataDir = Path.Combine(Path.GetTempPath(), "XO_Launcher", "forge-data", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            try
            {
                string profileJson;
                string? bundledVersionJson;
                using (var zip = ZipFile.OpenRead(installerPath))
                {
                    var profileEntry = zip.GetEntry("install_profile.json")
                        ?? throw new InvalidOperationException("安装包缺少 install_profile.json");
                    profileJson = ReadZipEntry(profileEntry);
                    bundledVersionJson = zip.GetEntry("version.json") is { } versionEntry
                        ? ReadZipEntry(versionEntry)
                        : null;
                    ExtractMaven(zip, librariesDir, report);
                    ExtractDataFolder(zip, dataDir);
                    ExtractOldUniversal(zip, profileJson, librariesDir, report);
                }

                using var profile = JsonDocument.Parse(profileJson);
                var root = profile.RootElement;

                if (root.TryGetProperty("versionInfo", out var versionInfo)
                    && (!root.TryGetProperty("processors", out var oldProcessors)
                        || oldProcessors.ValueKind != JsonValueKind.Array
                        || oldProcessors.GetArrayLength() == 0))
                {
                    report("正在写入旧版 Forge 配置…");
                    return versionInfo.GetRawText();
                }

                var versionJson = bundledVersionJson
                    ?? ReadEmbeddedVersionJson(root)
                    ?? throw new InvalidOperationException("安装包缺少 version.json");

                report("正在下载 Forge 安装所需库…");
                await DownloadProfileLibrariesAsync(root, versionJson, librariesDir, ct, report);
                await DownloadMojmapsAsync(vanillaJson, root, librariesDir, ct, report);

                if (root.TryGetProperty("processors", out var processors)
                    && processors.ValueKind == JsonValueKind.Array
                    && processors.GetArrayLength() > 0)
                {
                    await RunProcessorsAsync(
                        processors, root, java, installerPath, gameDir, librariesDir, dataDir,
                        minecraftId, vanillaJar, report, ct);
                }

                return versionJson;
            }
            finally
            {
                try { Directory.Delete(dataDir, recursive: true); } catch (Exception) { }
            }
        }

        private static void ExtractMaven(ZipArchive zip, string librariesDir, Action<string> report)
        {
            var copied = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (entry.Length == 0 || name.EndsWith('/')) continue;
                if (!name.StartsWith("maven/", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = name["maven/".Length..];
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var dest = Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                copied++;
            }
            if (copied > 0)
                report("已从安装包解出 " + copied + " 个库文件");
        }

        private static void ExtractDataFolder(ZipArchive zip, string dataDir)
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (entry.Length == 0 || name.EndsWith('/')) continue;
                if (!name.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(dataDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        private static void ExtractOldUniversal(ZipArchive zip, string profileJson, string librariesDir, Action<string> report)
        {
            try
            {
                using var doc = JsonDocument.Parse(profileJson);
                if (!doc.RootElement.TryGetProperty("install", out var install)) return;
                var filePath = install.TryGetProperty("filePath", out var fileEl) ? fileEl.GetString() : null;
                var maven = install.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(maven)) return;

                var dest = Path.Combine(librariesDir, MavenRelative(maven).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) return;

                var entry = zip.GetEntry(filePath)
                            ?? zip.GetEntry(filePath.Replace('\\', '/'))
                            ?? zip.Entries.FirstOrDefault(e =>
                                e.FullName.EndsWith(Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase));
                if (entry is null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                report("已解出旧版 Forge 主文件");
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Install", "解出旧版 Forge 主文件失败：" + ex.Message);
            }
        }

        private static string? ReadEmbeddedVersionJson(JsonElement profile)
        {
            if (profile.TryGetProperty("versionInfo", out var info) && info.ValueKind == JsonValueKind.Object)
                return info.GetRawText();
            return null;
        }

        private static async Task DownloadProfileLibrariesAsync(
            JsonElement profile,
            string versionJson,
            string librariesDir,
            CancellationToken ct,
            Action<string> report)
        {
            var files = new List<(string Url, string Path)>();
            CollectLibraries(profile, librariesDir, files);
            try
            {
                using var version = JsonDocument.Parse(versionJson);
                CollectLibraries(version.RootElement, librariesDir, files);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Install", "解析 Forge version.json 库列表失败：" + ex.Message);
            }

            var pending = files
                .Where(f => !string.IsNullOrWhiteSpace(f.Url))
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Where(f => !File.Exists(f.Path) || new FileInfo(f.Path).Length == 0)
                .ToList();
            if (pending.Count == 0) return;
            report("正在下载 Forge 依赖 " + pending.Count + " 个文件…");
            await HttpDownloadHelper.DownloadManyAsync(pending, ct, HttpDownloadHelper.FileParallelism,
                (done, total, name) => report($"正在下载 Forge 依赖 {done}/{total}：{name}"));
        }

        private static void CollectLibraries(JsonElement root, string librariesDir, List<(string Url, string Path)> files)
        {
            if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
                return;

            foreach (var lib in libraries.EnumerateArray())
            {
                var name = lib.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                if (lib.TryGetProperty("downloads", out var downloads)
                    && downloads.TryGetProperty("artifact", out var artifact))
                {
                    var relative = artifact.TryGetProperty("path", out var pathEl)
                        ? pathEl.GetString()
                        : string.IsNullOrWhiteSpace(name) ? null : MavenRelative(name);
                    var url = artifact.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    var dest = Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    // 空 URL 表示由安装包或后续处理步骤生成，不要去官方 Maven 猜地址
                    if (!string.IsNullOrWhiteSpace(url))
                        files.Add((url, dest));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name)) continue;
                var maven = MavenRelative(name);
                var baseUrl = lib.TryGetProperty("url", out var libUrl) ? libUrl.GetString() : null;
                if (string.IsNullOrWhiteSpace(baseUrl))
                    baseUrl = GuessMavenBase(name);
                if (string.IsNullOrWhiteSpace(baseUrl)) continue;
                if (!baseUrl.EndsWith('/')) baseUrl += "/";
                files.Add((baseUrl + maven, Path.Combine(librariesDir, maven.Replace('/', Path.DirectorySeparatorChar))));
            }
        }

        private static string? GuessMavenBase(string name)
        {
            if (name.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
                return "https://maven.minecraftforge.net/";
            if (name.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
                return "https://maven.neoforged.net/releases/";
            if (name.StartsWith("net.fabricmc", StringComparison.OrdinalIgnoreCase))
                return "https://maven.fabricmc.net/";
            if (name.StartsWith("net.minecraft", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("com.mojang", StringComparison.OrdinalIgnoreCase))
                return "https://libraries.minecraft.net/";
            return "https://libraries.minecraft.net/";
        }

        private static async Task DownloadMojmapsAsync(
            string vanillaJson,
            JsonElement profile,
            string librariesDir,
            CancellationToken ct,
            Action<string> report)
        {
            if (!profile.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return;

            string? mappingsUrl = null;
            try
            {
                using var vanilla = JsonDocument.Parse(vanillaJson);
                if (vanilla.RootElement.TryGetProperty("downloads", out var downloads)
                    && downloads.TryGetProperty("client_mappings", out var maps)
                    && maps.TryGetProperty("url", out var urlEl))
                    mappingsUrl = urlEl.GetString();
            }
            catch (Exception)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(mappingsUrl)) return;

            foreach (var item in data.EnumerateObject())
            {
                if (!TryClientValue(item.Value, out var raw) || string.IsNullOrWhiteSpace(raw))
                    continue;
                if (!(raw.Contains("official@", StringComparison.OrdinalIgnoreCase)
                      || raw.Contains(":mappings@", StringComparison.OrdinalIgnoreCase)
                      || raw.Contains(":official@", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!(raw.StartsWith('[') && raw.EndsWith(']'))) continue;
                var dest = Path.Combine(librariesDir, MavenRelative(raw[1..^1]).Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                report("正在下载 Mojang 映射表…");
                await HttpDownloadHelper.DownloadFileAsync(mappingsUrl, dest, ct);
            }
        }

        private static async Task RunProcessorsAsync(
            JsonElement processors,
            JsonElement profile,
            string java,
            string installerPath,
            string gameDir,
            string librariesDir,
            string dataDir,
            string minecraftId,
            string vanillaJar,
            Action<string> report,
            CancellationToken ct)
        {
            var data = profile.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
                ? dataEl
                : default;
            var list = processors.EnumerateArray().Where(p => IsClientProcessor(p)).ToList();
            var index = 0;
            foreach (var processor in list)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                if (OutputsExist(processor, data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar))
                {
                    report($"Forge 处理步骤 {index}/{list.Count} 已完成，跳过");
                    continue;
                }

                var jarName = processor.TryGetProperty("jar", out var jarEl) ? jarEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(jarName))
                    throw new InvalidOperationException("Forge 处理步骤缺少 jar");
                var jarPath = LibraryPath(librariesDir, jarName);
                if (!File.Exists(jarPath))
                    throw new InvalidOperationException("缺少 Forge 处理工具：" + jarName);

                if (IsDownloadMojmaps(processor)
                    && OutputsExist(processor, data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar, forceArgs: true))
                {
                    report($"Forge 处理步骤 {index}/{list.Count}：映射表已就绪");
                    continue;
                }

                var mainClass = ReadMainClass(jarPath)
                    ?? throw new InvalidOperationException("无法读取处理工具入口：" + jarName);
                var classpath = new List<string> { jarPath };
                if (processor.TryGetProperty("classpath", out var cp) && cp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in cp.EnumerateArray())
                    {
                        var name = item.GetString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var path = LibraryPath(librariesDir, name);
                        if (!File.Exists(path))
                            throw new InvalidOperationException("缺少 Forge 处理依赖：" + name);
                        classpath.Add(path);
                    }
                }

                var args = new List<string>();
                if (processor.TryGetProperty("args", out var argNode) && argNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in argNode.EnumerateArray())
                    {
                        var raw = item.GetString() ?? "";
                        args.Add(ReplaceVars(raw, data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar));
                    }
                }

                foreach (var arg in args)
                    TryCreateParent(arg);

                report($"正在运行 Forge 处理步骤 {index}/{list.Count}…");
                await RunJavaAsync(java, classpath, mainClass, args, gameDir, ct);
            }
        }

        private static bool IsClientProcessor(JsonElement processor)
        {
            if (!processor.TryGetProperty("sides", out var sides) || sides.ValueKind != JsonValueKind.Array)
                return true;
            return sides.EnumerateArray().Any(s =>
                string.Equals(s.GetString(), "client", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDownloadMojmaps(JsonElement processor)
        {
            if (!processor.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
                return false;
            return args.EnumerateArray().Any(a =>
                string.Equals(a.GetString(), "DOWNLOAD_MOJMAPS", StringComparison.OrdinalIgnoreCase));
        }

        private static bool OutputsExist(
            JsonElement processor,
            JsonElement data,
            string installerPath,
            string gameDir,
            string librariesDir,
            string dataDir,
            string minecraftId,
            string vanillaJar,
            bool forceArgs = false)
        {
            var paths = new List<string>();
            if (processor.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in outputs.EnumerateObject())
                    paths.Add(ReplaceVars(item.Name, data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar));
            }
            if (forceArgs && processor.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                var list = args.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
                for (var i = 0; i < list.Count - 1; i++)
                {
                    if (list[i] is "--output" or "--out" or "--map")
                        paths.Add(ReplaceVars(list[i + 1], data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar));
                }
            }

            return paths.Count > 0 && paths.All(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && new FileInfo(p).Length > 0);
        }

        private static async Task RunJavaAsync(
            string java,
            IReadOnlyList<string> classpath,
            string mainClass,
            IReadOnlyList<string> args,
            string workingDir,
            CancellationToken ct)
        {
            var start = new ProcessStartInfo
            {
                FileName = java,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("-Xmx2G");
            start.ArgumentList.Add("-Djava.awt.headless=true");
            start.ArgumentList.Add("-cp");
            start.ArgumentList.Add(string.Join(Path.PathSeparator, classpath));
            start.ArgumentList.Add(mainClass);
            foreach (var arg in args)
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("无法启动 Forge 处理工具");
            try { process.StandardInput.Close(); } catch (Exception) { }

            var log = new StringBuilder();
            var stdout = PumpAsync(process.StandardOutput, log, ct);
            var stderr = PumpAsync(process.StandardError, log, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException("Forge 处理步骤超时：" + TrimLog(log));
            }

            try { await stdout; } catch (Exception) { }
            try { await stderr; } catch (Exception) { }
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Forge 处理步骤失败：" + TrimLog(log));
        }

        private static async Task PumpAsync(StreamReader reader, StringBuilder log, CancellationToken ct)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (log)
                {
                    if (log.Length > 0) log.AppendLine();
                    log.Append(line);
                    if (log.Length > 8000)
                        log.Remove(0, log.Length - 4000);
                }
            }
        }

        private static string TrimLog(StringBuilder log)
        {
            lock (log)
            {
                var text = log.ToString().Trim();
                return string.IsNullOrWhiteSpace(text) ? "没有输出" : text;
            }
        }

        private static string ReplaceVars(
            string text,
            JsonElement data,
            string installerPath,
            string gameDir,
            string librariesDir,
            string dataDir,
            string minecraftId,
            string vanillaJar)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, "\\{([A-Za-z0-9_]+)\\}", match =>
            {
                var key = match.Groups[1].Value;
                return ResolveToken(key, data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar);
            });
        }

        private static string ResolveToken(
            string key,
            JsonElement data,
            string installerPath,
            string gameDir,
            string librariesDir,
            string dataDir,
            string minecraftId,
            string vanillaJar)
        {
            switch (key.ToUpperInvariant())
            {
                case "SIDE": return "client";
                case "MINECRAFT_JAR": return vanillaJar;
                case "MINECRAFT_VERSION": return minecraftId;
                case "ROOT": return gameDir;
                case "INSTALLER": return installerPath;
                case "LIBRARY_DIR": return librariesDir;
            }

            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var node)
                && TryClientValue(node, out var raw) && !string.IsNullOrWhiteSpace(raw))
            {
                return InterpretValue(raw, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar, data);
            }

            return "";
        }

        private static string InterpretValue(
            string raw,
            string installerPath,
            string gameDir,
            string librariesDir,
            string dataDir,
            string minecraftId,
            string vanillaJar,
            JsonElement data)
        {
            raw = raw.Trim();
            if (raw.Length >= 2 && raw.StartsWith('[') && raw.EndsWith(']'))
                return Path.GetFullPath(Path.Combine(librariesDir, MavenRelative(raw[1..^1]).Replace('/', Path.DirectorySeparatorChar)));
            if (raw.Length >= 2 && raw.StartsWith('\'') && raw.EndsWith('\''))
                return raw[1..^1];
            if (raw.StartsWith('/'))
            {
                var relative = raw.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(dataDir, relative);
                if (File.Exists(dest)) return dest;
                dest = Path.Combine(dataDir, "data", Path.GetFileName(relative));
                if (File.Exists(dest)) return dest;
                return Path.Combine(dataDir, relative);
            }

            if (raw.Contains('{', StringComparison.Ordinal))
                return ReplaceVars(raw, data, installerPath, gameDir, librariesDir, dataDir, minecraftId, vanillaJar);
            return raw;
        }

        private static bool TryClientValue(JsonElement node, out string value)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                value = node.GetString() ?? "";
                return true;
            }
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("client", out var client)
                && client.ValueKind == JsonValueKind.String)
            {
                value = client.GetString() ?? "";
                return true;
            }
            value = "";
            return false;
        }

        private static string LibraryPath(string librariesDir, string mavenName)
            => Path.Combine(librariesDir, MavenRelative(mavenName).Replace('/', Path.DirectorySeparatorChar));

        internal static string MavenRelative(string name)
        {
            var ext = "jar";
            var at = name.LastIndexOf('@');
            if (at > 0)
            {
                ext = name[(at + 1)..];
                name = name[..at];
            }
            var parts = name.Split(':');
            if (parts.Length < 3) return name.Replace('.', '/') + "." + ext;
            var group = parts[0].Replace('.', '/');
            var artifact = parts[1];
            var version = parts[2];
            var classifier = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]) ? "-" + parts[3] : "";
            return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
        }

        private static string? ReadMainClass(string jarPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                var entry = zip.GetEntry("META-INF/MANIFEST.MF");
                if (entry is null) return null;
                var text = ReadZipEntry(entry).Replace("\r\n", "\n").Replace('\r', '\n');
                var builder = new StringBuilder();
                foreach (var line in text.Split('\n'))
                {
                    if (line.StartsWith(' ') && builder.Length > 0)
                        builder.Append(line.TrimStart());
                    else
                    {
                        if (builder.Length > 0)
                        {
                            var value = ReadManifestValue(builder.ToString(), "Main-Class");
                            if (value is not null) return value;
                        }
                        builder.Clear();
                        builder.Append(line.TrimEnd());
                    }
                }
                return ReadManifestValue(builder.ToString(), "Main-Class");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? ReadManifestValue(string line, string key)
        {
            var prefix = key + ":";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var value = line[prefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void TryCreateParent(string path)
        {
            try
            {
                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return;
                if (!Path.IsPathRooted(path)) return;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception)
            {
                // 参数不一定是路径
            }
        }

        private static string ReadZipEntry(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
