using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Api;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 自制整合包：把某个实例的 mods + config 导出成 <see cref="XoPackManifest"/>，
    /// 并把清单 gzip + base64url 编码进一条 <c>xolauncher://pack/…</c> 分享链接；
    /// 反向也能从链接还原清单交给安装流程。
    ///
    /// 模组本身不进链接（体积不允许），链接只带「去哪下、下哪个版本」的元数据；
    /// 接收方打开启动器粘贴链接后，模组按 <see cref="XoPackMod.Url"/> 在线下载，
    /// config 则从链接内联的 base64 直接还原。
    /// </summary>
    public static class ModpackExportService
    {
        public const string LinkPrefix = "xolauncher://pack/";

        // config 内联进链接前的原始字节上限：超过就停止收录并如实告知用户被截断。
        // 文本配置 gzip 后通常能压到 1/8 左右，1.5MB 原文压完约 200KB，链接虽长但可用。
        private const long MaxConfigRawBytes = 1_500_000;

        private static readonly string[] ConfigTextExtensions =
        {
            ".cfg", ".json", ".json5", ".toml", ".txt", ".yml", ".yaml",
            ".ini", ".properties", ".lang", ".snbt", ".nbt", ".conf", ".xml", ".zs"
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public sealed class ExportOutcome
        {
            public XoPackManifest Manifest { get; } = new();

            /// <summary>在 Modrinth 上按 sha1 查不到、因而无法进链接的模组文件名。</summary>
            public List<string> Unresolved { get; } = new();

            public int ConfigFileCount { get; set; }
            public long ConfigRawBytes { get; set; }
            public bool ConfigTruncated { get; set; }

            /// <summary>因超限或非文本而被跳过的 config 文件名。</summary>
            public List<string> ConfigSkipped { get; } = new();
        }

        public static async Task<ExportOutcome> BuildAsync(
            string contentRoot,
            string packName,
            string gameVersion,
            string loader,
            string loaderVersion,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var outcome = new ExportOutcome();
            var manifest = outcome.Manifest;
            manifest.Name = string.IsNullOrWhiteSpace(packName) ? "自制整合包" : packName.Trim();
            manifest.GameVersion = gameVersion ?? "";
            manifest.Loader = string.IsNullOrWhiteSpace(loader) ? "vanilla" : loader;
            manifest.LoaderVersion = loaderVersion ?? "";

            var modsDir = Path.Combine(contentRoot, "mods");
            var jars = Directory.Exists(modsDir)
                ? Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<string>();

            if (jars.Count > 0)
            {
                progress?.Report($"正在计算 {jars.Count} 个模组的校验值…");
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // path -> sha1
                foreach (var jar in jars)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { hashes[jar] = Sha1OfFile(jar); }
                    catch (Exception) { hashes[jar] = ""; }
                }

                progress?.Report("正在从 Modrinth 匹配模组…");
                var api = new ModrinthApi();
                IReadOnlyDictionary<string, ModrinthVersion> resolved;
                try
                {
                    resolved = await api.GetVersionsByHashesAsync(
                        hashes.Values.Where(v => v.Length > 0), cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LauncherLogService.Error("Modpack", "批量反查模组失败，全部按未解析处理", ex);
                    resolved = new Dictionary<string, ModrinthVersion>();
                }

                foreach (var jar in jars)
                {
                    var sha1 = hashes.TryGetValue(jar, out var h) ? h : "";
                    var localName = Path.GetFileName(jar);
                    if (sha1.Length > 0 && resolved.TryGetValue(sha1, out var version))
                    {
                        var file = version.PrimaryFile;
                        var url = file?.Url ?? "";
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            manifest.Mods.Add(new XoPackMod
                            {
                                FileName = !string.IsNullOrWhiteSpace(file!.Filename) ? file.Filename : localName,
                                Url = url,
                                Sha1 = sha1,
                                Size = file.Size > 0 ? file.Size : SafeLength(jar),
                                ProjectId = version.ProjectId,
                                ProjectTitle = version.Name,
                                VersionNumber = version.VersionNumber,
                                Env = "client"
                            });
                            continue;
                        }
                    }
                    outcome.Unresolved.Add(localName);
                }
            }

            CollectConfig(contentRoot, manifest, outcome);
            return outcome;
        }

        private static void CollectConfig(string contentRoot, XoPackManifest manifest, ExportOutcome outcome)
        {
            var configDir = Path.Combine(contentRoot, "config");
            if (!Directory.Exists(configDir)) return;

            long budget = MaxConfigRawBytes;
            foreach (var file in Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = ToRelativeKey(contentRoot, file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                long len;
                try { len = new FileInfo(file).Length; }
                catch (Exception) { continue; }

                // 只内联小体积文本配置；二进制 / 超大文件跳过并如实记录。
                if (!ConfigTextExtensions.Contains(ext) || len <= 0 || len > budget)
                {
                    outcome.ConfigSkipped.Add(rel);
                    if (ConfigTextExtensions.Contains(ext) && len > budget)
                        outcome.ConfigTruncated = true;
                    continue;
                }

                try
                {
                    var bytes = File.ReadAllBytes(file);
                    manifest.Config[rel] = Convert.ToBase64String(bytes);
                    budget -= bytes.Length;
                    outcome.ConfigFileCount++;
                    outcome.ConfigRawBytes += bytes.Length;
                }
                catch (Exception)
                {
                    outcome.ConfigSkipped.Add(rel);
                }
            }
        }

        private static string ToRelativeKey(string contentRoot, string file)
        {
            var rel = Path.GetRelativePath(contentRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            return rel;
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (Exception) { return 0; }
        }

        private static string Sha1OfFile(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>把清单编码成分享链接（gzip 压缩 + base64url）。</summary>
        public static string ToShareLink(XoPackManifest manifest)
        {
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var raw = Encoding.UTF8.GetBytes(json);
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gz.Write(raw, 0, raw.Length);
            }
            return LinkPrefix + Base64UrlEncode(ms.ToArray());
        }

        /// <summary>
        /// 从分享链接还原清单。容忍三种输入：带 <see cref="LinkPrefix"/> 前缀的完整链接、
        /// 只有 base64url 负载的裸串、以及把链接塞进 <c>?d=</c> 查询参数的形式。
        /// 解析不出时返回 null（调用方给出「链接无效」提示）。
        /// </summary>
        public static XoPackManifest? FromShareLink(string? link)
        {
            var text = (link ?? "").Trim();
            if (text.Length == 0) return null;

            var payload = text;
            if (payload.StartsWith(LinkPrefix, StringComparison.OrdinalIgnoreCase))
                payload = payload[LinkPrefix.Length..];

            // 形如 https://…?d=<payload> 或 xolauncher://pack/?d=<payload>
            var marker = payload.IndexOf("d=", StringComparison.Ordinal);
            if (marker >= 0 && (payload.Contains('?') || payload.Contains('&')))
            {
                var afterMarker = payload[(marker + 2)..];
                var cut = afterMarker.IndexOf('&');
                payload = cut >= 0 ? afterMarker[..cut] : afterMarker;
            }

            payload = payload.Trim().TrimEnd('/');
            if (payload.Length == 0) return null;

            try
            {
                var compressed = Base64UrlDecode(payload);
                using var input = new MemoryStream(compressed);
                using var gz = new GZipStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                var json = reader.ReadToEnd();
                var manifest = JsonSerializer.Deserialize<XoPackManifest>(json);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.GameVersion))
                    return null;
                return manifest;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Modpack", "解析分享链接失败", ex);
                return null;
            }
        }

        private static string Base64UrlEncode(byte[] data)
            => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
