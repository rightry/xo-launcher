using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace XO_Launcher.Services
{
    /// <summary>枚举本机已安装的字体家族，供设置页选择。</summary>
    public static class SystemFontCatalog
    {
        private const int DefaultCharset = 1;
        private const int LfFaceSize = 32;
        private static IReadOnlyList<string>? _cache;

        public static IReadOnlyList<string> Families()
        {
            if (_cache is not null) return _cache;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var logfont = new LogFont { lfCharSet = DefaultCharset };
                var hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    try
                    {
                        EnumFontExProc proc = (ref LogFont lf, IntPtr _, int __, IntPtr ___) =>
                        {
                            var name = lf.lfFaceName;
                            if (IsUiFamily(name))
                                names.Add(name);
                            return 1;
                        };
                        EnumFontFamiliesExW(hdc, ref logfont, proc, IntPtr.Zero, 0);
                        GC.KeepAlive(proc);
                    }
                    finally
                    {
                        ReleaseDC(IntPtr.Zero, hdc);
                    }
                }
            }
            catch (Exception)
            {
                // 枚举失败时仍提供推荐字体
            }

            if (names.Count == 0)
            {
                foreach (var fallback in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial" })
                    names.Add(fallback);
            }

            _cache = names.ToList();
            return _cache;
        }

        public static bool Contains(string family)
            => Families().Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

        private static bool IsUiFamily(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
                return false;
            ReadOnlySpan<string> skip = new[]
            {
                "MDL2", "Fluent Icons", "Webdings", "Wingdings", "Marlett",
                "HoloLens", "Emoji", "Symbol", "Icons", "MT Extra", "MS Outlook",
                "Bookshelf Symbol", "MS Reference Specialty", "Segoe Boot"
            };
            foreach (var token in skip)
            {
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private delegate int EnumFontExProc(ref LogFont lpelfe, IntPtr lpntme, int fontType, IntPtr lParam);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int EnumFontFamiliesExW(
            IntPtr hdc, ref LogFont lpLogfont, EnumFontExProc lpEnumFontFamExProc, IntPtr lParam, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LogFont
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LfFaceSize)]
            public string lfFaceName;
        }
    }

    public sealed class UiFontOption
    {
        public string Family { get; init; } = "";
        public string Label { get; init; } = "";
        public string Display =>
            string.IsNullOrWhiteSpace(Label) || string.Equals(Label, Family, StringComparison.Ordinal)
                ? Family
                : Label + "  ·  " + Family;
    }
}
