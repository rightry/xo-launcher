using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 屏幕取色的底层抓帧：用 Win32 BitBlt 把整块虚拟屏幕（含多显示器）拷进一张
    /// 32 位 BGRA 位图。<see cref="Views.EyedropperWindow"/> 拿这张图当背景显示，
    /// 用户点哪儿就取哪个像素的颜色。
    ///
    /// 不用 Windows.Graphics.Capture：那套要么需要用户点选目标窗口/显示器的选取器
    /// （GraphicsCapturePicker 会弹系统 UI，打断「随手取色」的心流），要么需要
    /// 先建 GraphicsCaptureItem，对「全屏取一个像素」这种需求过重。BitBlt 一次成型，
    /// 不弹任何系统框，抓完即走。
    /// </summary>
    internal sealed class ScreenCapture : IDisposable
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>虚拟屏幕左上角相对主屏左上角的偏移，抓帧时用它把多屏坐标归一到 (0,0)。</summary>
        public int OriginX { get; }
        public int OriginY { get; }

        private readonly byte[] _pixels; // BGRA，行主序，自上而下，宽度 = Width

        private ScreenCapture(int originX, int originY, int width, int height, byte[] pixels)
        {
            OriginX = originX;
            OriginY = originY;
            X = originX;
            Y = originY;
            Width = width;
            Height = height;
            _pixels = pixels;
        }

        /// <summary>读取虚拟屏幕坐标 (screenX, screenY) 处的颜色；越界返回透明黑。</summary>
        public Color GetPixel(int screenX, int screenY)
        {
            int px = screenX - OriginX;
            int py = screenY - OriginY;
            if (px < 0 || py < 0 || px >= Width || py >= Height)
                return Color.FromArgb(0, 0, 0, 0);
            int i = (py * Width + px) * 4;
            // DIB 是 BGRA 顺序
            return Color.FromArgb(_pixels[i + 3], _pixels[i + 2], _pixels[i + 1], _pixels[i]);
        }

        /// <summary>把整帧包装成可直接喂给 Image.Source 的位图源。</summary>
        public Microsoft.UI.Xaml.Media.ImageSource ToBitmapSource()
        {
            // 用 WriteableBitmap 而不是 SoftwareBitmapSource：前者直接吃 BGRA 字节缓冲，
            // 无需在 SoftwareBitmap 与 XAML 位图之间再做一次颜色格式协商。
            var bitmap = new WriteableBitmap(Width, Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(_pixels, 0, _pixels.Length);
            }
            bitmap.Invalidate();
            return bitmap;
        }

        public static ScreenCapture? CaptureVirtualScreen()
        {
            int vx = GetSystemMetrics(SmXVirtualScreen);
            int vy = GetSystemMetrics(SmYVirtualScreen);
            int vw = GetSystemMetrics(SmCxVirtualScreen);
            int vh = GetSystemMetrics(SmCyVirtualScreen);
            if (vw <= 0 || vh <= 0)
            {
                vw = GetSystemMetrics(SmCxScreen);
                vh = GetSystemMetrics(SmCyScreen);
                vx = vy = 0;
            }

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, vw, vh);
            IntPtr oldObj = IntPtr.Zero;
            try
            {
                oldObj = SelectObject(hdcMem, hBitmap);
                // CAPTUREBLT 才能把分层窗口（部分覆盖层、某些游戏 HUD）一并抓进来。
                if (!BitBlt(hdcMem, 0, 0, vw, vh, hdcScreen, vx, vy, SrcCopy | CaptureBlt))
                {
                    LauncherLogService.Warn("Eyedropper", $"BitBlt 失败，虚拟屏 {vw}x{vh}@({vx},{vy})");
                    return null;
                }

                var bmi = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = vw,
                    Height = -vh, // 负高 = 自上而下，省得后面手动翻行
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0 // BI_RGB
                };

                var pixels = new byte[vw * vh * 4];
                int read = GetDIBits(hdcScreen, hBitmap, 0, (uint)vh, pixels, ref bmi, DibRgb);
                if (read == 0)
                {
                    LauncherLogService.Warn("Eyedropper", $"GetDIBits 返回 0，虚拟屏 {vw}x{vh}");
                    return null;
                }

                return new ScreenCapture(vx, vy, vw, vh, pixels);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Eyedropper", "抓取屏幕失败", ex);
                return null;
            }
            finally
            {
                if (oldObj != IntPtr.Zero) SelectObject(hdcMem, oldObj);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        public void Dispose() { }

        private const int SmCxScreen = 0;
        private const int SmCyScreen = 1;
        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;
        private const uint SrcCopy = 0x00CC0020;
        private const uint CaptureBlt = 0x40000000;
        private const uint DibRgb = 0;

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }
    }
}
