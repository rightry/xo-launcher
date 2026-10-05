using System;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 把 64×64 皮肤合成成一张「正面立绘」预览图 —— 用户能一眼看出是个人形，
    /// 而不是一张平铺的 UV 贴图。（导出时的「截图」用途，以及导出前给用户看的缩略图。）
    ///
    /// 纯 BGRA8 像素运算，不依赖任何 WinUI / WIC 类型，便于在普通控制台程序里做单元测试。
    /// 输出为直通 alpha（非预乘），背景不透明。
    /// </summary>
    public static class SkinPreviewRenderer
    {
        /// <summary>1× 基准画布尺寸：宽 16（右臂 4 + 躯干 8 + 左臂 4），高 32（头 8 + 躯干 12 + 腿 12）。</summary>
        public const int BaseWidth = 16;
        public const int BaseHeight = 32;

        /// <summary>背景色（浅灰）。铺不透明背景是为了让透明区域也看得见，否则用户在浅色界面上会以为图是空的。</summary>
        private const byte BgB = 0xF0;
        private const byte BgG = 0xF0;
        private const byte BgR = 0xF0;

        /// <summary>
        /// 目标尺寸（宽度, 高度）。
        /// </summary>
        public static (int Width, int Height) FrontSheetSize(int scale)
        {
            if (scale < 1)
                throw new ArgumentOutOfRangeException(nameof(scale), scale, "缩放倍率必须 >= 1。");
            return (BaseWidth * scale, BaseHeight * scale);
        }

        /// <summary>
        /// 把 64×64 皮肤合成成正面立绘（BGRA8，直通 alpha，背景不透明）。
        /// scale 为整数倍最近邻放大。
        /// </summary>
        public static byte[] RenderFrontSheet(byte[] bgra64, bool slim, int scale)
        {
            if (bgra64 is null) throw new ArgumentNullException(nameof(bgra64));
            if (bgra64.Length != 64 * 64 * 4)
                throw new ArgumentException($"像素数据长度必须为 {64 * 64 * 4}（64×64×4），实际为 {bgra64.Length}。", nameof(bgra64));

            var (outW, outH) = FrontSheetSize(scale);
            byte[] sheet = BuildBaseSheet(bgra64, slim);
            return ScaleUp(sheet, BaseWidth, BaseHeight, scale, outW, outH);
        }

        /// <summary>
        /// 1× 合成。列的划分是「右臂 | 躯干 | 左臂」——
        /// 注意角色的「右」在正面立绘里出现在**观众左边**，所以右臂/右腿放左列、左臂/左腿放右列。
        /// </summary>
        private static byte[] BuildBaseSheet(byte[] src, bool slim)
        {
            var dst = new byte[BaseWidth * BaseHeight * 4];

            for (int i = 0; i < dst.Length; i += 4)
            {
                dst[i] = BgB;
                dst[i + 1] = BgG;
                dst[i + 2] = BgR;
                dst[i + 3] = 0xFF;
            }

            // 纤细（Alex）手臂是 3 像素宽，且要贴紧躯干一侧，避免肩膀错位
            int armW = slim ? 3 : 4;
            int leftArmX = slim ? 13 : 12;

            // 先基础层，再外层（外层压在上面）
            Blit(src, dst, 8, 8, 4, 0, 8, 8);                       // 头正面
            Blit(src, dst, 40, 8, 4, 0, 8, 8);                      // 帽子正面
            Blit(src, dst, 20, 20, 4, 8, 8, 12);                    // 躯干正面
            Blit(src, dst, 20, 36, 4, 8, 8, 12);                    // 夹克正面
            Blit(src, dst, 44, 20, 0, 8, armW, 12);                 // 右臂正面
            Blit(src, dst, 44, 36, 0, 8, armW, 12);                 // 右臂外层正面
            Blit(src, dst, 36, 52, leftArmX, 8, armW, 12);          // 左臂正面
            Blit(src, dst, 52, 52, leftArmX, 8, armW, 12);          // 左臂外层正面
            Blit(src, dst, 4, 20, 4, 20, 4, 12);                    // 右腿正面
            Blit(src, dst, 4, 36, 4, 20, 4, 12);                    // 右腿外层正面
            Blit(src, dst, 20, 52, 8, 20, 4, 12);                   // 左腿正面
            Blit(src, dst, 4, 52, 8, 20, 4, 12);                    // 左腿外层正面

            return dst;
        }

        /// <summary>把源图 (sx,sy) 起的 w×h 矩形以 source-over 方式叠到目标 (dx,dy)。目标背景不透明，所以输出 alpha 恒为 255。</summary>
        private static void Blit(byte[] src, byte[] dst, int sx, int sy, int dx, int dy, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int si = ((sy + y) * 64 + (sx + x)) * 4;
                    int di = ((dy + y) * BaseWidth + (dx + x)) * 4;
                    int a = src[si + 3];
                    if (a == 0) continue;

                    if (a == 255)
                    {
                        dst[di] = src[si];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                        continue;
                    }

                    dst[di] = (byte)((src[si] * a + dst[di] * (255 - a)) / 255);
                    dst[di + 1] = (byte)((src[si + 1] * a + dst[di + 1] * (255 - a)) / 255);
                    dst[di + 2] = (byte)((src[si + 2] * a + dst[di + 2] * (255 - a)) / 255);
                }
            }
        }

        /// <summary>整数倍最近邻放大（横向逐像素复制，再把整行纵向 BlockCopy）。</summary>
        private static byte[] ScaleUp(byte[] src, int srcW, int srcH, int scale, int outW, int outH)
        {
            var dst = new byte[outW * outH * 4];
            int rowBytes = outW * 4;

            for (int y = 0; y < srcH; y++)
            {
                int rowBase = y * scale * rowBytes;
                for (int x = 0; x < srcW; x++)
                {
                    int si = (y * srcW + x) * 4;
                    int di = rowBase + x * scale * 4;
                    for (int k = 0; k < scale; k++)
                    {
                        dst[di] = src[si];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                        dst[di + 3] = src[si + 3];
                        di += 4;
                    }
                }
                for (int k = 1; k < scale; k++)
                    Buffer.BlockCopy(dst, rowBase, dst, rowBase + k * rowBytes, rowBytes);
            }

            return dst;
        }
    }
}
