using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 全屏取色笔：把整块虚拟屏幕抓成一张静态图铺满一个无边框置顶窗口，
    /// 用户在「冻结」的画面上移动鼠标即可看到放大镜与实时十六进制读数，
    /// 左键取样、右键或 Esc 取消。
    ///
    /// 之所以冻屏而不是实时预览：抓一帧后窗口本身会盖在原始内容上，实时预览
    /// 会把取色窗口自己拍进去形成无限套娃；冻屏则所见即所得，且性能稳定。
    /// </summary>
    internal sealed class EyedropperWindow : Window
    {
        private const double MagnifierSize = 148;
        private const double Zoom = 6;

        private readonly ScreenCapture _capture;
        private readonly TaskCompletionSource<Color?> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Image _backdrop;
        private readonly Image _magImage;
        private readonly Border _magSwatch;
        private readonly TextBlock _magHex;
        private readonly StackPanel _magnifier;
        private readonly Canvas _overlay;
        private readonly ScaleTransform _magScale = new() { ScaleX = Zoom, ScaleY = Zoom };
        private readonly TranslateTransform _magTranslate = new();
        private bool _closed;

        // 取色期间必须有一个静态根引用把窗口「钉住」。WinUI 的 Window 若无人持有，
        // await 挂起后 win→_tcs→Task→续体→状态机→win 只是一个没有外部根的孤岛，
        // 抓帧产生的大对象很快触发 GC，窗口会在能交互前被回收关闭（表现为「点了没反应/一闪而过」）。
        private static EyedropperWindow? _active;

        private EyedropperWindow(ScreenCapture capture)
        {
            _capture = capture;

            var root = new Grid { Background = new SolidColorBrush(Colors.Black) };

            _backdrop = new Image
            {
                Source = capture.ToBitmapSource(),
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            root.Children.Add(_backdrop);

            // 放大镜 + 读数，浮在最上层且不接收指针（否则会挡住取样点击）。
            _overlay = new Canvas { IsHitTestVisible = false };

            var magCircle = new Grid { Width = MagnifierSize, Height = MagnifierSize };
            var magBorder = new Border
            {
                Width = MagnifierSize,
                Height = MagnifierSize,
                CornerRadius = new CornerRadius(MagnifierSize / 2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Colors.Black)
            };

            _magImage = new Image
            {
                Source = _backdrop.Source,
                Stretch = Stretch.Fill,
                RenderTransformOrigin = new Point(0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            var group = new TransformGroup();
            group.Children.Add(_magScale);
            group.Children.Add(_magTranslate);
            _magImage.RenderTransform = group;

            // 中心十字标记被采样的那一个像素。
            var crosshair = new Rectangle
            {
                Width = Zoom + 2,
                Height = Zoom + 2,
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            // 放大画面单独放一个 148×148 容器并裁成圆：
            // Border.CornerRadius 只裁自己的 Background/BorderBrush、不裁子元素，
            // 直接把整屏尺寸的放大图塞进圆角 Border 会铺满全屏（表现为「放大镜没显示」）。
            // 而 WinUI 的 UIElement.Clip 只接受 RectangleGeometry，圆形只能用合成层裁剪。
            var magHost = new Grid { Width = MagnifierSize, Height = MagnifierSize };
            magHost.Children.Add(_magImage);
            magHost.Loaded += (_, _) => ApplyCircleClip(magHost);

            magCircle.Children.Add(magBorder);
            magCircle.Children.Add(magHost);
            magCircle.Children.Add(crosshair);

            _magSwatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            _magHex = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center
            };
            var readout = new Border
            {
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(10, 5, 12, 5),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 20, 20, 20)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _magSwatch, _magHex }
                }
            };

            _magnifier = new StackPanel
            {
                IsHitTestVisible = false,
                Children = { magCircle, readout }
            };
            _overlay.Children.Add(_magnifier);
            root.Children.Add(_overlay);

            // 透明的全屏按钮：既能拿到带坐标的指针事件，又能在获得焦点后接收 Esc。
            var hit = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                UseSystemFocusVisuals = false,
                FocusVisualPrimaryThickness = new Thickness(0),
                FocusVisualSecondaryThickness = new Thickness(0),
                Content = null
            };
            hit.PointerMoved += Hit_PointerMoved;
            // 必须用 AddHandler(handledEventsToo:true)：Button 的类处理器会把「左键按下」标记为
            // 已处理（那是它 Click 语义的一部分），普通 += 收不到左键，只有右键还能进来 ——
            // 表现就是「右键能取消，左键点不动」。
            hit.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Hit_PointerPressed), true);
            hit.KeyDown += Hit_KeyDown;
            root.Children.Add(hit);

            Content = root;

            root.Loaded += (_, _) =>
            {
                hit.Focus(FocusState.Programmatic);
                PositionFullscreen();
            };
            root.SizeChanged += (_, _) => PositionFullscreen();
        }

        public static async Task<Color?> PickAsync()
        {
            var capture = ScreenCapture.CaptureVirtualScreen();
            if (capture == null)
            {
                LauncherLogService.Warn("Eyedropper", "抓屏返回空，取色已取消");
                return null;
            }

            var win = new EyedropperWindow(capture);
            _active = win;
            win.Activate();
            try
            {
                return await win._tcs.Task;
            }
            finally
            {
                _active = null;
            }
        }

        private void PositionFullscreen()
        {
            try
            {
                var appWindow = AppWindow;
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.SetBorderAndTitleBar(false, false);
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    presenter.IsAlwaysOnTop = true;
                }
                appWindow.IsShownInSwitchers = false;
                appWindow.MoveAndResize(new RectInt32(
                    _capture.OriginX, _capture.OriginY, _capture.Width, _capture.Height));

                if (Content is FrameworkElement root)
                {
                    _magImage.Width = root.ActualWidth;
                    _magImage.Height = root.ActualHeight;
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Eyedropper", "定位全屏取色窗口失败", ex);
            }
        }

        private (int px, int py, Color c) SampleAt(Windows.Foundation.Point dip)
        {
            double w = _backdrop.ActualWidth;
            double h = _backdrop.ActualHeight;
            if (w <= 0 || h <= 0) return (0, 0, Colors.Black);
            int pixelX = _capture.OriginX + (int)Math.Round(dip.X / w * _capture.Width);
            int pixelY = _capture.OriginY + (int)Math.Round(dip.Y / h * _capture.Height);
            pixelX = Math.Clamp(pixelX, _capture.OriginX, _capture.OriginX + _capture.Width - 1);
            pixelY = Math.Clamp(pixelY, _capture.OriginY, _capture.OriginY + _capture.Height - 1);
            return (pixelX, pixelY, _capture.GetPixel(pixelX, pixelY));
        }

        /// <summary>用合成层的圆角矩形裁剪（半径=半边长即成正圆）把放大画面裁成圆形放大镜。</summary>
        private static void ApplyCircleClip(Grid host)
        {
            try
            {
                if (ElementCompositionPreview.GetElementVisual(host) is not ContainerVisual visual) return;
                var compositor = visual.Compositor;
                var geometry = compositor.CreateRoundedRectangleGeometry();
                geometry.Size = new Vector2((float)host.Width, (float)host.Height);
                geometry.CornerRadius = new Vector2((float)(host.Width / 2), (float)(host.Height / 2));
                visual.Clip = compositor.CreateGeometricClip(geometry);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Eyedropper", "放大镜圆形裁剪失败（放大镜会呈方形，不影响取色）", ex);
            }
        }

        private void Hit_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Content is not FrameworkElement root) return;
            // PositionFullscreen 可能在首次布局前就跑过（那时 ActualWidth 还是 0），
            // 放大图尺寸必须与窗口 DIP 尺寸一致，平移量才算得对，这里每次移动兜一次同步。
            if (root.ActualWidth > 0 && (Math.Abs(_magImage.Width - root.ActualWidth) > 0.5
                                      || Math.Abs(_magImage.Height - root.ActualHeight) > 0.5))
            {
                _magImage.Width = root.ActualWidth;
                _magImage.Height = root.ActualHeight;
            }
            var dip = e.GetCurrentPoint(root).Position;
            var (_, _, c) = SampleAt(dip);

            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _magHex.Text = hex;
            _magSwatch.Background = new SolidColorBrush(c);

            // 放大镜图像按 DIP 铺满窗口，缩放后把采样点平移到圆心。
            _magTranslate.X = MagnifierSize / 2 - Zoom * dip.X;
            _magTranslate.Y = MagnifierSize / 2 - Zoom * dip.Y;

            double left = dip.X + 24;
            double top = dip.Y + 24;
            if (left + MagnifierSize > root.ActualWidth) left = dip.X - MagnifierSize - 24;
            if (top + MagnifierSize + 40 > root.ActualHeight) top = dip.Y - MagnifierSize - 40;
            Canvas.SetLeft(_magnifier, Math.Max(0, left));
            Canvas.SetTop(_magnifier, Math.Max(0, top));
        }

        private void Hit_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (Content is not FrameworkElement root) return;
            var props = e.GetCurrentPoint(root).Properties;
            if (props.IsRightButtonPressed)
            {
                Finish(null);
                return;
            }
            var dip = e.GetCurrentPoint(root).Position;
            var (_, _, c) = SampleAt(dip);
            Finish(c);
        }

        private void Hit_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                Finish(null);
                e.Handled = true;
            }
        }

        private void Finish(Color? color)
        {
            if (_closed) return;
            _closed = true;
            _tcs.TrySetResult(color);
            try { Close(); } catch { }
        }
    }
}
