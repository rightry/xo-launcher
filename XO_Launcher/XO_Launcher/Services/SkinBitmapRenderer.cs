using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 皮膚點陣渲染集中處：把「像素 → 可顯示的 WriteableBitmap」的全部知識收斂到一處。
    /// 原先這些函式散在 <c>SkinsPage.xaml.cs</c>（ScaleUp / BuildLayerPixels / BuildNetPixels /
    /// BuildHeadPreview / BlitFace / ToBitmap），頁面每加一種預覽就要再手寫一遍
    /// 預乘 / 放大 / 合成，極易寫出第二種亮度。
    /// 約定：輸入一律直通 alpha BGRA8；輸出給 WriteableBitmap 的一律預乘後寫入。
    /// 純靜態、無狀態，執行緒安全（呼叫方負責回到 UI 執行緒再賦給 Image.Source）。
    /// </summary>
    public static class SkinBitmapRenderer
    {
        public static WriteableBitmap RenderLayer(byte[] src, SkinRegion layer, int zoom)
        {
            var px = BuildLayerPixels(src, layer);
            return ScaleUp(px, SkinCanvas.Size, SkinCanvas.Size, zoom);
        }

        public static WriteableBitmap RenderNet(byte[] src, SkinPart part, int zoom)
        {
            var (w, h) = SkinPartLayout.NetSize(part);
            var px = BuildNetPixels(src, part, w, h);
            return ScaleUp(px, w, h, zoom);
        }

        public static WriteableBitmap RenderHead(byte[] src, int scale = 10)
        {
            const int face = 8;
            int outSize = face * scale;
            var dst = new byte[outSize * outSize * 4];
            BlitFace(src, dst, outSize, 8, 8, scale, blend: false);
            BlitFace(src, dst, outSize, 40, 8, scale, blend: true);
            return ToBitmap(dst, outSize, outSize);
        }

        public static byte[] BuildLayerPixels(byte[] src, SkinRegion layer)
        {
            var dst = new byte[SkinCanvas.ByteCount];
            for (int y = 0; y < SkinCanvas.Size; y++)
            {
                for (int x = 0; x < SkinCanvas.Size; x++)
                {
                    if (SkinCanvas.RegionAt(x, y) != layer) continue;
                    int i = (y * SkinCanvas.Size + x) * 4;
                    byte a = src[i + 3];
                    if (a == 0) continue;
                    dst[i] = (byte)(src[i] * a / 255);
                    dst[i + 1] = (byte)(src[i + 1] * a / 255);
                    dst[i + 2] = (byte)(src[i + 2] * a / 255);
                    dst[i + 3] = a;
                }
            }
            return dst;
        }

        /// <summary>
        /// 部位展開圖：基礎層先鋪，外層 source-over 疊上。兩層展開矩形逐格重合，
        /// 疊完即該部位的真實立體外觀（所見即所得）。
        /// </summary>
        public static byte[] BuildNetPixels(byte[] src, SkinPart part, int w, int h)
        {
            var dst = new byte[w * h * 4];
            var faces = SkinPartLayout.FacesOf(part);

            // 先把每個展開格反查回貼圖座標，逐格合成，避免對每像素掃全表。
            for (int ny = 0; ny < h; ny++)
            {
                for (int nx = 0; nx < w; nx++)
                {
                    byte b = 0, g = 0, r = 0, a = 0;
                    foreach (var f in faces)
                    {
                        if (!f.Contains(nx, ny)) continue;
                        var fb = SkinCanvas.FaceBounds(f.FaceIndex);
                        int si = ((fb.Y1 + (ny - f.Y)) * SkinCanvas.Size + (fb.X1 + (nx - f.X))) * 4;
                        if (f.Layer == SkinRegion.Base)
                        {
                            b = src[si]; g = src[si + 1]; r = src[si + 2]; a = src[si + 3];
                        }
                        else
                        {
                            byte oa = src[si + 3];
                            if (oa == 255) { b = src[si]; g = src[si + 1]; r = src[si + 2]; a = 0xFF; }
                            else if (oa > 0)
                            {
                                int back = a * (255 - oa) / 255;
                                int na = oa + back;
                                if (na > 0)
                                {
                                    b = (byte)((src[si] * oa + b * back) / na);
                                    g = (byte)((src[si + 1] * oa + g * back) / na);
                                    r = (byte)((src[si + 2] * oa + r * back) / na);
                                    a = (byte)na;
                                }
                            }
                        }
                    }

                    int di = (ny * w + nx) * 4;
                    dst[di] = (byte)(b * a / 255);
                    dst[di + 1] = (byte)(g * a / 255);
                    dst[di + 2] = (byte)(r * a / 255);
                    dst[di + 3] = a;
                }
            }
            return dst;
        }

        /// <summary>
        /// 最近鄰放大後 1:1 交給 Image。WinUI 3 沒有 RenderOptions.BitmapInterpolationMode，
        /// 讓 Image 自己放大必走雙線性、像素會糊，所以放大必須自己做。
        /// </summary>
        public static WriteableBitmap ScaleUp(byte[] src, int srcW, int srcH, int zoom)
        {
            if (zoom <= 1) return ToBitmap(src, srcW, srcH);
            int outW = srcW * zoom;
            int outH = srcH * zoom;
            int rowBytes = outW * 4;
            var dst = new byte[outW * outH * 4];

            for (int y = 0; y < srcH; y++)
            {
                int rowBase = y * zoom * rowBytes;
                for (int x = 0; x < srcW; x++)
                {
                    int si = (y * srcW + x) * 4;
                    int di = rowBase + x * zoom * 4;
                    for (int k = 0; k < zoom; k++)
                    {
                        dst[di] = src[si];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                        dst[di + 3] = src[si + 3];
                        di += 4;
                    }
                }
                for (int k = 1; k < zoom; k++)
                    Buffer.BlockCopy(dst, rowBase, dst, rowBase + k * rowBytes, rowBytes);
            }
            return ToBitmap(dst, outW, outH);
        }

        private static void BlitFace(byte[] src, byte[] dst, int outSize, int sx, int sy, int scale, bool blend)
        {
            const int face = 8;
            for (int y = 0; y < face; y++)
            {
                for (int x = 0; x < face; x++)
                {
                    int si = ((sy + y) * SkinCanvas.Size + (sx + x)) * 4;
                    byte a = src[si + 3];
                    if (blend && a == 0) continue;
                    for (int oy = 0; oy < scale; oy++)
                    {
                        for (int ox = 0; ox < scale; ox++)
                        {
                            int di = (((y * scale) + oy) * outSize + (x * scale) + ox) * 4;
                            if (!blend || a == 255)
                            {
                                dst[di] = src[si];
                                dst[di + 1] = src[si + 1];
                                dst[di + 2] = src[si + 2];
                            }
                            else
                            {
                                dst[di] = (byte)((src[si] * a + dst[di] * (255 - a)) / 255);
                                dst[di + 1] = (byte)((src[si + 1] * a + dst[di + 1] * (255 - a)) / 255);
                                dst[di + 2] = (byte)((src[si + 2] * a + dst[di + 2] * (255 - a)) / 255);
                            }
                            dst[di + 3] = 0xFF;
                        }
                    }
                }
            }
        }

        public static WriteableBitmap ToBitmap(byte[] pixels, int width, int height)
        {
            var bitmap = new WriteableBitmap(width, height);
            using var stream = bitmap.PixelBuffer.AsStream();
            stream.Write(pixels, 0, pixels.Length);
            return bitmap;
        }

        /// <summary>
        /// 部位展開圖的輔助網格：沿每個擺放矩形（去重後）的邊界畫 1px 框線。
        /// 基礎層與外層矩形完全重合，先按座標去重；輸出為「邏輯像素 × zoom」尺寸下的線段表。
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<(double X1, double Y1, double X2, double Y2)> NetGridLines(SkinPart part, int zoom)
        {
            var lines = new System.Collections.Generic.List<(double, double, double, double)>();
            var seen = new System.Collections.Generic.HashSet<(int, int, int, int)>();
            foreach (var f in SkinPartLayout.FacesOf(part))
            {
                if (!seen.Add((f.X, f.Y, f.Width, f.Height))) continue;
                double left = f.X * zoom, top = f.Y * zoom;
                double right = (f.X + f.Width) * zoom, bottom = (f.Y + f.Height) * zoom;
                for (int i = 0; i <= f.Width; i++)
                {
                    double pos = left + i * zoom;
                    lines.Add((pos, top, pos, bottom));
                }
                for (int i = 0; i <= f.Height; i++)
                {
                    double pos = top + i * zoom;
                    lines.Add((left, pos, right, pos));
                }
            }
            return lines;
        }

        /// <summary>整圖模式的均勻網格線。zoom &lt; 8 時每 8 像素一條（對齊皮膚區塊邊界），否則逐像素。</summary>
        public static System.Collections.Generic.IReadOnlyList<(double X1, double Y1, double X2, double Y2)> WholeGridLines(int zoom)
        {
            var lines = new System.Collections.Generic.List<(double, double, double, double)>();
            double size = SkinCanvas.Size * zoom;
            int step = zoom >= 8 ? 1 : 8;
            for (int i = 0; i <= SkinCanvas.Size; i += step)
            {
                double pos = i * zoom;
                lines.Add((pos, 0, pos, size));
                lines.Add((0, pos, size, pos));
            }
            return lines;
        }
    }
}
