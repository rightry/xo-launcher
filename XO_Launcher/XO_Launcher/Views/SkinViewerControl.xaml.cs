using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class SkinViewerControl : UserControl
    {
        private bool _ready;
        private bool _failed;
        private bool _slim;
        private string? _skinPath;

        /// <summary>
        /// 「像素来源」下的皮肤像素缓存（内存编码，不落盘）。
        /// 与 <see cref="_skinPath"/> 二选一：_pixels 非空表示最近一次是直接喂像素的（皮肤编辑器实时预览），
        /// 为空表示最近一次来自磁盘文件。左右手模型切换按钮据此决定用哪条路径重推。
        /// </summary>
        private byte[]? _pixels;

        private TaskCompletionSource<bool>? _navigated;

        public SkinViewerControl()
        {
            InitializeComponent();
        }

        public static async Task ShowDialogAsync(XamlRoot? xamlRoot, string? pngPath, bool slim, string title)
        {
            var viewer = new SkinViewerControl
            {
                Width = 320,
                Height = 420
            };
            _ = viewer.ShowSkinAsync(pngPath, slim);
            await AppDialog.ShowAsync(xamlRoot, title, "", close: "关闭", body: viewer);
        }

        /// <summary>从磁盘上的皮肤 PNG 加载 3D 预览（皮肤来源卡片、资源列表等走这条）。</summary>
        public async Task ShowSkinAsync(string? pngPath, bool slim)
        {
            _skinPath = pngPath;
            _pixels = null;
            _slim = slim;
            UpdateModelButtons();

            if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath))
            {
                SetStatus("还没有可预览的皮肤。");
                return;
            }

            SetStatus("正在加载 3D 预览…", loading: true);
            await PushToViewerAsync();
        }

        /// <summary>
        /// 直接把内存里的 64×64 BGRA8 像素推给 3D 预览（不产生临时文件）。
        /// 皮肤编辑器「边画边看」走这条。
        /// </summary>
        public async Task ShowSkinPixelsAsync(byte[] bgra64, bool slim)
        {
            if (bgra64 is null || bgra64.Length != 64 * 64 * 4)
            {
                SetStatus("皮肤像素数据不合法，无法预览。");
                return;
            }

            // 存副本：调用方后续还会继续改它（编辑器每落一笔都会推一次）
            var copy = new byte[bgra64.Length];
            Buffer.BlockCopy(bgra64, 0, copy, 0, bgra64.Length);
            _pixels = copy;
            _skinPath = null;
            _slim = slim;
            UpdateModelButtons();

            SetStatus("正在加载 3D 预览…", loading: true);
            await PushToViewerAsync();
        }

        /// <summary>把当前来源（内存像素优先，否则磁盘文件）编码成 PNG data URL 并推给 WebView2。</summary>
        private async Task PushToViewerAsync()
        {
            var pixels = _pixels;
            var path = _skinPath;
            if (pixels is null && (string.IsNullOrWhiteSpace(path) || !File.Exists(path)))
            {
                SetStatus("还没有可预览的皮肤。");
                return;
            }

            try
            {
                await EnsureViewerAsync();
                if (_failed)
                {
                    // WebView2 起不来：只有磁盘来源才能退回贴图显示，内存来源没有文件可显示
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) ShowFallbackImage(path);
                    else
                    {
                        SetStatus("3D 引擎不可用。");
                    }
                    return;
                }

                byte[] png = pixels is not null
                    ? await SkinPngCodec.EncodeToBytesAsync(pixels)
                    : await File.ReadAllBytesAsync(path!);

                var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
                // 同理：只做 JS 字符串字面量转义，不能用走反射的 JsonSerializer
                // （Release 构建下 JsonSerializer.IsReflectionEnabledByDefault 为 false）
                var js = "window.setSkin(\"" + System.Text.Json.JsonEncodedText.Encode(dataUrl).ToString()
                         + "\", " + (_slim ? "true" : "false") + ")";
                await View.ExecuteScriptAsync(js);
                SetStatus(null);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Skin", "3D 预览失败：" + ex.Message);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) ShowFallbackImage(path);
                else
                {
                    SetStatus("无法显示 3D 预览。");
                }
            }
        }

        private async Task EnsureViewerAsync()
        {
            if (_ready || _failed) return;
            try
            {
                await View.EnsureCoreWebView2Async();
                View.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                View.CoreWebView2.Settings.AreDevToolsEnabled = false;
                View.CoreWebView2.Settings.IsStatusBarEnabled = false;
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
                View.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "xo-skin.local",
                    assets,
                    CoreWebView2HostResourceAccessKind.Allow);
                _navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                View.NavigationCompleted += OnNavigated;
                View.Source = new Uri("https://xo-skin.local/skin-viewer.html");
                var finished = await Task.WhenAny(_navigated.Task, Task.Delay(8000));
                if (finished != _navigated.Task || !_navigated.Task.Result)
                    throw new InvalidOperationException("皮肤预览页加载超时");
                await Task.Delay(400);
                _ready = true;
            }
            catch (Exception ex)
            {
                _failed = true;
                LauncherLogService.Warn("Skin", "WebView2 无法初始化：" + ex.Message);
            }
        }

        /// <summary>
        /// 休眠：把 3D 预览导航到空白页，并将 WebView2 内存目标压到低档 —— 渲染进程里的
        /// WebGL 上下文与皮肤贴图是启动器最大的一块内存，托管堆的 GC 收不走它。
        /// 唤醒：重走原有的装载路径（EnsureViewerAsync 会重新导航，PushToViewerAsync 重推当前皮肤）。
        /// </summary>
        public void SetMemoryTarget(bool low)
        {
            try
            {
                var core = View.CoreWebView2;
                if (core is null) return;   // 引擎还没起来，本来就没占内存

                core.MemoryUsageTargetLevel = low
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;

                if (low)
                {
                    if (!_ready || _failed) return;
                    _ready = false;
                    core.Navigate("about:blank");
                }
                else
                {
                    _ = PushToViewerAsync();
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Skin", "调整 3D 预览内存档位失败：" + ex.Message);
            }
        }

        private void OnNavigated(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            sender.NavigationCompleted -= OnNavigated;
            _navigated?.TrySetResult(args.IsSuccess);
        }

        private void ShowFallbackImage(string pngPath)
        {
            try
            {
                var image = new BitmapImage();
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(Path.GetFullPath(pngPath));
                FallbackImage.Source = image;
                FallbackImage.Visibility = Visibility.Visible;
                View.Visibility = Visibility.Collapsed;
                SetStatus("无法加载 3D 引擎，已显示皮肤贴图。");
            }
            catch (Exception)
            {
                SetStatus("无法预览这张皮肤。");
            }
        }

        /// <summary>
        /// 状态小卡片唯一写入口：text=null 收起整张卡；只有 loading=true 才转环。
        /// 转环必须跟着文案语义走 —— 错误提示旁边挂转环会让人以为还在加载。
        /// </summary>
        private void SetStatus(string? text, bool loading = false)
        {
            if (text is null)
            {
                StatusPanel.Visibility = Visibility.Collapsed;
                return;
            }
            StatusText.Text = text;
            StatusRing.IsActive = loading;
            StatusRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
        }

        private async void Classic_Click(object sender, RoutedEventArgs e)
        {
            if (!_slim) return;
            await ReshowAsync(slim: false);
        }

        private async void Slim_Click(object sender, RoutedEventArgs e)
        {
            if (_slim) return;
            await ReshowAsync(slim: true);
        }

        /// <summary>
        /// 切换经典/纤细模型时按「最近一次的来源」重推：
        /// 内存像素来源就直接用缓存像素重推，否则回到磁盘文件那条路径。
        /// （原先直接调 ShowSkinAsync(_skinPath)，像素来源下 _skinPath 为 null 会误报「还没有可预览的皮肤」。）
        /// </summary>
        private Task ReshowAsync(bool slim)
        {
            var pixels = _pixels;
            return pixels is not null ? ShowSkinPixelsAsync(pixels, slim) : ShowSkinAsync(_skinPath, slim);
        }

        private void UpdateModelButtons()
        {
            ClassicButton.Background = ThemeService.Brush(_slim ? "NeutralButtonBrush" : "AccentBrush");
            ClassicButton.Foreground = ThemeService.Brush(_slim ? "NeutralButtonForegroundBrush" : "AccentOnBrush");
            SlimButton.Background = ThemeService.Brush(_slim ? "AccentBrush" : "NeutralButtonBrush");
            SlimButton.Foreground = ThemeService.Brush(_slim ? "AccentOnBrush" : "NeutralButtonForegroundBrush");
        }
    }
}
