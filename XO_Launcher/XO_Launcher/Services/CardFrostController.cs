using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 逐卡片毛玻璃（方案 B：专用磨砂层）。
    ///
    /// 为什么不能把磨砂挂到卡片自己身上：WinUI 里往任何 XAML 元素的合成可视上挂子视觉，
    /// 永远渲染在该元素内容**之上**（InsertAtBottom 只在手动挂的子视觉之间排序，压不到 XAML
    /// 内容下面），挂父级更会盖住整片卡片。唯一可靠的「全景之上、内容之下」次序靠 XAML 兄弟
    /// 次序实现 —— MainWindow.xaml 里夹在 PanoramaHost 与内容层之间的 FrostLayer。
    ///
    /// 本控制器在 FrostLayer 的合成可视上为每张卡片摆一个 backdrop 高斯模糊 SpriteVisual，
    /// 位置/尺寸/圆角用 TransformToVisual 实时重算（卡片在各自 ScrollViewer 里会滚动，
    /// 磨砂层不在滚动范围内，必须自己跟随）。全部操作兜 try/catch，失败只降级为普通卡片。
    /// </summary>
    internal sealed class CardFrostController
    {
        private const string CardBrushKey = "CardBackgroundBrush";
        private const double MaxBlur = 40;

        private readonly FrameworkElement _root;
        private readonly FrameworkElement _layer;
        private readonly Compositor _compositor;
        private readonly ContainerVisual _layerVisual;
        private readonly SolidColorBrush _transparent = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        private readonly Dictionary<Border, SpriteVisual> _attached = new();
        private readonly Dictionary<Border, CompositionEffectBrush> _brushes = new();
        private readonly Dictionary<Border, (CompositionGeometricClip Clip, CompositionRoundedRectangleGeometry Geometry)> _clips = new();
        private readonly HashSet<ScrollViewer> _scrollers = new();

        private float _blur;
        private Windows.UI.Color _tint;
        private bool _active;
        private bool _hooked;
        private bool _workQueued;

        private readonly Func<Border, bool>? _accept;

        /// <param name="accept">只给满足条件的卡片挂磨砂；页面磨砂层与弹窗磨砂层用它互斥分工，
        /// 否则两个控制器会抢同一张卡片、互相覆写 Background。为 null 表示所有可见卡片都挂。</param>
        public CardFrostController(FrameworkElement root, FrameworkElement frostLayer, Func<Border, bool>? accept = null)
        {
            _root = root;
            _layer = frostLayer;
            _accept = accept;
            if (ElementCompositionPreview.GetElementVisual(frostLayer) is not ContainerVisual visual)
                throw new InvalidOperationException("磨砂层合成可视不可用");
            _layerVisual = visual;
            _compositor = visual.Compositor;
        }

        /// <summary>模糊半径（0–40）；实时作用到所有已附加卡片。</summary>
        public void SetBlur(double pixels)
        {
            _blur = (float)Math.Clamp(pixels, 0, MaxBlur);
            foreach (var brush in _brushes.Values)
            {
                try { brush.Properties.InsertScalar("blur.BlurAmount", _blur); }
                catch { /* 单个画刷更新失败忽略 */ }
            }
        }

        /// <summary>卡片着色（半透明，随明暗主题变化）；启用中切换会整体重建一次。</summary>
        public void SetTint(Windows.UI.Color tint)
        {
            if (tint.Equals(_tint)) return;
            _tint = tint;
            if (!_active) return;
            Disable();
            Enable();
        }

        public void Enable()
        {
            if (_active) { Rescan(); return; }
            _active = true;
            Hook();
            Rescan();
        }

        public void Disable()
        {
            if (!_active) return;
            _active = false;
            Unhook();
            foreach (var border in new List<Border>(_attached.Keys))
                Detach(border);
            _attached.Clear();
            _brushes.Clear();
            _clips.Clear();
        }

        /// <summary>重新扫描可视树：给新出现的卡片补挂磨砂、给消失的卡片回收、再重算全部位置。</summary>
        public void Rescan()
        {
            if (!_active) return;
            try
            {
                PruneDetached();
                if (ThemeService.Brush(CardBrushKey) is Brush marker)
                    Walk(_root, marker);
                UpdatePositions();
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Frost", "扫描卡片毛玻璃失败", ex);
            }
        }

        private void Walk(DependencyObject parent, Brush marker)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Border border
                    && ReferenceEquals(border.Background, marker)
                    && !_attached.ContainsKey(border)
                    && (_accept is null || _accept(border))
                    && IsReallyVisible(border))
                {
                    Attach(border);
                }
                Walk(child, marker);
            }
        }

        /// <summary>页面 Host 用 Visibility=Collapsed 切换，但可视树遍历仍会路过它们；跳过不可见的卡片。</summary>
        private bool IsReallyVisible(Border border)
        {
            for (DependencyObject? current = border;
                 current is not null && current != _root;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement { Visibility: Visibility.Visible }) continue;
                return false;
            }
            return true;
        }

        private void Attach(Border border)
        {
            try
            {
                var brush = CreateBrush();
                var sprite = _compositor.CreateSpriteVisual();
                sprite.Brush = brush;

                var geometry = _compositor.CreateRoundedRectangleGeometry();
                var clip = _compositor.CreateGeometricClip(geometry);
                sprite.Clip = clip;

                _layerVisual.Children.InsertAtBottom(sprite);

                _attached[border] = sprite;
                _brushes[border] = brush;
                _clips[border] = (clip, geometry);
                border.Background = _transparent;
                HookScrollers(border);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Frost", "附加卡片毛玻璃失败", ex);
            }
        }

        private void Detach(Border border)
        {
            try
            {
                if (_attached.TryGetValue(border, out var sprite))
                {
                    _layerVisual.Children.Remove(sprite);
                    sprite.Dispose();
                }
                if (_brushes.TryGetValue(border, out var brush))
                    brush.Dispose();
                border.Background = ThemeService.Brush(CardBrushKey);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Frost", "移除卡片毛玻璃失败", ex);
            }
            _attached.Remove(border);
            _brushes.Remove(border);
            _clips.Remove(border);
        }

        /// <summary>清理已从可视树移除或已隐藏的卡片（翻页、动态消息、弹窗关闭等）。</summary>
        private void PruneDetached()
        {
            List<Border>? dead = null;
            foreach (var border in _attached.Keys)
            {
                if (!IsReallyVisible(border))
                    (dead ??= new List<Border>()).Add(border);
            }
            if (dead is null) return;
            foreach (var border in dead)
                Detach(border);
        }

        /// <summary>
        /// 按卡片当前实际布局重算磨砂 sprite 的位置/尺寸/圆角。
        /// TransformToVisual 已包含祖先 ScrollViewer 的滚动偏移；磨砂层是 RootGrid 的直接子层，
        /// 两者坐标系一致，无需再乘缩放比（XAML 单位、同一视图像空间）。
        /// </summary>
        private void UpdatePositions()
        {
            if (!_active) return;
            foreach (var (border, sprite) in _attached)
            {
                try
                {
                    if (border.ActualWidth <= 0 || border.ActualHeight <= 0)
                    {
                        sprite.IsVisible = false;
                        continue;
                    }
                    var p = border.TransformToVisual(_layer).TransformPoint(new Point(0, 0));
                    sprite.IsVisible = true;
                    sprite.Offset = new Vector3((float)p.X, (float)p.Y, 0);
                    var size = new Vector2((float)border.ActualWidth, (float)border.ActualHeight);
                    sprite.Size = size;

                    if (_clips.TryGetValue(border, out var clipPair))
                    {
                        var radius = (float)border.CornerRadius.TopLeft;
                        if (radius > 0.5f)
                        {
                            clipPair.Geometry.Size = size;
                            clipPair.Geometry.CornerRadius = new Vector2(radius, radius);
                            sprite.Clip = clipPair.Clip;
                        }
                        else
                        {
                            sprite.Clip = null;
                        }
                    }
                }
                catch (Exception)
                {
                    // 转换失败（卡片刚脱离可视树等）：本帧先隐藏，下一帧再试
                    try { sprite.IsVisible = false; } catch { }
                }
            }
        }

        private CompositionEffectBrush CreateBrush()
        {
            var backdrop = _compositor.CreateBackdropBrush();
            var factory = _compositor.CreateEffectFactory(BuildGraph(), new[] { "blur.BlurAmount" });
            var brush = factory.CreateBrush();
            brush.SetSourceParameter("backdrop", backdrop);
            brush.Properties.InsertScalar("blur.BlurAmount", _blur);
            return brush;
        }

        /// <summary>高斯模糊(背景) 之上叠一层半透明着色，合成磨砂玻璃效果。</summary>
        private CompositeEffect BuildGraph()
        {
            var blur = new GaussianBlurEffect
            {
                Name = "blur",
                BlurAmount = _blur,
                BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("backdrop"),
            };
            var tint = new ColorSourceEffect { Name = "tint", Color = _tint };
            return new CompositeEffect
            {
                Mode = CanvasComposite.SourceOver,
                Sources = { blur, tint },
            };
        }

        // ==================== 事件挂钩 ====================

        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            _root.LayoutUpdated += OnLayoutUpdated;
            _root.SizeChanged += OnAnythingChanged;
            _layer.SizeChanged += OnAnythingChanged;
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            _root.LayoutUpdated -= OnLayoutUpdated;
            _root.SizeChanged -= OnAnythingChanged;
            _layer.SizeChanged -= OnAnythingChanged;
            foreach (var scroller in _scrollers)
                scroller.ViewChanged -= OnAnythingChanged;
            _scrollers.Clear();
        }

        /// <summary>卡片所在页面的 ScrollViewer 滚动不触发 LayoutUpdated，需单独挂钩。</summary>
        private void HookScrollers(Border border)
        {
            for (DependencyObject? current = VisualTreeHelper.GetParent(border);
                 current is not null && current != _root;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is ScrollViewer scroller && _scrollers.Add(scroller))
                    scroller.ViewChanged += OnAnythingChanged;
            }
        }

        private void OnLayoutUpdated(object? sender, object e)
        {
            QueueWork(fullScan: true);
        }

        private void OnAnythingChanged(object? sender, object e)
        {
            QueueWork(fullScan: false);
        }

        private void QueueWork(bool fullScan)
        {
            if (!_active || _workQueued) return;
            _workQueued = true;
            _root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _workQueued = false;
                if (!_active) return;
                if (fullScan) Rescan();
                else UpdatePositions();
            });
        }
    }
}
