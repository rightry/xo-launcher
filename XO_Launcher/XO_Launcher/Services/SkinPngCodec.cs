using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace XO_Launcher.Services
{
    /// <summary>
    /// Minecraft 皮肤 PNG 编解码：64×64（或老式 64×32）↔ 64×64 BGRA8 缓冲。
    /// 像素格式严格对齐 <see cref="SkinAvatarService.RenderHeadAsync"/>：
    /// BitmapPixelFormat.Bgra8 + BitmapAlphaMode.Straight，PNG 经 BitmapEncoder.PngEncoderId 写出。
    /// </summary>
    public static class SkinPngCodec
    {
        private const int Size = 64;

        /// <summary>
        /// 解码 PNG 为 64×64 BGRA8。尺寸不是 64×64 时返回 null（调用方负责提示）。
        /// 兼容 64×32 老格式：缺失的下半部分补成透明，并把老格式的右腿/右臂镜像到现代左腿/左臂位置，
        /// 同时所有覆盖层（帽子/夹克/袖子/裤子）置为全透明。
        /// </summary>
        public static async Task<byte[]?> DecodeAsync(string pngPath)
        {
            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath)) return null;

            byte[] fileBytes = await File.ReadAllBytesAsync(pngPath);

            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(fileBytes);
                await writer.StoreAsync();
            }
            input.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(input);
            int w = (int)decoder.PixelWidth;
            int h = (int)decoder.PixelHeight;

            // 仅支持现代 64×64 与老式 64×32；其余尺寸视为非法
            if (!((w == Size && h == Size) || (w == Size && h == Size / 2)))
                return null;

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] src = pixelData.DetachPixelData();

            if (w == Size && h == Size)
            {
                // 现代格式：严格 64×64，直接拷贝（老式解码器不会补 stride 衬垫）
                var dst = new byte[Size * Size * 4];
                System.Buffer.BlockCopy(src, 0, dst, 0, dst.Length);
                return dst;
            }

            // 老式 64×32 → 现代 64×64
            return ConvertLegacy32To64(src);
        }

        /// <summary>
        /// 把老式 64×32 皮肤转换为现代 64×64 BGRA8。
        /// 上半部分（y 0..32）与原图逐行完全一致，直接拷贝；下半部分（y 32..64）本不存在，补透明。
        /// 现代格式的左腿/左臂区域在老式里不存在，需由右腿/右臂「水平镜像」得到（见下）。
        /// </summary>
        private static byte[] ConvertLegacy32To64(byte[] src)
        {
            var dst = new byte[Size * Size * 4];

            // 上半 32 行逐行拷贝（两布局行宽都是 64，可直接整段复制）
            System.Buffer.BlockCopy(src, 0, dst, 0, src.Length);

            // 右腿 (0,16,16,32) → 左腿 (16,48,32,64)
            MirrorRegion(src, dst, sx: 0, sy: 16, dx: 16, dy: 48, rw: 16, rh: 16);
            // 右臂 (40,16,56,32) → 左臂 (32,48,48,64)
            MirrorRegion(src, dst, sx: 40, sy: 16, dx: 32, dy: 48, rw: 16, rh: 16);

            // 覆盖层（帽子/夹克/袖子/裤子）保持透明——dst 其余部分本就是 0，无需处理
            return dst;
        }

        /// <summary>
        /// 把源图 (sx,sy) 起的 rw×rh 矩形「水平镜像」复制到目标 (dx,dy)。
        /// 镜像：目标第 x 列取自源第 (rw-1-x) 列，从而在身体中轴两侧形成左右对称。
        /// </summary>
        private static void MirrorRegion(byte[] src, byte[] dst, int sx, int sy, int dx, int dy, int rw, int rh)
        {
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    int si = ((sy + y) * Size + (sx + (rw - 1 - x))) * 4; // 源列水平翻转
                    int di = ((dy + y) * Size + (dx + x)) * 4;
                    dst[di] = src[si];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = src[si + 3];
                }
            }
        }

        /// <summary>
        /// 把 BGRA8 像素编码成 PNG 字节（不落盘）。宽高必须与 bgra 长度一致。
        /// 供「实时 3D 预览」在内存里出图用，避免每次落盘。
        /// </summary>
        public static async Task<byte[]> EncodeToBytesAsync(byte[] bgra, int width, int height)
        {
            if (bgra is null) throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"宽高必须为正数，实际为 {width}×{height}。", nameof(width));
            if (bgra.Length != width * height * 4)
                throw new ArgumentException($"像素数据长度必须为 {width * height * 4}（{width}×{height}×4），实际为 {bgra.Length}。", nameof(bgra));

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                (uint)width, (uint)height, 96, 96, bgra);
            await encoder.FlushAsync();
            output.Seek(0);

            var buffer = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(buffer);
            }
            return buffer;
        }

        /// <summary>64×64 皮肤的便捷重载。</summary>
        public static Task<byte[]> EncodeToBytesAsync(byte[] bgra) => EncodeToBytesAsync(bgra, Size, Size);

        /// <summary>把 64×64 BGRA8 编码成 PNG 写入目标路径（覆盖）。</summary>
        public static async Task EncodeAsync(string pngPath, byte[] bgra)
        {
            if (string.IsNullOrWhiteSpace(pngPath)) throw new ArgumentNullException(nameof(pngPath));
            byte[] buffer = await EncodeToBytesAsync(bgra);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
            await File.WriteAllBytesAsync(pngPath, buffer);
        }
    }
}
