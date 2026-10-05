using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 亮暗主题与主题色。设置页可切换 dark / light / system，以及预设或自定义强调色。
    /// 强调色写入 ThemeDictionaries 中已有的 SolidColorBrush.Color，界面会立刻刷新。
    /// </summary>
    public static class ThemeService
    {
        public const string DefaultAccent = "#7CBD4B";
        public const string DefaultFontFamily = "Microsoft YaHei UI";
        public const string DefaultFontStack = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI Variable, Segoe UI, sans-serif";

        public static FontFamily AppFont { get; private set; } = new(DefaultFontStack);

        private static readonly (string Family, string Label)[] RecommendedFonts =
        {
            ("Microsoft YaHei UI", "微软雅黑 UI"),
            ("Microsoft YaHei", "微软雅黑"),
            ("Microsoft JhengHei UI", "微软正黑体 UI"),
            ("DengXian", "等线"),
            ("Segoe UI Variable", "Segoe UI Variable"),
            ("Segoe UI", "Segoe UI"),
            ("Source Han Sans SC", "思源黑体"),
            ("Noto Sans SC", "Noto Sans SC"),
            ("Cascadia Code", "Cascadia Code"),
            ("Arial", "Arial"),
            ("Calibri", "Calibri"),
            ("Times New Roman", "Times New Roman")
        };

        public static readonly (string Name, string Hex)[] AccentPresets =
        {
            ("草地绿", "#7CBD4B"),
            ("青绿", "#3D9B8F"),
            ("天空蓝", "#4A90D9"),
            ("靛蓝", "#5B6CDB"),
            ("紫色", "#8B6CCF"),
            ("玫红", "#D96B9A"),
            ("珊瑚", "#E05A5A"),
            ("琥珀", "#E08A3C")
        };

        private static FrameworkElement? _root;
        private static UISettings? _uiSettings;
        private static bool _hooked;

        public static bool IsLight => ResolveLight(LauncherSettings.Instance.AppTheme);

        public static ElementTheme Current => IsLight ? ElementTheme.Light : ElementTheme.Dark;

        public static void Apply(FrameworkElement? root)
        {
            if (root is null) return;
            _root = root;
            EnsureSystemThemeHook();
            root.RequestedTheme = Current;
            ApplyFont();
            ApplyAccent(root);
        }

        public static string ResolveFontStack(string? family)
        {
            var name = string.IsNullOrWhiteSpace(family) ? DefaultFontFamily : family.Trim();
            if (string.Equals(name, DefaultFontFamily, StringComparison.OrdinalIgnoreCase))
                return DefaultFontStack;
            return name + ", " + DefaultFontStack;
        }

        public static IReadOnlyList<UiFontOption> ListUiFonts()
        {
            var list = new List<UiFontOption>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (family, label) in RecommendedFonts)
            {
                if (!SystemFontCatalog.Contains(family) || !seen.Add(family)) continue;
                list.Add(new UiFontOption { Family = family, Label = label });
            }

            foreach (var family in SystemFontCatalog.Families())
            {
                if (!seen.Add(family)) continue;
                list.Add(new UiFontOption { Family = family, Label = "" });
            }

            if (list.Count == 0)
                list.Add(new UiFontOption { Family = DefaultFontFamily, Label = "微软雅黑 UI" });
            return list;
        }

        public static void ApplyFont()
        {
            try
            {
                var stack = ResolveFontStack(LauncherSettings.Instance.UiFont);
                var family = new FontFamily(stack);
                AppFont = family;
                SetFontResource("AppFontFamily", family);
                SetFontResource("ContentControlThemeFontFamily", family);

                if (_root is not null)
                {
                    var theme = Current;
                    _root.RequestedTheme = ElementTheme.Default;
                    _root.RequestedTheme = theme;
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Theme", "应用字体失败", ex);
            }
        }

        private static void SetFontResource(string key, FontFamily family)
        {
            // App.xaml 顶层和三个主题字典里都已经声明了 AppFontFamily /
            // ContentControlThemeFontFamily。对**已存在的键** set_Item 会抛
            // 0x800F0902；ContainsKey / Remove 同样会抛（见 WriteResource 说明）。
            //
            // 正确做法是「就地改属性」：拿到已解析的那个 FontFamily 做不到（FontFamily
            // 的 Source 只读），所以改走另一条路 —— 见 Apply() 里的
            // AppFont 赋值 + 顶层替换。这里的写入只在**键确实不存在**时才成功，
            // 失败完全正常，降级为日志即可，不要再往这里加探测。
            var app = Application.Current.Resources;
            WriteResource(app, key, family);
            foreach (var themeKey in new[] { "Dark", "Light", "Default" })
            {
                if (app.ThemeDictionaries[themeKey] is ResourceDictionary dict)
                    WriteResource(dict, key, family);
            }
        }

        /// <summary>
        /// 写资源键的唯一入口。**失败只记日志，绝不抛** —— 硬性要求，不是防御式编程。
        ///
        /// 背景（0x800F0902，Windows 语义是「组件存储里已有同名资源」）：
        /// WinUI 的 ResourceDictionary 是 WinRT 的 IMap&lt;Object,Object&gt;，**读路径同样不安全**——
        /// `ContainsKey` 映射到 IMap::HasKey，对 App.xaml 里已声明的键调用它就会抛 0x800F0902；
        /// 试图 `Remove` 再写也一样抛。实测（.NET Runtime 事件日志）连 HasKey 都能把进程
        /// 打成 AccessViolationException（0xc0000005，崩在 coreclr 里）。
        /// 结论：**已存在的键覆盖不得，也查不得**。
        ///
        /// 所以本方法只做「直接赋值」，由调用点自己判断要不要写：
        ///   · 我们自己新增的键（如 ContentDialogSmokeFill）→ 安全；
        ///   · App.xaml 已声明的键 → 会失败，降级为日志。
        ///
        /// 为什么必须吞住：全部调用点都在 MainWindow..ctor 的
        /// RefreshChrome → ApplyTheme 里，即**首帧渲染之前**。异常一旦漏出，会撞上
        /// XAML 生成器挂的 UnhandledException 处理器，被 Debugger.Break() 拦下，
        /// 表现为「启动即卡死」。而主题是纯外观：写失败最多某个控件没吃到自定义色，
        /// 绝不该让整个启动器起不来。
        /// </summary>
        private static void WriteResource(ResourceDictionary dict, string key, object value)
        {
            try
            {
                dict[key] = value;
            }
            catch (Exception ex)
            {
                _lastWriteFailure = key;
                LauncherLogService.Error("Theme", $"写入主题资源失败：{key}", ex);
            }
        }

        /// <summary>最近一次写失败的键，仅供诊断。</summary>
        private static string? _lastWriteFailure;

        public static T Get<T>(string key)
        {
            var name = IsLight ? "Light" : "Dark";
            if (Application.Current.Resources.ThemeDictionaries[name] is ResourceDictionary dict)
            {
                // 这里**绝不能**用 dict.ContainsKey(key) 去探测：WinRT 的 IMap::HasKey
                // 对 App.xaml 已声明的键会抛 0x800F0902（见 WriteResource 说明）。
                // 唯一安全的读法是「直接索引 + 兜底」，让「键不存在」走正常的异常路径。
                try
                {
                    if (dict[key] is T themed) return themed;
                }
                catch (Exception) { /* 该键不在当前主题字典里，回落到顶层 */ }
            }
            return (T)Application.Current.Resources[key];
        }

        public static Brush Brush(string key) => Get<Brush>(key);

        // ==================== 全景图背景样式 ====================

        private static readonly Dictionary<(string Theme, string Key), Color> _panoramaOriginals = new();
        private static bool _panoramaStyleOn;
        // -1 = 还没测出背景明暗（跟随主题）；0 = 背景偏暗；1 = 背景偏亮
        private static sbyte _panoramaTone = -1;

        /// <summary>
        /// 测到全景环带的明暗后调用：让卡片底色与文字一起跟着背景走，
        /// 而不是跟着用户选的亮暗主题走 —— 亮背景上配暗主题就是白字糊在白底上。
        /// </summary>
        public static void SetPanoramaTone(bool brightBackground)
        {
            var tone = (sbyte)(brightBackground ? 1 : 0);
            if (_panoramaTone == tone) return;
            _panoramaTone = tone;
            if (!_panoramaStyleOn) return;
            // 先还原再按新色调重写：_panoramaOriginals 里存的是各键的原始颜色
            ApplyPanoramaStyle(false);
            ApplyPanoramaStyle(true);
        }

        /// <summary>
        /// 全景图背景模式：页面底色改为全透明（露出 RootGrid 下的全景层），
        /// 卡片/凹陷底色改为半透明，配合全景层上的 Acrylic 模糊形成毛玻璃质感。
        /// 只能就地改 SolidColorBrush.Color（带 alpha），不能替换资源实例（见 WriteResource 说明）。
        /// </summary>
        public static void ApplyPanoramaStyle(bool on)
        {
            if (on == _panoramaStyleOn) return;
            _panoramaStyleOn = on;
            var app = Application.Current.Resources;
            foreach (var themeKey in new[] { "Dark", "Light", "Default" })
            {
                if (app.ThemeDictionaries[themeKey] is ResourceDictionary dict)
                    WritePanoramaColors(dict, themeKey, on);
            }
            WritePanoramaColors(app, "Top", on);
            RefreshCardFrostTint();
        }

        /// <summary>全景模式下由背景明暗定出的色调（true = 亮底）；没开全景或还没测出来时为 null，跟随主题。</summary>
        public static bool? PanoramaToneBright => _panoramaStyleOn && _panoramaTone >= 0
            ? _panoramaTone == 1
            : (bool?)null;

        private static double _cardFrostTint = 0.35;

        // 着色亮度浓度：WinUI 除了 TintOpacity 还有一层 TintLuminosityOpacity，
        // 它停在 1 时会把着色顶回实心，只调 TintOpacity 卡片照样不透。
        private const double CardFrostLuminosity = 0.5;

        /// <summary>卡片亚克力的浓度（0 最透 – 1 实心）。设置页那根滑杆现在调的就是它。</summary>
        public static void SetCardFrostTint(double tint)
        {
            _cardFrostTint = Math.Clamp(tint, 0, 1);
            RefreshCardFrostTint();
        }

        /// <summary>
        /// 把浓度写进当前主题的 AcrylicBrush。主题切换后要再调一次（每个主题字典是不同实例）。
        /// 关闭全景时两个值都强制 1（实心）：普通模式下身后没有全景可糊，透出底色只会让卡片发灰。
        /// </summary>
        public static void RefreshCardFrostTint()
        {
            try
            {
                if (Get<Brush>("CardAcrylicBrush") is not AcrylicBrush brush) return;
                brush.TintOpacity = (float)(_panoramaStyleOn ? _cardFrostTint : 1.0);
                brush.TintLuminosityOpacity = (float)(_panoramaStyleOn ? CardFrostLuminosity : 1.0);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Theme", "写入卡片亚克力浓度失败", ex);
            }
        }

        private static void WritePanoramaColors(ResourceDictionary dict, string themeKey, bool on)
        {
            var light = themeKey is "Light" || (themeKey == "Top" && IsLight);
            // 背景明暗测出来之后，亮/暗底以背景为准（主题只决定没开全景时的观感）
            if (on && _panoramaTone >= 0) light = _panoramaTone == 1;

            SetPanoramaColor(dict, themeKey, "AppBackgroundBrush", on, light ? 0x00F3F3F3u : 0x000E0E0Eu);
            SetPanoramaColor(dict, themeKey, "CardBackgroundBrush", on, light ? 0xD9FFFFFFu : 0xD9151515u);
            SetPanoramaColor(dict, themeKey, "RecessedBackgroundBrush", on, light ? 0x14000000u : 0x59000000u);

            // 文字跟着底色走：亮底配深色字、暗底配浅色字，否则暗主题 + 雪景全景根本读不出。
            SetPanoramaColor(dict, themeKey, "TextPrimaryBrush", on, light ? 0xFF1A1A1Au : 0xFFF2F2F2u);
            SetPanoramaColor(dict, themeKey, "TextSecondaryBrush", on, light ? 0xB31A1A1Au : 0xB3F2F2F2u);
            SetPanoramaColor(dict, themeKey, "TextTertiaryBrush", on, light ? 0x8C1A1A1Au : 0x8CF2F2F2u);
            SetPanoramaColor(dict, themeKey, "DividerBrush", on, light ? 0x33000000u : 0x33FFFFFFu);

            // 输入框（自绘 TextInput / SearchBox / SelectBox 的外壳都走 TextControlBackground）。
            // 全景下这些键会被写进主题字典：值一律用「半透明黑/白叠加」而不是固定深灰，
            // 这样输入框只是比它所在的那层表面深一档，不会糊成一块与卡片脱节的实心板（色差）。
            // PointerOver / Focused / Disabled 置透明：状态反馈由自绘外壳的描边负责，
            // WinUI 模板里的 BorderElement 再涂一层就会在框内叠出第二条更深的色带。
            SetPanoramaColor(dict, themeKey, "TextControlBackground", on, light ? 0x8CFFFFFFu : 0x40000000u);
            SetPanoramaColor(dict, themeKey, "TextControlBackgroundPointerOver", on, 0x00000000u);
            SetPanoramaColor(dict, themeKey, "TextControlBackgroundFocused", on, 0x00000000u);
            SetPanoramaColor(dict, themeKey, "TextControlBackgroundDisabled", on, 0x00000000u);
            // 自制下拉（SelectBox）的面板与列表项：面板要几乎实心才压得住身后的全景，
            // 列表项两档则继续用「叠一层半透明」的写法，跟卡片那层的色差保持一致。
            SetPanoramaColor(dict, themeKey, "DropDownBackgroundBrush", on, light ? 0xF2FFFFFFu : 0xF01F1F1Fu);
            SetPanoramaColor(dict, themeKey, "DropDownBorderBrush", on, light ? 0x33000000u : 0x4DFFFFFFu);
            SetPanoramaColor(dict, themeKey, "DropDownItemBackgroundPointerOver", on, light ? 0x14000000u : 0x1FFFFFFFu);
            SetPanoramaColor(dict, themeKey, "DropDownItemBackgroundSelected", on, light ? 0x1F000000u : 0x33FFFFFFu);
        }

        private static void SetPanoramaColor(ResourceDictionary dict, string themeKey, string brushKey, bool on, uint argb)
        {
            try
            {
                if (dict[brushKey] is not SolidColorBrush brush) return;
                var id = (themeKey, brushKey);
                if (!_panoramaOriginals.TryGetValue(id, out var original))
                {
                    original = brush.Color;
                    _panoramaOriginals[id] = original;
                }
                brush.Color = on
                    ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
                    : original;
            }
            catch (Exception)
            {
                // 键不存在或写失败：外观降级，不影响运行
            }
        }

        /// <summary>
        /// 图标字体（Fluent UI System Icons Filled）。XAML 侧等价写法是
        /// {StaticResource IconFontFamily}。代码里新建 FontIcon 时必须显式带上，
        /// 否则会回落到 WinUI 默认的 Segoe Fluent Icons（无实心字形）。
        /// </summary>
        public static FontFamily IconFontFamily => Get<FontFamily>("IconFontFamily");

        public static void ApplyAccent(FrameworkElement? windowRoot = null)
        {
            try
            {
                windowRoot ??= _root;
                var palette = AccentPalette.FromHex(LauncherSettings.Instance.AccentColor);

                var app = Application.Current.Resources;
                foreach (var themeKey in new[] { "Dark", "Light", "Default" })
                {
                    if (app.ThemeDictionaries[themeKey] is ResourceDictionary dict)
                        palette.Write(dict, light: themeKey == "Light");
                }
                palette.Write(app, light: IsLight);

                if (windowRoot?.Resources.ThemeDictionaries is { } local)
                {
                    if (local["Dark"] is ResourceDictionary dark) palette.Write(dark, light: false);
                    if (local["Light"] is ResourceDictionary light) palette.Write(light, light: true);
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Theme", "应用主题色失败", ex);
            }
        }

        private static bool ResolveLight(string? theme)
        {
            if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)) return false;
            return IsSystemLight();
        }

        private static bool IsSystemLight()
        {
            try
            {
                _uiSettings ??= new UISettings();
                var background = _uiSettings.GetColorValue(UIColorType.Background);
                return background.R + background.G + background.B > 384;
            }
            catch (Exception)
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Light;
            }
        }

        private static void EnsureSystemThemeHook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                _uiSettings ??= new UISettings();
                _uiSettings.ColorValuesChanged += (_, _) => _root?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.Equals(LauncherSettings.Instance.AppTheme, "system", StringComparison.OrdinalIgnoreCase))
                        return;
                    if (_root is not null)
                    {
                        _root.RequestedTheme = Current;
                        ApplyAccent(_root);
                    }
                });
            }
            catch (Exception)
            {
                // 系统主题监听不可用时忽略
            }
        }

        private readonly struct AccentPalette
        {
            public required Color Base { get; init; }
            public required Color Hover { get; init; }
            public required Color Pressed { get; init; }
            public required Color Dark { get; init; }
            public required Color OnAccent { get; init; }
            public required Color Disabled { get; init; }
            public required Color Selection { get; init; }
            public required Color Soft { get; init; }

            public static AccentPalette FromHex(string? hex)
            {
                var color = Parse(hex) ?? Parse(DefaultAccent)!.Value;
                var luma = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
                return new AccentPalette
                {
                    Base = color,
                    Hover = Mix(color, Colors.White, 0.18f),
                    Pressed = Mix(color, Colors.Black, 0.18f),
                    Dark = Mix(color, Colors.Black, 0.28f),
                    OnAccent = luma > 168
                        ? Color.FromArgb(255, 15, 31, 10)
                        : Color.FromArgb(255, 255, 255, 255),
                    Disabled = WithAlpha(color, 0x4D),
                    Selection = WithAlpha(color, 0x66),
                    Soft = Mix(color, Colors.White, 0.32f)
                };
            }

            public void Write(ResourceDictionary dict, bool light)
            {
                var text = light ? Dark : Base;
                var textHover = light ? Mix(Dark, Colors.White, 0.12f) : Hover;
                var textPressed = light ? Mix(Dark, Colors.Black, 0.12f) : Pressed;
                var onPressed = Mix(OnAccent, Colors.Black, 0.12f);

                Set(dict, "AccentBrush", Base);
                Set(dict, "AccentDarkBrush", Dark);
                Set(dict, "AccentOnBrush", OnAccent);

                // 柔和强调底色（选中项 / 图标底衬）：浅色主题混白、深色主题压暗；
                // 前景在浅色主题下用深一档的 Dark 保证对比度。
                Set(dict, "AccentSoftBrush", light ? Mix(Base, Colors.White, 0.82f) : Mix(Base, Colors.Black, 0.78f));
                Set(dict, "AccentSoftForegroundBrush", light ? Dark : Base);

                Set(dict, "ToggleSwitchFillOn", Base);
                Set(dict, "ToggleSwitchFillOnPointerOver", Hover);
                Set(dict, "ToggleSwitchFillOnPressed", Pressed);
                Set(dict, "ToggleSwitchStrokeOn", Base);
                Set(dict, "ToggleSwitchStrokeOnPointerOver", Hover);
                Set(dict, "ToggleSwitchStrokeOnPressed", Pressed);

                Set(dict, "AccentFillColorDefaultBrush", Base);
                Set(dict, "AccentFillColorSecondaryBrush", Hover);
                Set(dict, "AccentFillColorTertiaryBrush", Pressed);
                Set(dict, "AccentFillColorDisabledBrush", Disabled);

                Set(dict, "AccentTextFillColorPrimaryBrush", text);
                Set(dict, "AccentTextFillColorSecondaryBrush", textHover);
                Set(dict, "AccentTextFillColorTertiaryBrush", Soft);
                Set(dict, "AccentTextFillColorDisabledBrush", Disabled);

                Set(dict, "SystemControlForegroundAccentBrush", text);
                Set(dict, "SystemControlBackgroundAccentBrush", Base);
                Set(dict, "SystemControlHighlightAccentBrush", Base);
                Set(dict, "SystemControlHyperlinkTextBrush", text);
                Set(dict, "TextControlSelectionHighlightColor", Selection);

                Set(dict, "HyperlinkButtonForeground", text);
                Set(dict, "HyperlinkButtonForegroundPointerOver", textHover);
                Set(dict, "HyperlinkButtonForegroundPressed", textPressed);

                Set(dict, "AccentButtonBackground", Base);
                Set(dict, "AccentButtonBackgroundPointerOver", Hover);
                Set(dict, "AccentButtonBackgroundPressed", Pressed);
                Set(dict, "AccentButtonBackgroundDisabled", Disabled);
                Set(dict, "AccentButtonBorderBrush", Base);
                Set(dict, "AccentButtonBorderBrushPointerOver", Hover);
                Set(dict, "AccentButtonBorderBrushPressed", Pressed);
                Set(dict, "AccentButtonBorderBrushDisabled", Disabled);
                Set(dict, "AccentButtonForeground", OnAccent);
                Set(dict, "AccentButtonForegroundPointerOver", OnAccent);
                Set(dict, "AccentButtonForegroundPressed", onPressed);
                Set(dict, "AccentButtonForegroundDisabled", WithAlpha(OnAccent, 0x73));
            }

            /// <summary>
            /// 强调色的写入口。**先尝试就地改色，改不动才新建** —— 顺序不能反。
            ///
            /// 直接 `dict[key] = new SolidColorBrush(...)` 覆盖已存在的键会抛
            /// 0x800F0902（见 ThemeService.WriteResource 的完整说明），
            /// 所以正确姿势是：拿到**已解析的那个 SolidColorBrush 实例**改它的 Color。
            /// 颜色是普通属性，改属性不触发资源键的重复判定，界面会自动刷新 ——
            /// 这也正是 App.xaml 里强调色「只放占位、启动后改写 Color」的设计前提。
            ///
            /// 探测存在性**不能**用 ContainsKey（IMap::HasKey 同样抛 0x800F0902），
            /// 只能用「直接索引 + 兜底」。
            /// </summary>
            private static void Set(ResourceDictionary dict, string key, Color color)
            {
                try
                {
                    if (dict[key] is SolidColorBrush existing)
                    {
                        existing.Color = color;
                        return;
                    }
                }
                catch (Exception)
                {
                    // 键不存在 → 落到下面新建。这是正常路径，不是错误。
                }

                try
                {
                    dict[key] = new SolidColorBrush(color);
                }
                catch (Exception ex)
                {
                    LauncherLogService.Error("Theme", $"写入强调色失败：{key}", ex);
                }
            }

            private static Color Mix(Color a, Color b, float t)
            {
                t = Math.Clamp(t, 0, 1);
                return Color.FromArgb(255,
                    (byte)(a.R + (b.R - a.R) * t),
                    (byte)(a.G + (b.G - a.G) * t),
                    (byte)(a.B + (b.B - a.B) * t));
            }

            private static Color WithAlpha(Color color, byte alpha)
                => Color.FromArgb(alpha, color.R, color.G, color.B);

            public static Color? Parse(string? hex)
            {
                if (string.IsNullOrWhiteSpace(hex)) return null;
                hex = hex.Trim().TrimStart('#');
                if (hex.Length == 8) hex = hex[2..];
                if (hex.Length != 6) return null;
                try
                {
                    return Color.FromArgb(255,
                        Convert.ToByte(hex[..2], 16),
                        Convert.ToByte(hex[2..4], 16),
                        Convert.ToByte(hex[4..6], 16));
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public static string ToHex(Color color)
                => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        public static Color ParseAccent(string? hex)
            => AccentPalette.Parse(hex) ?? AccentPalette.Parse(DefaultAccent)!.Value;

        public static string FormatAccent(Color color) => AccentPalette.ToHex(color);

        public static bool IsPreset(string hex)
        {
            foreach (var (_, preset) in AccentPresets)
            {
                if (string.Equals(preset, hex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
