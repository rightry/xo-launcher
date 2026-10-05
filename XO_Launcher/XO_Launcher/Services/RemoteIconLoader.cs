using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 用 HttpClient 拉图标再交给 BitmapImage，避免 WinUI 直接下载 http 时进程闪退。
    /// WebP（Modrinth 图标的主力格式，实测约占 80%）交给 WIC 解码：
    /// Win10 1809+ / Win11 通常自带 WebP codec 可直接解码；老系统（LTSC/精简版）
    /// 解不了时首次失败会被缓存，之后的 WebP 直接跳过，图标回落到占位符。
    /// </summary>
    internal static class RemoteIconLoader
    {
        private static readonly HttpClient Client = CreateClient(TimeSpan.FromSeconds(8));

        /// <summary>
        /// 大图（详情页横幅这类不设体积上限的图）单独走一个更宽松的客户端：
        /// 原图动辄数 MB，8 秒超时会被直接掐断，表现为横幅永远加载不出来。
        /// </summary>
        private static readonly HttpClient ImageClient = CreateClient(TimeSpan.FromSeconds(30));

        /// <summary>本机 WIC 能否解 WebP；null = 尚未探测。</summary>
        private static bool? _webpSupported;

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher UI)");
            return client;
        }

        public static bool IsSafeUrl(string? url, out Uri uri)
        {
            uri = null!;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri!)) return false;
            if (uri.Scheme is not ("http" or "https")) return false;
            var path = uri.AbsolutePath;
            // svg 无法安全光栅化、ico 罕见且尺寸不可控，仍然拒绝。
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                return false;
            // 已确认本机解不了 WebP 就不再下载，省流量省等待。
            if (_webpSupported == false && path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>图标等小图的上限：1.5 MB。列表卡片里的徽标不需要更大。</summary>
        public const long DefaultMaxBytes = 1_500_000;

        public static Task<byte[]?> DownloadAsync(string? url, CancellationToken cancellationToken)
            => DownloadAsync(url, cancellationToken, DefaultMaxBytes);

        /// <summary>
        /// maxBytes = 可接受的字节上限。详情页顶部背景横幅这类「不限制大小」的场景
        /// 传 <see cref="long.MaxValue"/>，否则大幅原图会被 1.5 MB 上限直接丢弃。
        /// </summary>
        public static async Task<byte[]?> DownloadAsync(string? url, CancellationToken cancellationToken, long maxBytes)
        {
            if (!IsSafeUrl(url, out var uri)) return null;
            var client = maxBytes > DefaultMaxBytes ? ImageClient : Client;
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                var media = response.Content.Headers.ContentType?.MediaType ?? "";
                if (media.Contains("svg", StringComparison.OrdinalIgnoreCase))
                    return null;
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 24 || bytes.Length > maxBytes) return null;
                if (!IsSupportedImage(bytes)) return null;
                if (IsWebp(bytes) && _webpSupported == false) return null;
                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>图标用的正方形解码（默认 40×40）。</summary>
        public static Task<BitmapImage?> CreateBitmapAsync(byte[] bytes)
            => CreateBitmapAsync(bytes, 40, 40);

        /// <summary>
        /// 按原图自身的像素尺寸解码，不做任何降采样（DecodePixelWidth/Height = 0
        /// 即沿用源尺寸）。详情页背景图要求「用原本的大小」，必须走这个重载。
        /// </summary>
        public static Task<BitmapImage?> CreateOriginalBitmapAsync(byte[] bytes)
            => CreateBitmapAsync(bytes, 0, 0);

        /// <summary>
        /// 按宽度等比解码（decodeHeight 传 0 = 高度随源图比例）。
        /// 宽幅背景图必须走这个重载：强制解码成正方形会把图片压扁，
        /// 再用 UniformToFill 放大就会明显变形。
        /// </summary>
        public static async Task<BitmapImage?> CreateBitmapAsync(byte[] bytes, int decodeWidth, int decodeHeight)
        {
            // WebP 先探测本机解码能力（结果全局缓存）：BitmapDecoder 找不到
            // codec 会抛可捕获异常，不会像未观察的 SetSourceAsync 那样闪退。
            if (IsWebp(bytes) && !await IsWebpDecodableAsync(bytes).ConfigureAwait(false))
                return null;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var image = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelHeight = decodeHeight };
                await image.SetSourceAsync(stream);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<bool> IsWebpDecodableAsync(byte[] bytes)
        {
            if (_webpSupported is bool cached) return cached;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                // 只创建 decoder + 读元数据即可判断 codec 是否存在（WIC 惰性解码，成本极低）。
                var decoder = await BitmapDecoder.CreateAsync(stream);
                _ = decoder.FrameCount;
                _webpSupported = true;
            }
            catch (Exception)
            {
                _webpSupported = false;
            }
            return _webpSupported.Value;
        }

        /// <summary>
        /// 启动器自带方块图用文件 URI；整合包封面先校验再走内存流。
        /// 不要把未校验的 http 交给 BitmapImage，解码失败会 0xc000027b。
        /// </summary>
        public static BitmapImage? TryCreateFromFile(string? path, int decodePx = 40)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;

                if (IsBundledIcon(path))
                    return new BitmapImage(new Uri(path)) { DecodePixelWidth = decodePx, DecodePixelHeight = decodePx };

                var length = new FileInfo(path).Length;
                if (length is < 24 or > 2_000_000)
                    return null;
                var bytes = File.ReadAllBytes(path);
                if (!IsSupportedImage(bytes))
                    return null;
                if (IsWebp(bytes) && _webpSupported == false)
                    return null;
                var stream = new InMemoryRandomAccessStream();
                stream.WriteAsync(bytes.AsBuffer()).AsTask().GetAwaiter().GetResult();
                stream.Seek(0);
                var image = new BitmapImage { DecodePixelWidth = decodePx, DecodePixelHeight = decodePx };
                image.SetSource(stream);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsBundledIcon(string path)
        {
            var icons = VersionIconService.IconsFolder;
            return path.StartsWith(icons, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSupportedImage(byte[] bytes)
        {
            if (IsWebp(bytes)) return true;
            if (bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return true;
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return true;
            if (bytes.Length >= 3 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
                return true;
            return false;
        }

        /// <summary>WebP 容器魔数：RIFF....WEBP。</summary>
        public static bool IsWebp(byte[] bytes)
            => bytes.Length >= 12
               && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
               && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
    }
}
