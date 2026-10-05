using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 自定义颜色弹窗的本体：一个完全自绘的 HSV 取色器。
    /// 顶部 预览块 + HEX 输入 + 取色笔；中间 SV 光谱面板（拖拽选饱和度/明度）；
    /// 下方 色相条（拖拽选色相）与预设色板。
    ///
    /// 不再使用 WinUI 的 <c>ColorPicker</c>：那套控件自带无法主题化的系统外观，
    /// 与本应用「一切自绘」的风格（AppDialog / SearchBox / TextInput）不搭。
    /// 对外接口保持 <c>new ColorPickBody(initial)</c> + <see cref="Color"/> 不变。
    /// </summary>
    public sealed class ColorPickBody : StackPanel
    {
        private static readonly string[] Presets =
        {
            "#7CBD4B", "#58B09C", "#3E8ED0", "#7A5AF8",
            "#D6409F", "#E5484D", "#F76B15", "#8A8A8A"
        };

        private const double SvHeight = 200;
        private const double HueHeight = 18;
        private const double ThumbR = 9;      // SV 滑块半径
        private const double HueThumbW = 12;  // 色相滑块宽

        private readonly TextInput _hexBox;
        private readonly Border _preview;

        private readonly Border _svPanel;
        private readonly Rectangle _svHue;    // 纯色相底层（随 H 变）
        private readonly Border _svThumb;

        private readonly Border _hueBar;
        private readonly Border _hueThumb;

        private double _h;             // 0..360
        private double _s = 1;         // 0..1
        private double _v = 1;         // 0..1
        private bool _syncing;
        private bool _svDrag;
        private bool _hueDrag;

        public Color Color => HsvToRgb(_h, _s, _v);

        public ColorPickBody(Color initial)
        {
            Spacing = 14;
            Width = 340;

            // ---------- 顶行：预览 + HEX + 取色笔 ----------
            var topRow = new Grid { ColumnSpacing = 10 };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _preview = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                BorderBrush = ThemeService.Brush("DividerBrush"),
                BorderThickness = new Thickness(1)
            };

            _hexBox = new TextInput { VerticalAlignment = VerticalAlignment.Center };
            _hexBox.TextChanged += HexBox_TextChanged;

            var eyedropper = new Button
            {
                Height = 40,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = "取色", FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
            };
            ToolTipService.SetToolTip(eyedropper, "从屏幕任意位置吸取颜色");
            eyedropper.Click += async (_, _) =>
            {
                var picked = await EyedropperWindow.PickAsync();
                if (picked is { } c) SetColor(c);
            };

            topRow.Children.Add(_preview);
            Grid.SetColumn(_hexBox, 1);
            topRow.Children.Add(_hexBox);
            Grid.SetColumn(eyedropper, 2);
            topRow.Children.Add(eyedropper);
            Children.Add(topRow);

            // ---------- SV 光谱面板 ----------
            _svHue = FilledRect(8);
            var satOverlay = GradientRect(8, true,
                new GradientStop { Color = Color.FromArgb(255, 255, 255, 255), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 });
            var valOverlay = GradientRect(8, false,
                new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(255, 0, 0, 0), Offset = 1 });

            _svThumb = new Border
            {
                Width = ThumbR * 2,
                Height = ThumbR * 2,
                CornerRadius = new CornerRadius(ThumbR),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };

            var svGrid = new Grid { Children = { _svHue, satOverlay, valOverlay, _svThumb } };
            _svPanel = new Border
            {
                Height = SvHeight,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), // 透明但可命中
                Child = svGrid
            };
            WireDrag(_svPanel, isSv: true);
            _svPanel.SizeChanged += (_, _) => LayoutThumbs();
            Children.Add(_svPanel);

            // ---------- 色相条 ----------
            var rainbow = GradientRect(HueHeight / 2, true,
                new GradientStop { Color = Color.FromArgb(255, 255, 0, 0), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(255, 255, 255, 0), Offset = 1.0 / 6 },
                new GradientStop { Color = Color.FromArgb(255, 0, 255, 0), Offset = 2.0 / 6 },
                new GradientStop { Color = Color.FromArgb(255, 0, 255, 255), Offset = 3.0 / 6 },
                new GradientStop { Color = Color.FromArgb(255, 0, 0, 255), Offset = 4.0 / 6 },
                new GradientStop { Color = Color.FromArgb(255, 255, 0, 255), Offset = 5.0 / 6 },
                new GradientStop { Color = Color.FromArgb(255, 255, 0, 0), Offset = 1 });

            _hueThumb = new Border
            {
                Width = HueThumbW,
                Height = HueHeight + 6,
                CornerRadius = new CornerRadius(HueThumbW / 2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _hueBar = new Border
            {
                Height = HueHeight,
                CornerRadius = new CornerRadius(HueHeight / 2),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Child = new Grid { Children = { rainbow, _hueThumb } }
            };
            WireDrag(_hueBar, isSv: false);
            _hueBar.SizeChanged += (_, _) => LayoutThumbs();
            Children.Add(_hueBar);

            // ---------- 预设色板 ----------
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var hex in Presets)
            {
                if (!TryParseHex(hex, out var preset)) continue;
                var swatch = new Button
                {
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(preset)
                };
                ToolTipService.SetToolTip(swatch, hex);
                swatch.Click += (_, _) => SetColor(preset);
                swatches.Children.Add(swatch);
            }
            Children.Add(swatches);

            SetColor(initial);
        }

        // ==================== 交互 ====================

        private void WireDrag(Border host, bool isSv)
        {
            host.PointerPressed += (_, e) =>
            {
                if (isSv) _svDrag = true; else _hueDrag = true;
                host.CapturePointer(e.Pointer);
                ApplyPointer(host, isSv, e);
                e.Handled = true;
            };
            host.PointerMoved += (_, e) =>
            {
                if (isSv ? _svDrag : _hueDrag) ApplyPointer(host, isSv, e);
            };
            host.PointerReleased += (_, e) =>
            {
                if (isSv) _svDrag = false; else _hueDrag = false;
                host.ReleasePointerCapture(e.Pointer);
            };
            host.PointerCanceled += (_, _) =>
            {
                if (isSv) _svDrag = false; else _hueDrag = false;
            };
        }

        private void ApplyPointer(Border host, bool isSv, PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(host).Position;
            if (isSv)
            {
                double w = host.ActualWidth, h = host.ActualHeight;
                if (w <= 0 || h <= 0) return;
                _s = Math.Clamp(p.X / w, 0, 1);
                _v = Math.Clamp(1 - p.Y / h, 0, 1);
            }
            else
            {
                double w = host.ActualWidth;
                if (w <= 0) return;
                _h = Math.Clamp(p.X / w, 0, 1) * 360;
            }
            UpdateVisuals();
            UpdateHex();
        }

        private void SetColor(Color c)
        {
            var (h, s, v) = RgbToHsv(c);
            if (s > 0.0001) _h = h;   // 灰阶不改变已选色相，便于之后拖回彩色
            _s = s;
            _v = v;
            UpdateVisuals();
            UpdateHex();
        }

        private void UpdateVisuals()
        {
            _svHue.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            var cur = HsvToRgb(_h, _s, _v);
            _preview.Background = new SolidColorBrush(cur);
            _svThumb.Background = new SolidColorBrush(cur);
            _hueThumb.Background = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            LayoutThumbs();
        }

        private void LayoutThumbs()
        {
            double w = _svPanel.ActualWidth, h = _svPanel.ActualHeight;
            if (w > 0 && h > 0)
            {
                double x = Math.Clamp(_s * w, 0, w);
                double y = Math.Clamp((1 - _v) * h, 0, h);
                _svThumb.Margin = new Thickness(x - ThumbR, y - ThumbR, 0, 0);
            }
            double hw = _hueBar.ActualWidth;
            if (hw > 0)
            {
                double hx = Math.Clamp(_h / 360 * hw, 0, hw);
                _hueThumb.Margin = new Thickness(hx - HueThumbW / 2, 0, 0, 0);
            }
        }

        private void UpdateHex()
        {
            var hex = ToHex(Color);
            if (string.Equals(_hexBox.Text, hex, StringComparison.OrdinalIgnoreCase)) return;
            _syncing = true;
            try { _hexBox.Text = hex; }
            finally { _syncing = false; }
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncing) return;
            if (!TryParseHex(_hexBox.Text, out var c)) return;
            SetColor(c);
        }

        // ==================== 绘制辅助 ====================

        private static Rectangle FilledRect(double radius) => new()
        {
            RadiusX = radius,
            RadiusY = radius,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };

        private static Rectangle GradientRect(double radius, bool horizontal, params GradientStop[] stops)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = horizontal ? new Windows.Foundation.Point(1, 0) : new Windows.Foundation.Point(0, 1)
            };
            foreach (var s in stops) brush.GradientStops.Add(s);
            return new Rectangle
            {
                RadiusX = radius,
                RadiusY = radius,
                Fill = brush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
        }

        // ==================== 颜色换算 ====================

        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb(255,
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        private static (double h, double s, double v) RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            double h = 0;
            if (d > 1e-9)
            {
                if (Math.Abs(max - r) < 1e-9) h = 60 * ((g - b) / d % 6);
                else if (Math.Abs(max - g) < 1e-9) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            if (h < 0) h += 360;
            double s = max <= 1e-9 ? 0 : d / max;
            return (h, s, max);
        }

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        internal static bool TryParseHex(string? text, out Color color)
        {
            color = default;
            var s = (text ?? "").Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
            color = s.Length == 8
                ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }
    }
}
