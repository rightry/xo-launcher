using System;
using System.Collections.Generic;

namespace XO_Launcher.Models
{
    /// <summary>像素属于皮肤的哪一层。</summary>
    public enum SkinRegion { Unused = 0, Base = 1, Overlay = 2 }

    /// <summary>
    /// 纯逻辑的 Minecraft 皮肤像素画布（64×64、BGRA8、非预乘）。
    /// 不依赖任何 WinUI / WIC / Windows.* 类型，便于在普通控制台程序里做单元测试。
    /// </summary>
    public sealed class SkinCanvas
    {
        public const int Size = 64;
        public const int ByteCount = Size * Size * 4;   // BGRA8 = 16384

        /// <summary>像素缓冲，长度恒为 <see cref="ByteCount"/>。布局为逐行 BGRA8、非预乘（straight alpha）。</summary>
        public byte[] Pixels { get; }

        /// <summary>是否为 slim（Alex）模型。slim 只在渲染时收窄手臂宽度，不改变 64×64 区域布局，因此仅作存储/回传。</summary>
        public bool IsSlim { get; set; }

        /// <summary>单个立方体面（x1,y1,x2,y2，x2/y2 为排除值）。标准 Minecraft 64×64 展开下，12 个盒子各拆 6 面，共 72 项。
        /// 这是「哪些像素属于哪一层」的唯一真相来源：前 6 盒（0-5 头/右腿/躯干/右臂/左腿/左臂）为 Base，
        /// 后 6 盒（6-11 帽/夹克/右腿覆盖/右臂覆盖/左臂覆盖/左腿覆盖）为 Overlay。
        /// <see cref="RegionAt"/>、<see cref="FaceBoundsAt"/>、<see cref="MirrorPointAt"/> 全部由此表推导，避免双份真相。</summary>
        private static readonly (int X1, int Y1, int X2, int Y2)[] Faces =
        {
            // Base — 头部（6 面）
            (8, 0, 16, 8), (16, 0, 24, 8), (0, 8, 8, 16), (8, 8, 16, 16), (16, 8, 24, 16), (24, 8, 32, 16),
            // Base — 右腿
            (4, 16, 8, 20), (8, 16, 12, 20), (0, 20, 4, 32), (4, 20, 8, 32), (8, 20, 12, 32), (12, 20, 16, 32),
            // Base — 身体
            (20, 16, 28, 20), (28, 16, 36, 20), (16, 20, 20, 32), (20, 20, 28, 32), (28, 20, 32, 32), (32, 20, 40, 32),
            // Base — 右臂
            (44, 16, 48, 20), (48, 16, 52, 20), (40, 20, 44, 32), (44, 20, 48, 32), (48, 20, 52, 32), (52, 20, 56, 32),
            // Base — 左腿
            (20, 48, 24, 52), (24, 48, 28, 52), (16, 52, 20, 64), (20, 52, 24, 64), (24, 52, 28, 64), (28, 52, 32, 64),
            // Base — 左臂
            (36, 48, 40, 52), (40, 48, 44, 52), (32, 52, 36, 64), (36, 52, 40, 64), (40, 52, 44, 64), (44, 52, 48, 64),
            // Overlay — 帽子
            (40, 0, 48, 8), (48, 0, 56, 8), (32, 8, 40, 16), (40, 8, 48, 16), (48, 8, 56, 16), (56, 8, 64, 16),
            // Overlay — 夹克
            (20, 32, 28, 36), (28, 32, 36, 36), (16, 36, 20, 48), (20, 36, 28, 48), (28, 36, 32, 48), (32, 36, 40, 48),
            // Overlay — 右腿覆盖层
            (4, 32, 8, 36), (8, 32, 12, 36), (0, 36, 4, 48), (4, 36, 8, 48), (8, 36, 12, 48), (12, 36, 16, 48),
            // Overlay — 右臂覆盖层
            (44, 32, 48, 36), (48, 32, 52, 36), (40, 36, 44, 48), (44, 36, 48, 48), (48, 36, 52, 48), (52, 36, 56, 48),
            // Overlay — 左臂覆盖层
            (52, 48, 56, 52), (56, 48, 60, 52), (48, 52, 52, 64), (52, 52, 56, 64), (56, 52, 60, 64), (60, 52, 64, 64),
            // Overlay — 左腿覆盖层
            (4, 48, 8, 52), (8, 48, 12, 52), (0, 52, 4, 64), (4, 52, 8, 64), (8, 52, 12, 64), (12, 52, 16, 64),
        };

        /// <summary>盒子配对：下标 = Faces 中的盒子序号（每盒 6 面）。0→0(头) 2→2(躯干) 6→6(帽) 7→7(夹克) 自成对；
        /// 1↔4(右腿↔左腿) 3↔5(右臂↔左臂) 8↔11(右腿覆盖↔左腿覆盖) 9↔10(右臂覆盖↔左臂覆盖)。</summary>
        private static readonly int[] BoxPair =
        {
            0,  // 0 头
            4,  // 1 右腿 → 左腿
            2,  // 2 躯干
            5,  // 3 右臂 → 左臂
            1,  // 4 左腿 → 右腿
            3,  // 5 左臂 → 右臂
            6,  // 6 帽
            7,  // 7 夹克
            11, // 8 右腿覆盖 → 左腿覆盖
            10, // 9 右臂覆盖 → 左臂覆盖
            9,  // 10 左臂覆盖 → 右臂覆盖
            8,  // 11 左腿覆盖 → 右腿覆盖
        };

        /// <summary>槽位配对：顺序 = [顶, 底, 右, 前, 左, 背]。0/1/3/5(顶/底/前/背)就地翻转，2↔4(右面↔左面)。</summary>
        private static readonly int[] SlotPair = { 0, 1, 4, 3, 2, 5 };

        /// <summary>分配全透明画布。</summary>
        public SkinCanvas()
        {
            Pixels = new byte[ByteCount];
        }

        /// <summary>创建一张全透明画布。</summary>
        public static SkinCanvas CreateEmpty() => new SkinCanvas();

        /// <summary>从 BGRA8 缓冲构造画布。长度不符时抛 <see cref="ArgumentException"/>。</summary>
        public static SkinCanvas FromBgra(byte[] bgra, bool slim)
        {
            if (bgra is null) throw new ArgumentNullException(nameof(bgra));
            if (bgra.Length != ByteCount)
                throw new ArgumentException($"像素数据长度必须为 {ByteCount}（64×64×4），实际为 {bgra.Length}。", nameof(bgra));
            var canvas = new SkinCanvas();
            Buffer.BlockCopy(bgra, 0, canvas.Pixels, 0, ByteCount);
            canvas.IsSlim = slim;
            return canvas;
        }

        /// <summary>返回像素缓冲的副本。</summary>
        public byte[] ClonePixels()
        {
            var copy = new byte[ByteCount];
            Buffer.BlockCopy(Pixels, 0, copy, 0, ByteCount);
            return copy;
        }

        /// <summary>用 BGRA8 缓冲覆盖当前像素。长度不符时抛 <see cref="ArgumentException"/>。</summary>
        public void LoadPixels(byte[] bgra)
        {
            if (bgra is null) throw new ArgumentNullException(nameof(bgra));
            if (bgra.Length != ByteCount)
                throw new ArgumentException($"像素数据长度必须为 {ByteCount}（64×64×4），实际为 {bgra.Length}。", nameof(bgra));
            Buffer.BlockCopy(bgra, 0, Pixels, 0, ByteCount);
        }

        /// <summary>判断坐标是否在画布范围内。</summary>
        public static bool InBounds(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size;

        /// <summary>Faces 表的面数（72）。</summary>
        public static int FaceCount => Faces.Length;

        /// <summary>
        /// 返回坐标命中的 <see cref="Faces"/> 下标；不在任何立方体面内（含越界）时返回 -1。
        /// RegionAt / FaceBoundsAt / MirrorPointAt 与 <see cref="SkinPartLayout"/> 共用的唯一查找入口，避免多处重复手写循环。
        /// </summary>
        public static int FaceIndexAt(int x, int y)
        {
            if (!InBounds(x, y)) return -1;
            for (int i = 0; i < Faces.Length; i++)
            {
                var f = Faces[i];
                if (x >= f.X1 && x < f.X2 && y >= f.Y1 && y < f.Y2)
                    return i;
            }
            return -1;
        }

        /// <summary>按下标取该面属于哪一层。Faces 前 6 盒（下标 0-35）= Base，后 6 盒（36-71）= Overlay。</summary>
        public static SkinRegion LayerOfFace(int index)
        {
            if (index < 0 || index >= Faces.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"面下标必须在 0..{Faces.Length - 1} 之间。");
            return index < 36 ? SkinRegion.Base : SkinRegion.Overlay;
        }

        /// <summary>按下标取面的边界（x2/y2 为排除值）。</summary>
        public static (int X1, int Y1, int X2, int Y2) FaceBounds(int index)
        {
            if (index < 0 || index >= Faces.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"面下标必须在 0..{Faces.Length - 1} 之间。");
            var f = Faces[index];
            return (f.X1, f.Y1, f.X2, f.Y2);
        }

        /// <summary>返回某坐标属于哪一层。越界，或落在方块之间「不被任何立方体面覆盖」的像素（MC 不渲染的 padding）时返回 <see cref="SkinRegion.Unused"/>。</summary>
        public static SkinRegion RegionAt(int x, int y)
        {
            int fi = FaceIndexAt(x, y);
            return fi < 0 ? SkinRegion.Unused : LayerOfFace(fi);
        }

        /// <summary>返回 (x,y) 所属立方体面的边界（x2/y2 为排除值）；不属于任何面时返回整张画布哨兵 (0,0,Size,Size)。</summary>
        public static (int X1, int Y1, int X2, int Y2) FaceBoundsAt(int x, int y)
        {
            int fi = FaceIndexAt(x, y);
            if (fi < 0) return (0, 0, Size, Size);
            var f = Faces[fi];
            return (f.X1, f.Y1, f.X2, f.Y2);
        }

        /// <summary>
        /// 返回 (x,y) 在「角色左右镜像」下的对称像素。按立方体面对配对（不是整张画布翻转）：
        /// 头/帽/躯干/夹克对自己成对（顶/底/前/背就地水平翻转，左面↔右面互换）；
        /// 右腿↔左腿、右臂↔左臂、右腿覆盖↔左腿覆盖、右臂覆盖↔左臂覆盖。
        /// (x,y) 不属于任何面（Unused）时返回 null（无对称点）。
        /// </summary>
        public static (int X, int Y)? MirrorPointAt(int x, int y)
        {
            int fi = FaceIndexAt(x, y);
            if (fi < 0) return null;

            int box = fi / 6;
            int slot = fi % 6;
            int pi = BoxPair[box] * 6 + SlotPair[slot];
            var src = Faces[fi];
            var dst = Faces[pi];

            int pW = dst.X2 - dst.X1;
            int u = x - src.X1;
            if (u < 0) u = 0;
            if (u >= pW) u = pW - 1;          // 配对面宽度一致，保险起见夹取
            int v = y - src.Y1;
            int X = dst.X1 + (pW - 1 - u);       // 水平翻转
            int Y = dst.Y1 + v;                  // 垂直不动
            return (X, Y);
        }

        /// <summary>读取像素（BGRA 顺序）。越界时返回全 0。</summary>
        public void GetPixel(int x, int y, out byte b, out byte g, out byte r, out byte a)
        {
            if (!InBounds(x, y)) { b = g = r = a = 0; return; }
            int i = (y * Size + x) * 4;
            b = Pixels[i];
            g = Pixels[i + 1];
            r = Pixels[i + 2];
            a = Pixels[i + 3];
        }

        /// <summary>写入像素（BGRA 顺序）。越界时静默忽略。</summary>
        public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
        {
            if (!InBounds(x, y)) return;
            int i = (y * Size + x) * 4;
            Pixels[i] = b;
            Pixels[i + 1] = g;
            Pixels[i + 2] = r;
            Pixels[i + 3] = a;
        }

        /// <summary>内部写入，不做越界检查（调用方已保证）。</summary>
        private void SetPixelRaw(int x, int y, byte b, byte g, byte r, byte a)
        {
            int i = (y * Size + x) * 4;
            Pixels[i] = b;
            Pixels[i + 1] = g;
            Pixels[i + 2] = r;
            Pixels[i + 3] = a;
        }

        /// <summary>
        /// Bresenham 直线，用于把鼠标拖动的相邻两点连起来（否则快速拖动会断点）。
        /// scope 限定落笔区域（Unused 视为「不限制」）；mirrorX 为 true 时同时落笔到 <see cref="MirrorPointAt"/> 求得的对称像素（角色左右镜像，按立方体面对配对，不是整张画布翻转）。
        /// </summary>
        public void DrawLine(int x0, int y0, int x1, int y1,
                             byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                PlotLinePoint(x0, y0, b, g, r, a, scope, mirrorX);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>DrawLine 的单体落笔：scope 过滤 + 可选镜像。</summary>
        private void PlotLinePoint(int x, int y, byte b, byte g, byte r, byte a, SkinRegion scope, bool mirrorX)
        {
            if (InBounds(x, y) && (scope == SkinRegion.Unused || RegionAt(x, y) == scope))
                SetPixelRaw(x, y, b, g, r, a);

            // 镜像点仍要过 scope 过滤；不在任何面上（Unused）时 MirrorPointAt 返回 null，不落笔
            if (mirrorX && MirrorPointAt(x, y) is (int mx, int my))
            {
                if (InBounds(mx, my) && (scope == SkinRegion.Unused || RegionAt(mx, my) == scope))
                    SetPixelRaw(mx, my, b, g, r, a);
            }
        }

        /// <summary>
        /// 4 连通油漆桶。tolerance 为 0~255 的每通道容差；返回是否真的改了像素。
        /// scope 限定填充区域（Unused 视为「不限制」）；mirrorX 为 true 时同时填充 <see cref="MirrorPointAt"/> 求得的对称像素所在连通区。
        /// 落笔范围限定为整张画布（等价于不带裁剪框的重载）。
        /// </summary>
        public bool FloodFill(int x, int y, byte b, byte g, byte r, byte a,
                              int tolerance, SkinRegion scope, bool mirrorX)
            => FloodFill(x, y, b, g, r, a, tolerance, scope, mirrorX, 0, 0, Size, Size);

        /// <summary>
        /// 带裁剪框的 4 连通油漆桶。除落笔被限制在 [clipX1,clipX2)×[clipY1,clipY2) 内、且扩展前沿不越出该框外，其余语义与 9 参重载一致。
        /// 用于「只填充点击所在的单个立方体面」——调用方把 <see cref="FaceBoundsAt"/> 的结果作为裁剪框传入。
        /// mirrorX 为 true 时，镜像填充使用 <see cref="MirrorPointAt"/> 求得的对称像素作为种子，并以该对称像素自身的面边界作为裁剪框（保证对称面被完整填充）。
        /// </summary>
        public bool FloodFill(int x, int y, byte b, byte g, byte r, byte a, int tolerance, SkinRegion scope, bool mirrorX,
                              int clipX1, int clipY1, int clipX2, int clipY2)
        {
            bool changed = FillFrom(x, y, b, g, r, a, tolerance, scope, clipX1, clipY1, clipX2, clipY2);
            if (mirrorX && MirrorPointAt(x, y) is (int mx, int my))
            {
                // 裁剪框用镜像点自身的面边界，而不是把原裁剪框水平翻转
                var mclip = FaceBoundsAt(mx, my);
                if (FillFrom(mx, my, b, g, r, a, tolerance, scope, mclip.X1, mclip.Y1, mclip.X2, mclip.Y2))
                    changed = true;
            }
            return changed;
        }

        /// <summary>从 (sx,sy) 出发，对与种子色（容差内）连通、且位于裁剪框内的区域填充新色。返回是否改动了像素。</summary>
        private bool FillFrom(int sx, int sy, byte nb, byte ng, byte nr, byte na, int tolerance, SkinRegion scope,
                              int cX1, int cY1, int cX2, int cY2)
        {
            // 种子必须在裁剪框内，否则不填充
            if (sx < cX1 || sx >= cX2 || sy < cY1 || sy >= cY2) return false;
            if (scope != SkinRegion.Unused && RegionAt(sx, sy) != scope) return false;

            GetPixel(sx, sy, out byte tb, out byte tg, out byte tr, out byte ta);
            // 种子已等于目标色（容差内）则不填充，保证可返回 false
            if (WithinTolerance(tb, tg, tr, ta, nb, ng, nr, na, tolerance))
                return false;

            var stack = new Stack<int>(Size * Size);   // 用 y*Size+x 编码坐标，省去元组分配
            stack.Push(sy * Size + sx);
            int filled = 0;

            while (stack.Count > 0)
            {
                int cell = stack.Pop();
                int cx = cell % Size;
                int cy = cell / Size;
                if (!InBounds(cx, cy)) continue;
                if (cx < cX1 || cx >= cX2 || cy < cY1 || cy >= cY2) continue;   // 裁剪框
                if (scope != SkinRegion.Unused && RegionAt(cx, cy) != scope) continue;

                GetPixel(cx, cy, out byte cb, out byte cg, out byte cr, out byte ca);
                if (!WithinTolerance(cb, cg, cr, ca, tb, tg, tr, ta, tolerance)) continue;

                SetPixelRaw(cx, cy, nb, ng, nr, na);
                filled++;

                // 仅在裁剪框内扩展相邻像素，前沿不会越框
                if (cy + 1 < cY2) stack.Push((cy + 1) * Size + cx);
                if (cy > cY1) stack.Push((cy - 1) * Size + cx);
                if (cx + 1 < cX2) stack.Push(cy * Size + (cx + 1));
                if (cx > cX1) stack.Push(cy * Size + (cx - 1));
            }

            return filled > 0;
        }

        /// <summary>判断两色各通道差值是否都不超过容差。</summary>
        private static bool WithinTolerance(byte cb, byte cg, byte cr, byte ca,
                                            byte tb, byte tg, byte tr, byte ta, int tol)
        {
            return Math.Abs(cb - tb) <= tol &&
                   Math.Abs(cg - tg) <= tol &&
                   Math.Abs(cr - tr) <= tol &&
                   Math.Abs(ca - ta) <= tol;
        }

        /// <summary>取色器：返回 (b,g,r,a)；越界返回全 0。</summary>
        public (byte B, byte G, byte R, byte A) Pick(int x, int y)
        {
            if (!InBounds(x, y)) return (0, 0, 0, 0);
            int i = (y * Size + x) * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }

        /// <summary>把 scope 覆盖的整片像素设成给定颜色（用于「清空本层」）。scope 为 Unused 时清空整张画布。</summary>
        public void ClearRegion(SkinRegion scope, byte b, byte g, byte r, byte a)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (scope == SkinRegion.Unused || RegionAt(x, y) == scope)
                        SetPixelRaw(x, y, b, g, r, a);
                }
            }
        }
    }
}
