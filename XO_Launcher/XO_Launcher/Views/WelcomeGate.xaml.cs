using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using WinRT.Interop;
using XO_Launcher.Api;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 启动遮罩：把新手引导与更新说明串成一串页，走完或跳过后触发 <see cref="Finished"/>，
    /// 由 MainWindow 摘掉遮罩、写回标记，主界面这才露出来。
    /// 引导页里挂的是真实操作（登录 / 选目录 / 装首个版本 / 挑外观），不是纯图文。
    /// </summary>
    public sealed partial class WelcomeGate : UserControl
    {
        public event Action? Finished;

        private readonly IReadOnlyList<WelcomePages.Page> _pages;
        private readonly MojangApi _mojangApi = new();
        private readonly GameInstallService _installer = new();
        private IReadOnlyList<MinecraftVersion> _versions = Array.Empty<MinecraftVersion>();
        private bool _versionsLoading;
        private bool _installing;
        private bool _syncingSwitch;
        private int _index;

        public WelcomeGate(IReadOnlyList<WelcomePages.Page> pages)
        {
            InitializeComponent();
            _pages = pages;
            Show(0);
        }

        private void Show(int index)
        {
            _index = Math.Clamp(index, 0, Math.Max(0, _pages.Count - 1));
            var page = _pages[_index];

            EyebrowText.Text = page.Eyebrow;
            StepIcon.Glyph = page.Glyph;
            StepTitle.Text = page.Title;

            BulletsHost.Children.Clear();
            foreach (var bullet in page.Bullets)
            {
                BulletsHost.Children.Add(new TextBlock
                {
                    Text = "·  " + bullet,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.Brush("TextSecondaryBrush"),
                });
            }

            AccountPanel.Visibility = PanelFor(page.Kind, "account");
            FolderPanel.Visibility = PanelFor(page.Kind, "folder");
            InstallPanel.Visibility = PanelFor(page.Kind, "install");
            PersonalizePanel.Visibility = PanelFor(page.Kind, "personalize");

            switch (page.Kind)
            {
                case "account": RefreshAccountState(); break;
                case "folder": RefreshFolderState(); break;
                case "install": _ = EnsureVersionsAsync(); break;
                case "personalize": RefreshPersonalizeState(); break;
            }

            // 进度点：当前页拉成长条，其余是小圆点，一眼看得出还剩几页
            DotsHost.Children.Clear();
            for (var i = 0; i < _pages.Count; i++)
            {
                DotsHost.Children.Add(new Ellipse
                {
                    Width = i == _index ? 18 : 6,
                    Height = 6,
                    Fill = ThemeService.Brush(i == _index ? "AccentBrush" : "DividerBrush"),
                });
            }

            PrevButton.IsEnabled = _index > 0;
            SkipButton.Visibility = _index == _pages.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
            NextButton.Content = _index == _pages.Count - 1 ? "开始使用" : "下一步";
            PageIndicatorText.Text = $"{_index + 1} / {_pages.Count}";
        }

        private static Visibility PanelFor(string kind, string self)
            => kind == self ? Visibility.Visible : Visibility.Collapsed;

        // ==================== 第 1 步：账号 ====================

        private void RefreshAccountState()
        {
            var account = AccountStore.Instance.Selected;
            AccountStateText.Text = account is null
                ? "尚未登录"
                : "当前：" + account.Username + "（" + account.TypeLabel + "）";
        }

        private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
        {
            LoginOverlay.Visibility = Visibility.Visible;
            try
            {
                var account = await LoginOverlay.RunAsync();
                await UiDispatch.RunAsync(() =>
                {
                    AccountStore.Instance.AddOrReplaceMicrosoft(account);
                    RefreshAccountState();
                    AccountHintText.Text = "已登录微软正版账号，皮肤与官方联机都可用。";
                });
            }
            catch (OperationCanceledException)
            {
                await UiDispatch.RunAsync(() => AccountHintText.Text = "已取消登录，这一步可以先跳过。");
            }
            catch (Exception ex)
            {
                await UiDispatch.RunAsync(() => AccountHintText.Text = "登录失败：" + ex.Message);
            }
            finally
            {
                LoginOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void AddOffline_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AccountStore.Instance.AddOffline(OfflineNameBox.Text);
                OfflineNameBox.Text = "";
                RefreshAccountState();
                AccountHintText.Text = "已添加离线账号。离线账号没有官方皮肤服务器，皮肤只能在本启动器内预览。";
            }
            catch (Exception ex)
            {
                AccountHintText.Text = ex.Message;
            }
        }

        // ==================== 第 2 步：游戏目录 ====================

        private void RefreshFolderState()
        {
            GameDirText.Text = LauncherSettings.Instance.GameDirectory;
            FolderHintText.Text = "";
        }

        private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, GetHwnd());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            try
            {
                var resolved = GameDirectoryService.Apply(folder.Path, out var summary);
                GameDirText.Text = resolved;
                FolderHintText.Text = summary;
            }
            catch (Exception ex)
            {
                FolderHintText.Text = "无法使用该目录：" + ex.Message;
            }
        }

        // ==================== 第 3 步：装第一个版本 ====================

        private async Task EnsureVersionsAsync()
        {
            if (_versionsLoading || _versions.Count > 0) return;
            _versionsLoading = true;
            try
            {
                var manifest = await _mojangApi.GetVersionManifestAsync();
                var releases = manifest.Versions
                    .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                    .Take(40)
                    .ToList();
                await UiDispatch.RunAsync(() =>
                {
                    _versions = releases;
                    VersionCombo.ItemsSource = releases.Select(v => v.Id).ToList();
                    if (releases.Count > 0) VersionCombo.SelectedIndex = 0;
                    VersionCombo.PlaceholderText = "选择版本";
                });
            }
            catch (Exception ex)
            {
                await UiDispatch.RunAsync(() =>
                {
                    VersionCombo.PlaceholderText = "版本列表获取失败";
                    InstallStateText.Text = "获取版本列表失败：" + ex.Message
                        + "　这一步可以先跳过，之后在「下载」页安装。";
                });
            }
            finally
            {
                _versionsLoading = false;
            }
        }

        private void VersionCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (VersionCombo.SelectedIndex >= 0 && VersionCombo.SelectedIndex < _versions.Count)
                InstallStateText.Text = "会装成 " + _versions[VersionCombo.SelectedIndex].Id + "（不装加载器）";
        }

        private async void InstallFirst_Click(object sender, RoutedEventArgs e)
        {
            if (_installing) return;
            var pick = VersionCombo.SelectedIndex;
            if (pick < 0 || pick >= _versions.Count)
            {
                InstallStateText.Text = "先选一个版本。";
                return;
            }

            var version = _versions[pick];
            _installing = true;
            InstallFirstButton.IsEnabled = false;
            VersionCombo.IsEnabled = false;
            InstallRing.Visibility = Visibility.Visible;
            InstallRing.IsActive = true;
            InstallStateText.Text = "正在下载并安装 " + version.Id + "…";

            var task = DownloadService.Instance.StartInstall(version.Id, version.Id);
            try
            {
                var installedId = await _installer.InstallAsync(
                    version, "vanilla", null, task, version.Id,
                    installFabricApi: false, cancellationToken: task.Token);
                InstalledVersionService.Invalidate();
                LauncherSettings.Instance.SelectedVersion = installedId;
                InstallStateText.Text = "已安装 " + installedId + "，进主界面就能点启动。";
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                InstallStateText.Text = "已取消安装，可以稍后再装。";
            }
            catch (Exception ex)
            {
                InstallStateText.Text = "安装失败：" + ex.Message + "　可以先跳过，之后在「下载」页重试。";
            }
            finally
            {
                InstallRing.IsActive = false;
                InstallRing.Visibility = Visibility.Collapsed;
                VersionCombo.IsEnabled = true;
                InstallFirstButton.IsEnabled = true;
                _installing = false;
            }
        }

        // ==================== 第 4 步：外观 ====================

        private void RefreshPersonalizeState()
        {
            var theme = LauncherSettings.Instance.AppTheme;
            Highlight(ThemeSystemButton, theme == "system");
            Highlight(ThemeDarkButton, theme == "dark");
            Highlight(ThemeLightButton, theme == "light");

            _syncingSwitch = true;
            PanoramaSwitch.IsOn = LauncherSettings.Instance.PanoramaBackground;
            _syncingSwitch = false;
        }

        private static void Highlight(Button button, bool active)
        {
            // 必须走主题画刷：写死颜色在另一套主题下会变成「深底深字」读不出
            button.Background = ThemeService.Brush(active ? "AccentSoftBrush" : "NeutralButtonBrush");
            button.BorderBrush = ThemeService.Brush(active ? "AccentBrush" : "DividerBrush");
            button.BorderThickness = new Thickness(active ? 2 : 1);
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string theme }) return;
            LauncherSettings.Instance.AppTheme = theme;
            RefreshPersonalizeState();
        }

        private void Panorama_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncingSwitch) return;
            LauncherSettings.Instance.PanoramaBackground = PanoramaSwitch.IsOn;
        }

        private void Skip_Click(object sender, RoutedEventArgs e) => Finished?.Invoke();

        private void Prev_Click(object sender, RoutedEventArgs e) => Show(_index - 1);

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_index >= _pages.Count - 1) Finished?.Invoke();
            else Show(_index + 1);
        }

        private static IntPtr GetHwnd()
            => App.MainWindow is not null ? WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
    }
}
