using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 版本全景图：从客户端资源里取出标题界面全景的四面侧视图（panorama_0..3），
    /// 按 0→1→2→3 的相邻关系横拼成一条 360° 环带，缓存到
    /// %LocalAppData%\XO_Launcher\panoramas，供首页作为滚动背景（滚一圈正好转回原处）。
    /// panorama_4/5 是天空与地面，横滚带里放不进来，所以不取。
    /// Forge/NeoForge 等衍生版本会沿 inheritsFrom 链回溯到原版 jar。
    /// </summary>
    public static class PanoramaService
    {
        /// <summary>环带由几张方形侧面拼成（= 环带宽高比）。只有单面可用时退回 1。</summary>
        public const int SideFaces = 4;

        private static readonly string[] PanoramaEntryFormats =
        {
            // 1.21.2+ 新目录结构
            "assets/minecraft/textures/gui/title/background/panorama/panorama_{0}.png",
            // 旧目录结构
            "assets/minecraft/textures/gui/title/background/panorama_{0}.png"
        };

        private static readonly string[] AssetKeyFormats =
        {
            "minecraft/textures/gui/title/background/panorama/panorama_{0}.png",
            "minecraft/textures/gui/title/background/panorama_{0}.png"
        };

        // 新版 jar 里的 panorama_*.png 只是几十字节的占位图，真实贴图走 assets 资源索引
        private const long MinRealBytes = 512;

        // 启动时 RefreshChrome 会连着触发两次全景刷新；不串行的话两个任务会抢同一个
        // tmp 文件（一个在编码、一个 File.Create 报占用），失败的那个还会用单面回退
        // 把对方拼好的环带覆盖掉。
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>环带缓存目录，调试页要能打开它看拼出来的图。</summary>
        public static string CacheDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "panoramas");

        /// <summary>解析好的全景背景：Path 为缓存文件，Faces 为环带包含的方形面数（宽高比），
        /// Luminance 为环带平均亮度（0–1），供主题决定文字与卡片底色。</summary>
        public sealed record PanoramaImage(string Path, int Faces, double Luminance);

        /// <summary>解析版本全景环带的本地路径；找不到返回 null（不联网，仅读本地文件）。</summary>
        public static async Task<PanoramaImage?> GetPanoramaAsync(string gameDirectory, string instanceDir, string versionId)
        {
            await Gate.WaitAsync();
            var temps = new List<string>();
            try
            {
                var cachePath = Path.Combine(CacheDirectory, Sanitize(versionId) + "_pano.png");
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length >= MinRealBytes)
                {
                    var cached = FacesOfPng(cachePath);
                    if (cached > 0)
                        return new PanoramaImage(cachePath, cached, await GetAverageLuminanceAsync(cachePath));
                    File.Delete(cachePath);
                }

                var objects = LoadPanoramaObjects(gameDirectory, FindAssetIndexId(gameDirectory, versionId));
                var jars = EnumerateCandidateJars(gameDirectory, instanceDir, versionId).ToList();

                var faces = new string?[SideFaces];
                for (var i = 0; i < SideFaces; i++)
                    faces[i] = ResolveFace(objects, jars, i, temps);

                if (faces.All(f => f is not null) && await WriteStripAsync(faces!, cachePath, temps))
                    return new PanoramaImage(cachePath, SideFaces, await GetAverageLuminanceAsync(cachePath));

                // 凑不齐四面（资源没下全等）：退回单面，至少背景还在，不至于变纯色
                if (faces[0] is not null)
                {
                    CopyToCache(faces[0]!, cachePath, temps);
                    return new PanoramaImage(cachePath, 1, await GetAverageLuminanceAsync(cachePath));
                }
                return null;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Panorama", $"读取版本全景图失败：{versionId}", ex);
                return null;
            }
            finally
            {
                Gate.Release();
                foreach (var temp in temps)
                {
                    try { File.Delete(temp); } catch (Exception) { }
                }
            }
        }

        /// <summary>沿 inheritsFrom 链找到 assetIndex id（Fabric/Forge 的 json 里没有，需要回溯到原版）。</summary>
        private static string? FindAssetIndexId(string gameDirectory, string versionId)
        {
            var currentId = versionId;
            for (var guard = 0; guard < 6; guard++)
            {
                var json = Path.Combine(gameDirectory, "versions", currentId, currentId + ".json");
                if (!File.Exists(json)) return null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("assetIndex", out var index)
                        && index.TryGetProperty("id", out var idEl)
                        && idEl.GetString() is { Length: > 0 } id)
                        return id;
                    if (root.TryGetProperty("inheritsFrom", out var parentEl)
                        && parentEl.GetString() is { Length: > 0 } parent
                        && !string.Equals(parent, currentId, StringComparison.Ordinal))
                    {
                        currentId = parent;
                        continue;
                    }
                }
                catch (Exception)
                {
                    // json 损坏按没有资源索引处理
                }
                return null;
            }
            return null;
        }

        /// <summary>把资源索引里各侧面对应的 assets/objects 哈希文件路径一次性查出来。</summary>
        private static Dictionary<int, string> LoadPanoramaObjects(string gameDirectory, string? indexId)
        {
            var map = new Dictionary<int, string>();
            if (indexId is null) return map;
            var indexPath = Path.Combine(gameDirectory, "assets", "indexes", indexId + ".json");
            if (!File.Exists(indexPath)) return map;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                if (!doc.RootElement.TryGetProperty("objects", out var objects)) return map;
                for (var face = 0; face < SideFaces; face++)
                {
                    foreach (var format in AssetKeyFormats)
                    {
                        if (!objects.TryGetProperty(string.Format(format, face), out var obj)) continue;
                        if (!obj.TryGetProperty("hash", out var hashEl)
                            || hashEl.GetString() is not { Length: > 2 } hash) continue;
                        var objectPath = Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash);
                        if (File.Exists(objectPath))
                        {
                            map[face] = objectPath;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Panorama", $"从资源索引读取全景图失败：{indexId}", ex);
            }
            return map;
        }

        /// <summary>单面的来源：优先资源索引里的哈希文件（可直接读），否则从候选 jar 里解出来。</summary>
        private static string? ResolveFace(Dictionary<int, string> objects, IReadOnlyList<string> jars, int face, List<string> temps)
        {
            if (objects.TryGetValue(face, out var objectPath) && new FileInfo(objectPath).Length >= MinRealBytes)
                return objectPath;
            foreach (var jar in jars)
            {
                var extracted = ExtractFace(jar, face, temps);
                if (extracted is not null) return extracted;
            }
            return null;
        }

        private static IEnumerable<string> EnumerateCandidateJars(
            string gameDirectory, string instanceDir, string versionId)
        {
            var instanceJar = Path.Combine(instanceDir, versionId + ".jar");
            if (IsReadableJar(instanceJar)) yield return instanceJar;

            var currentId = versionId;
            for (var guard = 0; guard < 6; guard++)
            {
                var jar = Path.Combine(gameDirectory, "versions", currentId, currentId + ".jar");
                if (IsReadableJar(jar)) yield return jar;

                var json = Path.Combine(gameDirectory, "versions", currentId, currentId + ".json");
                if (!File.Exists(json)) yield break;
                string? parent = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var el))
                        parent = el.GetString();
                }
                catch (Exception)
                {
                    yield break;
                }
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, currentId, StringComparison.Ordinal))
                    yield break;
                currentId = parent;
            }
        }

        private static bool IsReadableJar(string path)
            => File.Exists(path) && new FileInfo(path).Length > 0;

        private static string? ExtractFace(string jarPath, int face, List<string> temps)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                foreach (var format in PanoramaEntryFormats)
                {
                    var entry = zip.GetEntry(string.Format(format, face));
                    if (entry is null || entry.Length < MinRealBytes) continue;
                    var tmp = Path.Combine(Path.GetTempPath(), $"xol_pano{face}_{Guid.NewGuid():N}.png");
                    using (var output = File.Create(tmp))
                    using (var input = entry.Open())
                        input.CopyTo(output);
                    temps.Add(tmp);
                    return tmp;
                }
            }
            catch (Exception)
            {
                // jar 不是合法 zip 等情况，继续尝试下一个候选
            }
            return null;
        }

        /// <summary>
        /// 把四面方形贴图按顺序横拼成 4:1 环带并写成 PNG 缓存。
        /// 各面尺寸不一致或不是正方形时返回 false，由调用方退回单面。
        /// </summary>
        private static async Task<bool> WriteStripAsync(IReadOnlyList<string> facePaths, string cachePath, List<string> temps)
        {
            var bitmaps = new List<SoftwareBitmap>(facePaths.Count);
            try
            {
                foreach (var path in facePaths)
                {
                    using var stream = File.OpenRead(path).AsRandomAccessStream();
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    bitmaps.Add(await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore));
                }

                var w = (int)bitmaps[0].PixelWidth;
                var h = (int)bitmaps[0].PixelHeight;
                if (w <= 0 || w != h || bitmaps.Any(b => b.PixelWidth != w || b.PixelHeight != h))
                    return false;

                var rowBytes = w * 4;
                var strip = new byte[rowBytes * h * bitmaps.Count];
                var face = new byte[rowBytes * h];
                for (var i = 0; i < bitmaps.Count; i++)
                {
                    bitmaps[i].CopyToBuffer(face.AsBuffer());
                    for (var y = 0; y < h; y++)
                        Buffer.BlockCopy(face, y * rowBytes, strip, y * rowBytes * bitmaps.Count + i * rowBytes, rowBytes);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                temps.Add(tmp);
                using (var output = File.Create(tmp).AsRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                        (uint)(w * bitmaps.Count), (uint)h, 96, 96, strip);
                    await encoder.FlushAsync();
                }
                File.Move(tmp, cachePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Panorama", "拼接全景环带失败", ex);
                return false;
            }
            finally
            {
                foreach (var bitmap in bitmaps) bitmap.Dispose();
            }
        }

        private static void CopyToCache(string source, string cachePath, List<string> temps)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            temps.Add(tmp);
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, cachePath, overwrite: true);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[]> ProfileCache = new();

        /// <summary>环带的平均亮度（0–1，越大越亮）。</summary>
        public static async Task<double> GetAverageLuminanceAsync(string path)
        {
            var profile = await GetLuminanceProfileAsync(path);
            if (profile.Length == 0) return 0.5;
            double sum = 0;
            foreach (var value in profile) sum += value;
            return sum / profile.Length;
        }

        /// <summary>
        /// 把环带横向等分成若干段、逐段求平均亮度。滚动时只按「当前可见那几段」取亮度，
        /// 文字与卡片底色就能跟着背景实时变化，而不必每帧重新解图。
        /// </summary>
        public static async Task<float[]> GetLuminanceProfileAsync(string path, int segments = 120)
        {
            if (ProfileCache.TryGetValue(path, out var cached)) return cached;
            try
            {
                using var stream = File.OpenRead(path).AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                var w = (int)bitmap.PixelWidth;
                var h = (int)bitmap.PixelHeight;
                if (w <= 0 || h <= 0) return Array.Empty<float>();

                var bytes = new byte[w * h * 4];
                bitmap.CopyToBuffer(bytes.AsBuffer());

                var sums = new double[segments];
                var counts = new long[segments];
                // 纵向每 4 行、横向每 2 列采一点：4096×1024 也就约 50 万次加法，只在载入时算一次
                for (var y = 0; y < h; y += 4)
                {
                    for (var x = 0; x < w; x += 2)
                    {
                        var i = (y * w + x) * 4;
                        // BGRA；相对亮度用 sRGB 线性近似足够区分亮暗
                        var lum = 0.2126 * bytes[i + 2] + 0.7152 * bytes[i + 1] + 0.0722 * bytes[i];
                        var seg = (int)((long)x * segments / w);
                        if (seg >= segments) seg = segments - 1;
                        sums[seg] += lum;
                        counts[seg]++;
                    }
                }

                var profile = new float[segments];
                for (var s = 0; s < segments; s++)
                    profile[s] = counts[s] > 0 ? (float)(sums[s] / counts[s] / 255.0) : 0.5f;
                ProfileCache[path] = profile;
                return profile;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Panorama", "读取全景亮度失败", ex);
                return Array.Empty<float>();
            }
        }

        /// <summary>
        /// 直接读 PNG 头里的宽高算出这张缓存是几面环带（宽高比），
        /// 省得再存一份「这是环带还是单面」的元数据。
        /// </summary>
        private static int FacesOfPng(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> head = stackalloc byte[24];
                if (fs.Read(head) != head.Length) return 0;
                var w = (uint)(head[16] << 24 | head[17] << 16 | head[18] << 8 | head[19]);
                var h = (uint)(head[20] << 24 | head[21] << 16 | head[22] << 8 | head[23]);
                if (w == 0 || h == 0) return 0;
                return Math.Max(1, (int)Math.Round((double)w / h));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        }
    }
}
