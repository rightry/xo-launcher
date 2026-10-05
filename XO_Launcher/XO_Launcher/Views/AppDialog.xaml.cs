using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>弹窗按钮语义。Close = 取消类（次要），Secondary = 中间动作，Primary = 确认类（主要）。</summary>
    public enum AppDialogResult
    {
        None,
        Primary,
        Secondary,
        Close
    }

    /// <summary>
    /// 自绘弹窗。**替代全应用的 WinUI ContentDialog**。
    ///
    /// 为什么要自己画：ContentDialog 的模板读一整套 ContentDialog* 资源键，
    /// 且这些键的解析时机早于 ThemeService 的覆盖，导致叠色、分隔线、圆角留白
    /// 都改不动（历史上有半页 App.xaml 和 ThemeService.ApplyDialogChrome 在为它打补丁）。
    /// 自己画一层遮罩 + 卡片，样式完全可控，也不再需要那套补丁键。
    ///
    /// 用法（刻意做成贴近 ContentDialog 的写法，便于逐处替换）：
    ///     var result = await AppDialog.ShowAsync(
    ///         XamlRoot, "标题", "正文", primary: "确定", close: "取消");
    ///     if (result == AppDialogResult.Primary) { ... }
    /// </summary>
    public sealed partial class AppDialog : UserControl
    {
        private TaskCompletionSource<AppDialogResult>? _completion;
        private AppDialogResult _result = AppDialogResult.None;

        public AppDialog()
        {
            InitializeComponent();
        }

        /// <summary>正文文本。设置后自动显示。</summary>
        public string Message
        {
            get => MessageText.Text;
            set
            {
                MessageText.Text = value ?? "";
                MessageText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>自定义内容（接替 ContentDialog 的 Content）。</summary>
        public object? Body
        {
            get => BodyHost.Content;
            set
            {
                BodyHost.Content = value;
                BodyHost.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>把弹窗挂到指定根上并等待用户选择。root 为 null 时不做任何事。</summary>
        public static Task<AppDialogResult> ShowAsync(
            XamlRoot? root,
            string title,
            string message,
            string? primary = null,
            string? close = null,
            object? body = null,
            string? secondary = null,
            double maxWidth = 560)
        {
            var dialog = new AppDialog();
            dialog.Configure(title, message, primary, close, body);
            if (!string.IsNullOrEmpty(secondary))
                dialog.AddButton(secondary!, AppDialogResult.Secondary, isPrimary: false, index: 1);
            dialog.Card.MaxWidth = maxWidth;
            return dialog.ShowAsync(root);
        }

        /// <summary>只读提示（仅一个「确定」按钮）。</summary>
        public static Task ShowMessageAsync(XamlRoot? root, string title, string message)
            => ShowAsync(root, title, message, primary: null, close: "确定");

        public void Configure(string title, string message, string? primary, string? close, object? body)
        {
            TitleText.Text = title ?? "";
            TitleText.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
            Message = message ?? "";

            if (!string.IsNullOrEmpty(close))
                AddButton(close!, AppDialogResult.Close, isPrimary: false);
            if (!string.IsNullOrEmpty(primary))
                AddButton(primary!, AppDialogResult.Primary, isPrimary: true);

            // 一个按钮都没有时补一个「确定」，否则弹窗关不掉。
            if (ButtonRow.Children.Count == 0)
                AddButton("确定", AppDialogResult.Close, isPrimary: true);

            Body = body;
        }

        public async Task<AppDialogResult> ShowAsync(XamlRoot? root)
        {
            if (root is null) return AppDialogResult.None;

            // 挂到应用根的可视树上。Catalog 最后一层就是最顶层，弹窗盖住全窗。
            var host = FindTopLevelHost(root);
            if (host is null) return AppDialogResult.None;

            _completion = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Children.Add(this);
            // 窗口根 Grid 带行定义（顶栏 48 / 内容 * / 底栏 Auto）。不显式跨行时
            // 弹窗会落进第 0 行的 48px 顶栏格里，卡片上下 Margin 32 一加高度直接
            // 量成 0 —— 表现就是「遮罩只有一条、弹窗本体渲染不出来」。
            if (host is Grid)
            {
                Grid.SetRow(this, 0);
                Grid.SetRowSpan(this, 999);
                Grid.SetColumnSpan(this, 999);
            }
            RootLayer.Visibility = Visibility.Visible;

            // 打开动画用系统主题动画（FadeIn/DrillIn）而非 DoubleAnimation +
            // RenderTransform 嵌套路径：后者在 WinUI 3 里可能静默失败，
            // 若配合「先置 Opacity=0 再动画回 1」就会把整个弹窗留在透明态（看似没渲染出来）。
            // 主题动画不改基础属性，即使动画失败弹窗依然完整可见。
            try
            {
                var fade = new Microsoft.UI.Xaml.Media.Animation.FadeInThemeAnimation
                {
                    Duration = TimeSpan.FromMilliseconds(140)
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, RootLayer);
                var enter = new Microsoft.UI.Xaml.Media.Animation.DrillInThemeAnimation
                {
                    Duration = TimeSpan.FromMilliseconds(160)
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(enter, Card);
                var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                story.Children.Add(fade);
                story.Children.Add(enter);
                story.Begin();
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AppDialog", "弹窗打开动画失败（不影响显示）", ex);
            }

            try
            {
                return await _completion.Task;
            }
            finally
            {
                RootLayer.Visibility = Visibility.Collapsed;
                host.Children.Remove(this);
                _completion = null;
            }
        }

        /// <summary>
        /// 找到承载弹窗的最顶层容器。XamlRoot.Content 是窗口内容根；
        /// 用 Grid 包一层自己的子元素，避免直接往非 Panel 上挂。
        /// </summary>
        private static Panel? FindTopLevelHost(XamlRoot root)
        {
            if (root.Content is not FrameworkElement content) return null;
            if (content is Panel panel) return panel;
            return null;
        }

        private void AddButton(string text, AppDialogResult result, bool isPrimary, int? index = null)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 84,
                Height = 36,
                Padding = new Thickness(18, 0, 18, 0),
                Style = (Style)Application.Current.Resources["AppButtonStyle"],
                Background = isPrimary
                    ? ThemeService.Brush("AccentBrush")
                    : ThemeService.Brush("NeutralButtonBrush"),
                Foreground = isPrimary
                    ? ThemeService.Brush("AccentOnBrush")
                    : ThemeService.Brush("NeutralButtonForegroundBrush"),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };
            button.Click += (_, _) => Complete(result);
            if (index is null || index >= ButtonRow.Children.Count)
                ButtonRow.Children.Add(button);
            else
                ButtonRow.Children.Insert(index.Value, button);
        }

        /// <summary>Esc 关闭；点遮罩也算取消。</summary>
        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Complete(AppDialogResult.Close);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Scrim_Tapped(object sender, TappedRoutedEventArgs e)
            => Complete(AppDialogResult.Close);

        private void Complete(AppDialogResult result)
        {
            if (_completion is null || _completion.Task.IsCompleted) return;
            _result = result;
            _completion.TrySetResult(result);
        }

        /// <summary>最近一次结果（供不便使用返回值的调用点读取）。</summary>
        public AppDialogResult Result => _result;
    }
}
