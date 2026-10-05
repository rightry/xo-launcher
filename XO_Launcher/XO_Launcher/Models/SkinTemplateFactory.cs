using System;

namespace XO_Launcher.Models
{
    /// <summary>
    /// 内置皮肤模板生成器：用代码「画」出一张开箱即用的完整人形皮肤（六个面全部填满），
    /// 让新手从一张看得懂的皮肤起步，而不是对着全透明的画布发呆。
    ///
    /// 关键性质 —— 所有面矩形一律取自 <see cref="SkinCanvas.FaceBounds"/>，代码里**不手写任何 UV 坐标**，
    /// 因此生成的皮肤必然 UV 正确。这一点很重要：仓库里原先那两张手工贴图
    /// (<c>Assets/skin-default/steve.png</c> / <c>alex.png</c>) 实测「左臂整条 0/256 像素、两条腿基本是空的」，
    /// 新手点进「左臂」只会看到一块空白，正是「画了没反应」的来源。
    ///
    /// 纯逻辑，不依赖任何 WinUI 类型，便于在普通控制台程序里做单元测试。
    /// 输出为 BGRA8、直通 alpha（非预乘），未填的覆盖层保持全透明。
    /// </summary>
    public static class SkinTemplateFactory
    {
        /// <summary>与 <see cref="SkinCanvas"/> 的 Faces 表一致的槽位顺序。</summary>
        private const int SlotTop = 0;
        private const int SlotBottom = 1;
        private const int SlotRight = 2;
        private const int SlotFront = 3;
        private const int SlotLeft = 4;
        private const int SlotBack = 5;

        /// <summary>Faces 表里每盒的盒号：0 头 / 1 右腿 / 2 躯干 / 3 右臂 / 4 左腿 / 5 左臂（后 6 盒为覆盖层）。</summary>
        private const int BoxHead = 0;
        private const int BoxRightLeg = 1;
        private const int BoxBody = 2;
        private const int BoxRightArm = 3;
        private const int BoxLeftLeg = 4;
        private const int BoxLeftArm = 5;

        /// <summary>眉毛、眼白、嘴等五官用的固定色（两张模板共用）。</summary>
        private static readonly (byte R, byte G, byte B) EyeWhite = (0xFA, 0xFA, 0xFA);

        /// <summary>一张模板的配色。</summary>
        private readonly struct Palette
        {
            public Palette(
                (byte R, byte G, byte B) hair,
                (byte R, byte G, byte B) skin,
                (byte R, byte G, byte B) skinShade,
                (byte R, byte G, byte B) iris,
                (byte R, byte G, byte B) mouth,
                (byte R, byte G, byte B) shirt,
                (byte R, byte G, byte B) belt,
                (byte R, byte G, byte B) pants,
                (byte R, byte G, byte B) shoe)
            {
                Hair = hair;
                Skin = skin;
                SkinShade = skinShade;
                Iris = iris;
                Mouth = mouth;
                Shirt = shirt;
                Belt = belt;
                Pants = pants;
                Shoe = shoe;
            }

            public (byte R, byte G, byte B) Hair { get; }
            public (byte R, byte G, byte B) Skin { get; }
            public (byte R, byte G, byte B) SkinShade { get; }
            public (byte R, byte G, byte B) Iris { get; }
            public (byte R, byte G, byte B) Mouth { get; }
            public (byte R, byte G, byte B) Shirt { get; }
            public (byte R, byte G, byte B) Belt { get; }
            public (byte R, byte G, byte B) Pants { get; }
            public (byte R, byte G, byte B) Shoe { get; }
        }

        /// <summary>经典（Steve）：棕色头发 + 青绿上衣 + 靛蓝长裤。</summary>
        private static readonly Palette StevePalette = new(
            hair: (0x3F, 0x2A, 0x1B),
            skin: (0xF1, 0xC4, 0x9C),
            skinShade: (0xD8, 0x9B, 0x6B),
            iris: (0x2E, 0x62, 0xA8),
            mouth: (0x8B, 0x5A, 0x3C),
            shirt: (0x2E, 0x9E, 0xA8),
            belt: (0x1B, 0x3D, 0x6B),
            pants: (0x3B, 0x4A, 0x8C),
            shoe: (0x4A, 0x4A, 0x4A));

        /// <summary>纤细（Alex）：橙红头发 + 草绿上衣 + 棕色长裤，便于和 Steve 一眼区分。</summary>
        private static readonly Palette AlexPalette = new(
            hair: (0xC8, 0x6A, 0x2B),
            skin: (0xF6, 0xD3, 0xAE),
            skinShade: (0xDE, 0xA9, 0x7E),
            iris: (0x2F, 0x6B, 0x3F),
            mouth: (0x9B, 0x5E, 0x3E),
            shirt: (0x6E, 0xA8, 0x3F),
            belt: (0x4A, 0x33, 0x1F),
            pants: (0x6B, 0x4A, 0x2E),
            shoe: (0x3A, 0x3A, 0x3A));

        /// <summary>
        /// 生成一张完整皮肤（BGRA8，直通 alpha）。
        /// </summary>
        /// <param name="slim">true = 纤细模型配色（Alex），false = 经典模型配色（Steve）。
        /// 注意：两者的贴图布局完全相同，纤细/经典只影响游戏里的模型宽度，因此这里只换配色以便区分。</param>
        public static byte[] Create(bool slim)
        {
            var px = new byte[SkinCanvas.ByteCount];
            var p = slim ? AlexPalette : StevePalette;

            PaintHead(px, p);
            PaintBody(px, p);
            PaintArm(px, BoxRightArm, p);
            PaintArm(px, BoxLeftArm, p);
            PaintLeg(px, BoxRightLeg, p);
            PaintLeg(px, BoxLeftLeg, p);

            // 覆盖层（帽 6 / 夹克 7 / 四个覆盖 8..11）全部保持透明：
            // 那是留给用户自己加帽子、外套的地方，塞满反而挡住了「覆盖层」这个概念。
            return px;
        }

        /// <summary>头部：顶/侧/背全是头发，只有正面是脸；正脸再点出刘海、眉、眼、鼻、嘴。</summary>
        private static void PaintHead(byte[] px, Palette p)
        {
            FillFace(px, BoxHead, SlotTop, p.Hair);
            FillFace(px, BoxHead, SlotBottom, p.Skin);      // 下巴底面 = 脖子肤色
            FillFace(px, BoxHead, SlotRight, p.Hair);
            FillFace(px, BoxHead, SlotLeft, p.Hair);
            FillFace(px, BoxHead, SlotBack, p.Hair);
            FillFace(px, BoxHead, SlotFront, p.Skin);

            var f = SkinCanvas.FaceBounds(BoxHead * 6 + SlotFront);   // 8×8 正脸

            // 刘海压住最上一行，两侧鬓角压住两行 —— 这样侧面看过去是「头发包住脸」，不会露出光边。
            FillRect(px, f.X1, f.Y1, f.X2, f.Y1 + 1, p.Hair);
            FillRect(px, f.X1, f.Y1, f.X1 + 1, f.Y1 + 2, p.Hair);
            FillRect(px, f.X2 - 1, f.Y1, f.X2, f.Y1 + 2, p.Hair);

            // 眉（左右各 2 px）
            FillRect(px, f.X1 + 2, f.Y1 + 3, f.X1 + 4, f.Y1 + 4, p.Hair);
            FillRect(px, f.X1 + 5, f.Y1 + 3, f.X1 + 7, f.Y1 + 4, p.Hair);

            // 眼：外侧眼白 + 内侧瞳孔，左右对称
            FillRect(px, f.X1 + 2, f.Y1 + 4, f.X1 + 4, f.Y1 + 5, EyeWhite);
            FillRect(px, f.X1 + 5, f.Y1 + 4, f.X1 + 7, f.Y1 + 5, EyeWhite);
            FillRect(px, f.X1 + 3, f.Y1 + 4, f.X1 + 4, f.Y1 + 5, p.Iris);
            FillRect(px, f.X1 + 5, f.Y1 + 4, f.X1 + 6, f.Y1 + 5, p.Iris);

            // 鼻 1 px + 嘴 3 px
            FillRect(px, f.X1 + 4, f.Y1 + 5, f.X1 + 5, f.Y1 + 6, p.SkinShade);
            FillRect(px, f.X1 + 3, f.Y1 + 6, f.X1 + 6, f.Y1 + 7, p.Mouth);
        }

        /// <summary>躯干：六面全上衣，四面底部两行压一条腰带，让上身和裤子有分界。</summary>
        private static void PaintBody(byte[] px, Palette p)
        {
            FillBox(px, BoxBody, p.Shirt);
            foreach (int slot in new[] { SlotRight, SlotFront, SlotLeft, SlotBack })
            {
                var f = SkinCanvas.FaceBounds(BoxBody * 6 + slot);
                FillRect(px, f.X1, f.Y2 - 2, f.X2, f.Y2, p.Belt);
            }
        }

        /// <summary>手臂：上 8 行袖子、下 4 行露出皮肤（短袖效果），底面是手掌肤色。</summary>
        private static void PaintArm(byte[] px, int box, Palette p)
        {
            FillBox(px, box, p.Shirt);
            foreach (int slot in new[] { SlotRight, SlotFront, SlotLeft, SlotBack })
            {
                var f = SkinCanvas.FaceBounds(box * 6 + slot);
                FillRect(px, f.X1, f.Y2 - 4, f.X2, f.Y2, p.Skin);
            }
            FillFace(px, box, SlotBottom, p.Skin);
        }

        /// <summary>腿：上 9 行裤子、下 3 行是鞋，底面也是鞋。</summary>
        private static void PaintLeg(byte[] px, int box, Palette p)
        {
            FillBox(px, box, p.Pants);
            foreach (int slot in new[] { SlotRight, SlotFront, SlotLeft, SlotBack })
            {
                var f = SkinCanvas.FaceBounds(box * 6 + slot);
                FillRect(px, f.X1, f.Y2 - 3, f.X2, f.Y2, p.Shoe);
            }
            FillFace(px, box, SlotBottom, p.Shoe);
        }

        private static void FillBox(byte[] px, int box, (byte R, byte G, byte B) color)
        {
            for (int slot = 0; slot < 6; slot++)
                FillFace(px, box, slot, color);
        }

        private static void FillFace(byte[] px, int box, int slot, (byte R, byte G, byte B) color)
        {
            var f = SkinCanvas.FaceBounds(box * 6 + slot);
            FillRect(px, f.X1, f.Y1, f.X2, f.Y2, color);
        }

        /// <summary>填充贴图上的矩形（x2/y2 为排除值），越界部分自动裁掉。</summary>
        private static void FillRect(byte[] px, int x1, int y1, int x2, int y2, (byte R, byte G, byte B) color)
        {
            for (int y = Math.Max(0, y1); y < Math.Min(y2, SkinCanvas.Size); y++)
            {
                for (int x = Math.Max(0, x1); x < Math.Min(x2, SkinCanvas.Size); x++)
                {
                    int i = (y * SkinCanvas.Size + x) * 4;
                    px[i] = color.B;
                    px[i + 1] = color.G;
                    px[i + 2] = color.R;
                    px[i + 3] = 0xFF;
                }
            }
        }
    }
}
