using System;
using System.Collections.Generic;

namespace XO_Launcher.Models
{
    /// <summary>面向用户的「身体部位」：一个基础盒（皮肤）+ 对应的外层盒（帽子/衣服）。</summary>
    public enum SkinPart { Head, Body, RightArm, LeftArm, RightLeg, LeftLeg }

    /// <summary>
    /// 展开图上的一个面：皮肤纹理里的某个立方体面被摆到「立方体展开图」的哪个位置。
    /// 坐标单位 = 皮肤像素（1:1），即把展开图按 1 倍画出来时的坐标。
    /// </summary>
    public readonly struct NetFace
    {
        public NetFace(int faceIndex, int x, int y, int width, int height, SkinRegion layer)
        {
            FaceIndex = faceIndex;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Layer = layer;
        }

        /// <summary>在 <see cref="SkinCanvas.FaceBounds"/> 里的下标。</summary>
        public int FaceIndex { get; }

        /// <summary>在展开图中的左上角。</summary>
        public int X { get; }
        public int Y { get; }

        /// <summary>在展开图中的尺寸（等于该面在纹理里的尺寸）。</summary>
        public int Width { get; }
        public int Height { get; }

        /// <summary>属于哪一层。</summary>
        public SkinRegion Layer { get; }

        public bool Contains(int netX, int netY) =>
            netX >= X && netX < X + Width && netY >= Y && netY < Y + Height;

        public override string ToString() =>
            $"[{Layer}] 面{FaceIndex} @({X},{Y}) {Width}×{Height}";
    }

    /// <summary>
    /// 「立方体展开图（net）」布局：把某个部位摊开成纸模那样 —— 右/前/左/背 横排一行，
    /// 顶与底并排放在这一行的上方，让用户看到的是一个立体的部位而不是 UV 贴图里的一块矩形。
    ///
    /// 纯逻辑，不依赖任何 WinUI 类型，便于在普通控制台程序里做单元测试。
    ///
    /// 关键事实（已逐项核对过 72 个面）：MC 的贴图布局本身就是这个展开图的紧凑排列。
    /// 设盒子 UV 原点为 (ox,oy)、立方体尺寸为 宽 W / 高 H / 厚 D，则展开位置为
    /// 顶 (D,0,W×D) / 底 (D+W,0,W×D) / 右 (0,D,D×H) / 前 (D,D,W×H) / 左 (D+W,D,D×H) / 背 (2D+W,D,W×H)，
    /// 整体尺寸 **(2D+2W) × (D+H)**。
    ///
    /// 注意顶行只是「顶+底」两块并排（宽 2W），所以展开图上还剩两个 D×D 的空白角
    /// （头 8×8、四肢 4×4）—— 那正是 MC 贴图里同样不渲染的 padding，属正常。
    ///
    /// 并且每个部位「基础盒」与「外层盒」的 (W,H,D) 完全相同，所以两层的展开图**位置完全重合**，
    /// 可以直接叠在同一个视图里 —— 这正是编辑器能「所见即所得」的前提。
    /// </summary>
    public static class SkinPartLayout
    {
        /// <summary>UI 里的部位顺序：头、身体、右臂、左臂、右腿、左腿。</summary>
        public static readonly SkinPart[] PartOrder =
        {
            SkinPart.Head, SkinPart.Body, SkinPart.RightArm, SkinPart.LeftArm, SkinPart.RightLeg, SkinPart.LeftLeg,
        };

        /// <summary>
        /// 盒子（<see cref="SkinCanvas"/> 里 Faces 表每 6 项一组，组号即盒号）→ 部位。
        /// 盒号顺序：0 头 / 1 右腿 / 2 躯干 / 3 右臂 / 4 左腿 / 5 左臂 / 6 帽 / 7 夹克 /
        /// 8 右腿覆盖 / 9 右臂覆盖 / 10 左臂覆盖 / 11 左腿覆盖。
        /// </summary>
        private static readonly SkinPart[] BoxToPart =
        {
            SkinPart.Head, SkinPart.RightLeg, SkinPart.Body, SkinPart.RightArm, SkinPart.LeftLeg, SkinPart.LeftArm,
            SkinPart.Head, SkinPart.Body, SkinPart.RightLeg, SkinPart.RightArm, SkinPart.LeftArm, SkinPart.LeftLeg,
        };

        /// <summary>展开位置用的槽位序号（与 Faces 表每盒内的排列一致）。</summary>
        private const int SlotTop = 0;
        private const int SlotBottom = 1;
        private const int SlotRight = 2;
        private const int SlotFront = 3;
        private const int SlotLeft = 4;
        private const int SlotBack = 5;

        private static readonly NetFace[][] ByPart = BuildAll();

        /// <summary>该部位用到的全部面（基础盒 6 面 + 外层盒 6 面），已按展开图位置摆好。</summary>
        public static IReadOnlyList<NetFace> FacesOf(SkinPart part) => ByPart[Index(part)];

        /// <summary>展开图整体尺寸（宽度, 高度），单位 = 皮肤像素。</summary>
        public static (int Width, int Height) NetSize(SkinPart part)
        {
            int w = 0, h = 0;
            foreach (var f in FacesOf(part))
            {
                if (f.X + f.Width > w) w = f.X + f.Width;
                if (f.Y + f.Height > h) h = f.Y + f.Height;
            }
            return (w, h);
        }

        /// <summary>展开图坐标 → 该层在 64×64 贴图里的坐标；该点不属于这个部位的这一层时返回 null。</summary>
        public static (int X, int Y)? ToTexture(SkinPart part, int netX, int netY, SkinRegion layer)
        {
            foreach (var f in FacesOf(part))
            {
                if (f.Layer != layer) continue;
                if (!f.Contains(netX, netY)) continue;
                var fb = SkinCanvas.FaceBounds(f.FaceIndex);
                return (fb.X1 + (netX - f.X), fb.Y1 + (netY - f.Y));
            }
            return null;
        }

        /// <summary>64×64 贴图坐标 → 展开图坐标；该像素不属于这个部位时返回 null。</summary>
        public static (int X, int Y)? ToNet(SkinPart part, int texX, int texY)
        {
            int fi = SkinCanvas.FaceIndexAt(texX, texY);
            if (fi < 0) return null;

            foreach (var f in FacesOf(part))
            {
                if (f.FaceIndex != fi) continue;
                var fb = SkinCanvas.FaceBounds(fi);
                return (f.X + (texX - fb.X1), f.Y + (texY - fb.Y1));
            }
            return null;
        }

        /// <summary>该贴图像素属于哪个部位；不属于任何立方体面（Unused / padding）时返回 null。</summary>
        public static SkinPart? PartOf(int texX, int texY)
        {
            int fi = SkinCanvas.FaceIndexAt(texX, texY);
            if (fi < 0) return null;
            return BoxToPart[fi / 6];
        }

        /// <summary>部位的中文名。</summary>
        public static string DisplayName(SkinPart part) => part switch
        {
            SkinPart.Head => "头",
            SkinPart.Body => "身体",
            SkinPart.RightArm => "右臂",
            SkinPart.LeftArm => "左臂",
            SkinPart.RightLeg => "右腿",
            SkinPart.LeftLeg => "左腿",
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "未知部位。"),
        };

        /// <summary>该部位包含的两个盒子（基础盒 + 外层盒）。</summary>
        private static int[] BoxesOf(SkinPart part) => part switch
        {
            SkinPart.Head => new[] { 0, 6 },
            SkinPart.Body => new[] { 2, 7 },
            SkinPart.RightArm => new[] { 3, 9 },
            SkinPart.LeftArm => new[] { 5, 10 },
            SkinPart.RightLeg => new[] { 1, 8 },
            SkinPart.LeftLeg => new[] { 4, 11 },
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "未知部位。"),
        };

        private static int Index(SkinPart part)
        {
            int i = (int)part;
            if (i < 0 || i >= PartOrder.Length)
                throw new ArgumentOutOfRangeException(nameof(part), part, "未知部位。");
            return i;
        }

        private static NetFace[][] BuildAll()
        {
            var all = new NetFace[PartOrder.Length][];
            for (int i = 0; i < PartOrder.Length; i++)
                all[i] = Build(PartOrder[i]);
            return all;
        }

        /// <summary>
        /// 按面表**推导**盒子几何（不新增第三张坐标表）：
        /// 原点 = 该盒 6 个面 X1/Y1 的最小值；D = 右面宽、W = 前面宽、H = 前面高。
        /// 推导出的 6 个展开矩形必须与面表逐项相等 —— 测试里有一条专门的全等断言把两者钉死。
        /// </summary>
        private static NetFace[] Build(SkinPart part)
        {
            var boxes = BoxesOf(part);
            var list = new List<NetFace>(boxes.Length * 6);

            foreach (int box in boxes)
            {
                int b = box * 6;
                var front = SkinCanvas.FaceBounds(b + SlotFront);
                var right = SkinCanvas.FaceBounds(b + SlotRight);
                int w = front.X2 - front.X1;
                int h = front.Y2 - front.Y1;
                int d = right.X2 - right.X1;

                for (int slot = 0; slot < 6; slot++)
                {
                    int fi = b + slot;
                    var (nx, ny, nw, nh) = NetRect(slot, w, h, d);
                    list.Add(new NetFace(fi, nx, ny, nw, nh, SkinCanvas.LayerOfFace(fi)));
                }
            }

            return list.ToArray();
        }

        /// <summary>
        /// 某个槽位在展开图里的位置与尺寸（相对展开图原点）。
        /// 右/前/左/背 横排一行，顶在其上、底在其下 —— 就是纸模展开的样子。
        /// </summary>
        private static (int X, int Y, int W, int H) NetRect(int slot, int w, int h, int d) => slot switch
        {
            SlotTop => (d, 0, w, d),
            SlotBottom => (d + w, 0, w, d),
            SlotRight => (0, d, d, h),
            SlotFront => (d, d, w, h),
            SlotLeft => (d + w, d, d, h),
            SlotBack => (d + w + d, d, w, h),
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "槽位必须在 0..5 之间。"),
        };
    }
}
