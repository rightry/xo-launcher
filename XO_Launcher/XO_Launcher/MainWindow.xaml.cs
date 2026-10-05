using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Text;
using XO_Launcher.Cli;
using XO_Launcher.Models;
using XO_Launcher.Services;
using XO_Launcher.Views;

namespace XO_Launcher
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppWindow _appWindow;
        private DownloadPage? _downloadPage;
        private LibraryPage? _libraryPage;
        private OnlinePage? _onlinePage;
        private AiPage? _aiPage;
        private SettingsPage? _settingsPage;
        private MessagesPage? _messagesPage;
        private SkinsPage? _skinsPage;
        private DebugPage? _debugPage;
        private InstanceSettingsPanel? _instanceSettings;
        private bool _launching;
        private string _currentPage = "home";

        /// <summary>当前显示的页面键（home/download/library/…），供 AiPage 等判断是否发通知。</summary>
        internal string CurrentPage => _currentPage;
        private string _themeStamp = "";
        private bool? _badgeActive;
        private string? _pendingQuickPlay;

        /// <summary>顶栏拖动区的非客户区来源（系统标题栏已摘掉，窗口拖动靠它下发 Caption 区域）。</summary>
        private InputNonClientPointerSource? _nonClientSource;

        /// <summary>当前运行中的游戏进程（供消息页显示状态与强制关闭）。</summary>
        internal static Process? RunningGame;
        internal static string RunningGameVersion = "";
        internal static DateTime RunningGameStartedUtc;
        /// <summary>游戏主窗口是否已显示（false 时消息页卡片显示「启动中」）。</summary>
        internal static bool RunningGameWindowShown;
        /// <summary>游戏由用户在消息页手动关闭时置位，用于跳过崩溃分析。</summary>
        internal static bool GameClosedByUser;

        public MainWindow()
        {
            InitializeComponent();

            Title = "XO Launcher";
            _appWindow = AppWindow;
            _appWindow.Title = "XO Launcher";

            // 系统标题栏已摘掉（见下方 SetBorderAndTitleBar），最小化 / 最大化 / 关闭三枚按钮
            // 全部由 MainWindow.xaml 里第四个「岛」自绘。
            // IsMaximizable = true：既让自绘的最大化键可用，也放开「双击拖动区 / Win+↑ /
            // 拖到屏幕顶边」这三条系统贴靠路径 —— 它们本质上都是对同一个 Presenter 状态的操作。
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = true;
                // 兜底：窗口若被系统以最大化状态恢复，先还原再定尺寸，
                // 否则后面的 Resize 不生效（甚至抛 InvalidOperationException）。
                if (presenter.State == OverlappedPresenterState.Maximized)
                    presenter.Restore();

                // 先摘系统标题栏，再配下面的 AppWindowTitleBar：
                // 这一步有可能把 ExtendsContentIntoTitleBar 重置回 false，所以顺序放在前面。
                try
                {
                    presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
                }
                catch (Exception ex)
                {
                    LauncherLogService.Error("Window", "移除系统标题栏失败，退回系统标题栏", ex);
                }
            }
            // 状态变化（自绘按钮 / 系统贴靠 / 还原）统一走这里，负责换图标与内缩。
            // 注意订阅的是 **AppWindow.Changed** 而不是 Presenter 上的事件 ——
            // OverlappedPresenter 根本没有 Changed 事件（反射实测其事件集为空），
            // 状态要经 AppWindowChangedEventArgs.DidPresenterChange 上报。
            _appWindow.Changed += OnAppWindowChanged;

            _appWindow.Resize(new SizeInt32(1280, 800));
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonForegroundColor = Color.FromArgb(255, 200, 200, 200);
            _appWindow.TitleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 120, 120, 120);
            // 这里**故意不调用** SetTitleBar(TitleBarDrag)：它建立在 AppWindow 标题栏之上
            // （官方 Remarks：「A custom title bar uses an AppWindow title bar for its implementation」），
            // 标题栏被摘掉后它算不出拖动区 —— 表现就是「窗口拖不动」；而且它会吞掉该元素
            // 及其子元素的指针输入。改为一句话直接下发非客户区区域，见 InitializeDragRegion。
            InitializeDragRegion();
            SetWindowIcon();

            UiDispatch.Bind(DispatcherQueue);

            // 空闲休眠：5 分钟没有任何鼠标 / 键盘 / 窗口动作就收一次内存，动一下就立刻恢复。
            _idle = new IdleSleepService(DispatcherQueue)
            {
                // 正在启动游戏或有下载在跑时不休眠：那时暂停滚动、收内存都是帮倒忙
                ShouldSkipSleep = () => _launching || DownloadService.Instance.Tasks.Any(t => t.IsActive),
            };
            _idle.Slept += EnterIdleMode;
            _idle.Woken += ExitIdleMode;
            RootGrid.PreviewKeyDown += (_, _) => _idle.NoteActivity();
            RootGrid.PointerMoved += (_, _) => _idle.NoteActivity();
            RootGrid.PointerPressed += (_, _) => _idle.NoteActivity();
            RootGrid.PointerWheelChanged += (_, _) => _idle.NoteActivity();
            Activated += (_, _) => _idle.NoteActivity();

            // 全景层尺寸确定/窗口缩放后重排两张图并重启滚动动画
            PanoramaHost.SizeChanged += (_, _) =>
            {
                if (PanoramaHost.Visibility == Visibility.Visible)
                    StartPanoramaScroll();
            };

            LauncherSettings.Instance.Changed += () => DispatcherQueue.TryEnqueue(RefreshChrome);
            AccountStore.Instance.Changed += () => DispatcherQueue.TryEnqueue(RefreshChrome);
            DownloadService.Instance.TaskAdded += _ => UpdateMessagesBadge();
            DownloadService.Instance.TaskAdded += AutoSwitchToMessagesOnTaskAdded;
            DownloadService.Instance.Tasks.CollectionChanged += OnDownloadTasksChanged;
            foreach (var task in DownloadService.Instance.Tasks)
                task.PropertyChanged += OnDownloadTaskPropertyChanged;
            NotificationService.PageRequested += OnNotificationPageRequested;
            NotificationService.Initialize();

            RefreshChrome();
            ShowPage("home");
            UpdateMessagesBadge();
            MaybeShowWelcomeGate();
            _ = Task.Run(() =>
            {
                JavaRuntimeService.ScanAll();
                DispatcherQueue.TryEnqueue(RefreshChrome);
            });
            _ = CheckLauncherUpdateOnStartupAsync();
            Closed += (_, _) =>
            {
                TerracottaService.Instance.Shutdown();
                NotificationService.Shutdown();
            };
            // 调试页解锁计数挂在根的 PreviewKeyDown（tunneling）上：
            // 焦点在页面、输入框还是导航按钮上都会先经过这里，才算得上「任意界面」。
            RootGrid.PreviewKeyDown += OnGlobalPreviewKeyDown;
            RootGrid.Loaded += MainWindow_Loaded;
        }

        /// <summary>
        /// 开门遮罩：首次使用先走新手引导，版本升级后补更新说明，两者都走完（或跳过）才露出主界面。
        /// 遮罩不透明且盖在最上层，主界面在它后面照常初始化，只是看不见也点不到。
        /// </summary>
        private void MaybeShowWelcomeGate()
        {
            // 命令行/AI 驱动的运行不该被引导挡住：这类会话要的是直接执行意图
            if (App.PendingCli is not null) return;

            var settings = LauncherSettings.Instance;
            var pages = new List<WelcomePages.Page>();
            if (!settings.WelcomeDone) pages.AddRange(WelcomePages.Onboarding);
            pages.AddRange(WelcomePages.ReleaseNotesNewerThan(settings.SeenReleaseNotes));
            if (pages.Count == 0) return;

            ShowWelcomeGate(pages, persist: true);
        }

        /// <summary>
        /// 挂出引导遮罩。persist=false 用于调试页重看：不把「已看过」写回设置，
        /// 否则调试一次就把真正的首跑引导吃掉了。
        /// </summary>
        private void ShowWelcomeGate(IReadOnlyList<WelcomePages.Page> pages, bool persist)
        {
            var settings = LauncherSettings.Instance;
            var gate = new WelcomeGate(pages);
            gate.Finished += () =>
            {
                if (persist)
                {
                    settings.WelcomeDone = true;
                    settings.SeenReleaseNotes = LauncherUpdateService.CurrentVersion;
                }
                RootGrid.Children.Remove(gate);
            };
            Grid.SetRow(gate, 0);
            Grid.SetRowSpan(gate, 999);
            Grid.SetColumnSpan(gate, 999);
            RootGrid.Children.Add(gate);
        }

        // ==================== 调试页解锁：累计按下 20 个不同按键 ====================

        private const int DebugUnlockKeyCount = 20;
        private readonly HashSet<Windows.System.VirtualKey> _distinctKeysPressed = new();
        private bool _debugUnlocked;

        private void OnGlobalPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // 浮层开着时 Esc 先收起浮层，不参与调试解锁计数
            if (_accountPopoverOpen && e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseAccountPopover();
                e.Handled = true;
                return;
            }
            if (_debugUnlocked) return;
            // 只统计**不同**的键：连按同一个键 20 次不算解锁，避免误触长串数字就开出来
            _distinctKeysPressed.Add(e.Key);
            if (_distinctKeysPressed.Count < DebugUnlockKeyCount) return;
            UnlockDebugPage();
        }

        private void UnlockDebugPage()
        {
            _debugUnlocked = true;
            DebugNavHost.Visibility = Visibility.Visible;
            LauncherLogService.Info("Debug", $"调试页已解锁（累计 {DebugUnlockKeyCount} 个不同按键）");
            ShowPage("debug");
        }

        private void DebugNavButton_Click(object sender, RoutedEventArgs e) => ShowPage("debug");

        /// <summary>调试页用：每个页面的 id、名称、是否已实例化、是否正显示。</summary>
        public IReadOnlyList<(string Id, string Name, bool Created, bool Current)> DebugPageStates()
            => new (string, string, bool, bool)[]
            {
                ("home", "首页", true, _currentPage == "home"),
                ("download", "下载（版本 / 模组）", _downloadPage is not null, _currentPage == "download"),
                ("library", "版本库", _libraryPage is not null, _currentPage == "library"),
                ("online", "快捷联机", _onlinePage is not null, _currentPage == "online"),
                ("ai", "AI 助手", _aiPage is not null, _currentPage == "ai"),
                ("skin", "皮肤编辑器", _skinsPage is not null, _currentPage == "skin"),
                ("settings", "设置", _settingsPage is not null, _currentPage == "settings"),
                ("messages", "消息中心", _messagesPage is not null, _currentPage == "messages"),
                ("debug", "调试信息", _debugPage is not null, _currentPage == "debug"),
            };

        /// <summary>调试页用的跳转入口。只在解锁后的调试页里可达，行为与点导航按钮一致。</summary>
        public void DebugNavigate(string page) => ShowPage(page);

        /// <summary>调试页用：重新弹出新手引导（带更新说明）或只弹更新说明，不写回「已看过」。</summary>
        public void DebugShowWelcomeGate(bool withOnboarding)
        {
            var pages = new List<WelcomePages.Page>();
            if (withOnboarding) pages.AddRange(WelcomePages.Onboarding);
            pages.AddRange(WelcomePages.ReleaseNotesNewerThan(null));
            if (pages.Count > 0) ShowWelcomeGate(pages, persist: false);
        }

        /// <summary>调试页用：走一遍真实的「检查更新 → 更新弹窗」链路，并给一行结果文本。</summary>
        public async Task<string> DebugCheckUpdateAsync()
        {
            var info = await LauncherUpdateService.CheckAsync();
            // 顺带把结果喂给设置页的更新卡，和启动时检查后的表现保持一致
            _settingsPage?.ShowUpdateResult(info);
            if (info.IsNewer) await PromptLauncherUpdateAsync(info);
            return "当前 " + LauncherUpdateService.CurrentVersion + " · 最新 " + info.Version + " · "
                   + (info.IsNewer
                        ? info.ForceUpdate ? "有更新（强制），已弹更新窗口" : "有更新，已弹更新窗口"
                        : "无更新（未弹窗）");
        }

        private bool _cliApplied;

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_cliApplied) return;
            _cliApplied = true;
            var req = App.PendingCli;
            if (req is null) return;
            ApplyCliIntent(req);
        }

        private TaskCompletionSource<string>? _aiLaunchTcs;

        /// <summary>
        /// 供 AI 调用：真正启动游戏并等待「启动结果」——游戏窗口出现即判成功；进程早退 / 崩溃则回传退出码与故障分析。
        /// 不等待整局游戏结束；超过 <paramref name="timeoutMs"/> 仍未确认则返回超时提示。结果只回灌一次。
        /// </summary>
        internal Task<string> LaunchGameForAiAsync(CliRequest req, System.Threading.CancellationToken ct = default, int timeoutMs = 240000)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _aiLaunchTcs = tcs;
            ct.Register(() => CompleteAiLaunch("启动已被取消。"));
            _ = Task.Delay(timeoutMs).ContinueWith(
                _ => CompleteAiLaunch("启动超时：仍未确认游戏是否成功启动（可能仍在加载，或已卡住/无响应）。可让用户查看「消息」页的最近一次故障分析。"),
                TaskScheduler.Default);
            ApplyCliIntent(req);
            return tcs.Task;
        }

        /// <summary>把启动结果回灌给等待中的 AI（只生效一次；非 AI 启动时为 no-op）。</summary>
        private void CompleteAiLaunch(string result)
        {
            var tcs = System.Threading.Interlocked.Exchange(ref _aiLaunchTcs, null);
            tcs?.TrySetResult(result);
        }

        private static string BuildAiLaunchFailureText(string versionId, int exitCode, GameCrashInfo? crash)
        {
            if (crash is null)
                return $"游戏进程已退出（版本 {versionId}，退出码 {exitCode}），未检测到崩溃。若这是刚启动就退出，通常意味着启动失败，请结合日志排查。";
            var text = $"启动/运行异常（版本 {versionId}）：{crash.Kind}，退出码 {exitCode}。";
            if (!string.IsNullOrWhiteSpace(crash.Summary))
                text += " 摘要：" + crash.Summary;
            return text;
        }

        internal void ApplyCliIntent(CliRequest req)
        {
            switch (req.Command)
            {
                case CliCommand.Open:
                    ShowPage(NormalizePage(req.Arg));
                    break;
                case CliCommand.Launch:
                    CliLaunch(req);
                    break;
            }
        }

        private void CliLaunch(CliRequest req)
        {
            var s = LauncherSettings.Instance;
            if (!string.IsNullOrWhiteSpace(req.Arg)) s.SelectedVersion = req.Arg!;
            var user = req.GetOption("user");
            if (!string.IsNullOrWhiteSpace(user)) s.Username = user!;
            var memory = req.GetOption("memory") ?? req.GetOption("mem");
            if (long.TryParse(memory, out var mv) && mv > 0) s.MemoryMB = mv;
            var java = req.GetOption("java");
            if (!string.IsNullOrWhiteSpace(java)) s.JavaPath = java!;
            ShowPage("home");
            PlayButton_Click(this, new RoutedEventArgs());
        }

        private static string NormalizePage(string? arg) => (arg ?? "home").ToLowerInvariant() switch
        {
            "download" or "downloads" => "download",
            "library" or "libraries" or "versions" => "library",
            "online" or "multiplayer" => "online",
            "ai" or "assistant" => "ai",
            "skin" or "skins" => "skin",
            "settings" or "setting" or "config" => "settings",
            "messages" or "message" or "tasks" => "messages",
            _ => "home"
        };

        private async Task CheckLauncherUpdateOnStartupAsync()
        {
            if (!LauncherSettings.Instance.AutoCheckUpdates) return;
            try
            {
                await Task.Delay(2000);
                var info = await LauncherUpdateService.CheckAsync();
                if (!info.IsNewer) return;
                DispatcherQueue.TryEnqueue(() => _ = PromptLauncherUpdateAsync(info));
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Update", "启动时检查更新失败", ex);
            }
        }

        internal async Task PromptLauncherUpdateAsync(LauncherUpdateInfo info)
        {
            _settingsPage?.ShowUpdateResult(info);
            var notes = string.IsNullOrWhiteSpace(info.Notes)
                ? "当前版本 " + LauncherUpdateService.CurrentVersion + "，可以下载更新。"
                : info.Notes.Length > 400 ? info.Notes[..400] + "…" : info.Notes;
            if (!string.IsNullOrWhiteSpace(info.ReleaseDate))
                notes = "发布日期 " + info.ReleaseDate + "\n\n" + notes;
            var result = await AppDialog.ShowAsync(
                RootGrid.XamlRoot,
                (info.ForceUpdate ? "需要更新到 " : "发现新版本 ") + info.Version,
                info.ForceUpdate ? "此版本为强制更新，请先完成升级。\n\n" + notes : notes,
                primary: info.CanDownload ? "下载更新" : "打开发布页",
                // 强制更新时不提供「稍后」，只能立即升级。
                close: info.ForceUpdate ? null : "稍后");
            if (result != AppDialogResult.Primary)
                return;

            if (!info.CanDownload)
            {
                if (Uri.TryCreate(info.PageUrl, UriKind.Absolute, out var uri))
                    await Windows.System.Launcher.LaunchUriAsync(uri);
                return;
            }

            var task = DownloadService.Instance.StartInstall("启动器更新 " + info.Version, info.FileName);
            try
            {
                await LauncherUpdateService.PrepareAsync(info, task, task.Token);
                var done = await AppDialog.ShowAsync(
                    RootGrid.XamlRoot,
                    "更新已下载",
                    "是否立即安装并重启启动器？",
                    primary: "安装并重启",
                    close: "稍后");
                if (done != AppDialogResult.Primary)
                    return;
                if (LauncherUpdateService.TryLaunchSetupInstead())
                {
                    Close();
                    return;
                }
                LauncherUpdateService.ApplyAndRestart();
                Close();
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Update", "下载或安装更新失败", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Detail = ex.Message;
                });
            }
        }

        private void SetWindowIcon()
        {
            try
            {
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
                var light = ThemeService.IsLight;
                var icoName = light ? "theme_light.ico" : "theme_dark.ico";
                var pngName = light ? "theme_light.png" : "theme_dark.png";

                var iconPath = Path.Combine(assets, icoName);
                if (!File.Exists(iconPath))
                    iconPath = Path.Combine(assets, "XO_Launcher.ico");
                if (File.Exists(iconPath))
                    _appWindow.SetIcon(iconPath);

                var pngPath = Path.Combine(assets, pngName);
                if (!File.Exists(pngPath))
                    pngPath = Path.Combine(assets, "xo_home.png");
                if (File.Exists(pngPath) && AppIconImage is not null)
                    AppIconImage.Source = new BitmapImage(new Uri(pngPath)) { DecodePixelWidth = 24, DecodePixelHeight = 24 };
            }
            catch (Exception)
            {
                // 图标文件缺失时使用系统默认图标
            }
        }

        private void HomeNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("home");
            RefreshChrome();
        }

        private void DownloadNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("download");
        }

        private void LibraryNavButton_Click(object sender, RoutedEventArgs e) => OpenLibraryPage();

        /// <summary>打开资源库页面。版本设置里的「前往资源库」也走这里。</summary>
        internal void OpenLibraryPage()
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("library");
            _libraryPage?.Refresh();
        }

        private void OnlineNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("online");
            _onlinePage?.Refresh();
        }

        private void AiNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("ai");
            _aiPage?.RefreshBanner();
        }

        private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("settings");
        }

        private void SkinNavButton_Click(object sender, RoutedEventArgs e) => OpenSkinsPage();

        /// <summary>打开独立皮肤页。设置页账号卡里的「前往皮肤页」也走这里。</summary>
        internal void OpenSkinsPage()
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("skin");
            _skinsPage?.EnsureReady();
        }

        internal void OpenAiAssistant(string displayPrompt, string? apiPrompt = null, bool autoSend = false)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("ai");
            _aiPage?.RefreshBanner();
            _aiPage?.EnqueuePrompt(displayPrompt, apiPrompt, autoSend);
        }

        internal void OpenAiSettings()
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("settings");
            _settingsPage?.FocusAi();
        }

        private void MessagesNavButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage("messages");
            _messagesPage?.RefreshSessionCrash();
            _messagesPage?.RefreshGameStatus();
            UpdateMessagesBadge();
        }

        private T EnsurePage<T>(ref T? field, Grid host) where T : UserControl, new()
        {
            if (field is not null) return field;
            field = new T();
            host.Children.Add(field);
            return field;
        }

        private void ShowPage(string page)
        {
            switch (page)
            {
                case "download":
                    EnsurePage(ref _downloadPage, DownloadHost).EnsureLoaded();
                    break;
                case "library":
                    EnsurePage(ref _libraryPage, LibraryHost);
                    break;
                case "online":
                    EnsurePage(ref _onlinePage, OnlineHost);
                    break;
                case "ai":
                    EnsurePage(ref _aiPage, AiHost);
                    break;
                case "skin":
                    EnsurePage(ref _skinsPage, SkinHost).EnsureReady();
                    break;
                case "settings":
                    EnsurePage(ref _settingsPage, SettingsHost).EnsureReady();
                    break;
                case "messages":
                    EnsurePage(ref _messagesPage, MessagesHost);
                    break;
                case "debug":
                    EnsurePage(ref _debugPage, DebugHost);
                    break;
            }
            _currentPage = page;
            HomeView.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
            DownloadHost.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
            LibraryHost.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
            OnlineHost.Visibility = page == "online" ? Visibility.Visible : Visibility.Collapsed;
            AiHost.Visibility = page == "ai" ? Visibility.Visible : Visibility.Collapsed;
            SkinHost.Visibility = page == "skin" ? Visibility.Visible : Visibility.Collapsed;
            SettingsHost.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            MessagesHost.Visibility = page == "messages" ? Visibility.Visible : Visibility.Collapsed;
            DebugHost.Visibility = page == "debug" ? Visibility.Visible : Visibility.Collapsed;

            HomeIndicator.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
            DownloadIndicator.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
            LibraryIndicator.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
            OnlineIndicator.Visibility = page == "online" ? Visibility.Visible : Visibility.Collapsed;
            AiIndicator.Visibility = page == "ai" ? Visibility.Visible : Visibility.Collapsed;
            SkinIndicator.Visibility = page == "skin" ? Visibility.Visible : Visibility.Collapsed;
            SettingsIndicator.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            MessagesIndicator.Visibility = page == "messages" ? Visibility.Visible : Visibility.Collapsed;
            DebugIndicator.Visibility = page == "debug" ? Visibility.Visible : Visibility.Collapsed;

            var primary = ThemeService.Brush("TextPrimaryBrush");
            var secondary = ThemeService.Brush("TextSecondaryBrush");

            SetNavButton(HomeNavButton, page == "home", primary, secondary);
            SetNavButton(DownloadNavButton, page == "download", primary, secondary);
            SetNavButton(LibraryNavButton, page == "library", primary, secondary);
            SetNavButton(OnlineNavButton, page == "online", primary, secondary);
            SetNavButton(AiNavButton, page == "ai", primary, secondary);
            SetNavButton(SkinNavButton, page == "skin", primary, secondary);
            SetNavButton(SettingsNavButton, page == "settings", primary, secondary);
            SetNavButton(MessagesNavButton, page == "messages", primary, secondary);
            SetNavButton(DebugNavButton, page == "debug", primary, secondary);

        }

        private static void SetNavButton(Button button, bool active, Brush primary, Brush secondary)
        {
            button.Foreground = ThemeService.Brush("GhostButtonForeground");
            button.FontWeight = active ? new FontWeight { Weight = 600 } : new FontWeight { Weight = 400 };
        }

        private void RefreshChrome()
        {
            ApplyTheme();
            RefreshPanoramaBackground();
            var settings = LauncherSettings.Instance;
            var launch = GameInstanceLayout.ResolveLaunch(settings.SelectedVersion);
            UpdateAccountChrome();
            ApplyBottomBarVisibility();
            GameDirectoryText.Text = settings.GameDirectory;

            var installed = InstalledVersionService.Find(settings.SelectedVersion);
            if (installed?.IsBedrock == true)
            {
                BottomMemoryText.Text = installed.LoaderId == "bedrock-server" ? "基岩服务器" : "基岩版";
                JavaVersionText.Text = installed.LoaderId == "bedrock-server"
                    ? "专用服务器"
                    : BedrockIds.IsSideloadClient(installed.Id) ? "本机注册" : "Microsoft Store";
            }
            else
            {
                BottomMemoryText.Text = launch.Independent
                    ? $"{launch.MemoryMB} MB · 本版本"
                    : $"{launch.MemoryMB} MB";
                JavaVersionText.Text = FormatJavaLabel(launch);
            }

            UpdateHomeVersionIcon(installed);
            if (installed is null)
            {
                var first = InstalledVersionService.Scan().FirstOrDefault();
                if (first is not null && !string.Equals(settings.SelectedVersion, first.Id, StringComparison.Ordinal))
                {
                    settings.SelectedVersion = first.Id;
                    return;
                }
                VersionText.Text = "未安装版本";
                VersionDescText.Text = "请先前往「下载」安装游戏版本";
                PlayButton.IsEnabled = !_launching;
                InstanceSettingsButton.IsEnabled = false;
                return;
            }

            VersionText.Text = installed.DisplayName;
            VersionDescText.Text = installed.IsBedrock
                ? installed.LoaderLabel
                : installed.LoaderId == "vanilla"
                    ? "原版 Minecraft"
                    : $"{installed.MinecraftVersion}  +  {installed.LoaderLabel}";
            PlayButton.IsEnabled = !_launching;
            InstanceSettingsButton.IsEnabled = !_launching;
        }

        /// <summary>按版本类型 / 自定义图标刷新主页的版本图标。</summary>
        private void UpdateHomeVersionIcon(InstalledVersion? installed)
        {
            string? custom = null;
            string? instanceDir = null;
            if (installed is not null)
            {
                instanceDir = GameInstanceLayout.GetDirectory(LauncherSettings.Instance.GameDirectory, installed.Id);
                custom = GameInstanceLayout.ReadMeta(instanceDir)?.CustomIcon;
            }
            var path = VersionIconService.GetIconPath(
                custom, installed?.LoaderId ?? "vanilla", installed?.Type ?? "release", instanceDir);
            var image = RemoteIconLoader.TryCreateFromFile(path, 56);
            if (image is not null)
            {
                HomeVersionIcon.Source = image;
                HomeVersionIcon.Visibility = Visibility.Visible;
                HomeVersionGlyph.Visibility = Visibility.Collapsed;
                return;
            }
            HomeVersionIcon.Source = null;
            HomeVersionIcon.Visibility = Visibility.Collapsed;
            HomeVersionGlyph.Visibility = Visibility.Visible;
        }

        private void ApplyTheme()
        {
            var stamp = $"{ThemeService.IsLight}|{LauncherSettings.Instance.AccentColor}|{LauncherSettings.Instance.UiFont}";
            if (stamp == _themeStamp && !string.IsNullOrEmpty(_themeStamp))
                return;

            // 启动路径（ctor → RefreshChrome → ApplyTheme）跑在首帧渲染之前。
            // 这里抛出去的任何异常都会撞上 XAML 生成器挂的 UnhandledException 处理器，
            // 被 Debugger.Break() 拦下 → 表现为「启动即卡死」。主题是纯外观，
            // 失败绝不该拦住启动，所以整段兜住。（诊断见 ThemeService.WriteResource）
            try
            {
                ThemeService.Apply(RootGrid);
                // 每个主题字典里的 AcrylicBrush 是不同实例，换主题后要按当前浓度重刷一次
                ThemeService.RefreshCardFrostTint();
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("App", "应用主题失败", ex);
                return;
            }

            var light = ThemeService.IsLight;
            _appWindow.TitleBar.ButtonForegroundColor = light
                ? Color.FromArgb(255, 40, 40, 40)
                : Color.FromArgb(255, 200, 200, 200);
            _appWindow.TitleBar.ButtonInactiveForegroundColor = light
                ? Color.FromArgb(255, 120, 120, 120)
                : Color.FromArgb(255, 120, 120, 120);
            _appWindow.TitleBar.ButtonHoverForegroundColor = light
                ? Color.FromArgb(255, 20, 20, 20)
                : Color.FromArgb(255, 255, 255, 255);
            _themeStamp = stamp;
            SetWindowIcon();
        }

        internal void RequestLaunch(string? quickPlayServer = null)
        {
            _pendingQuickPlay = quickPlayServer;
            CloseInstanceSettings(refresh: false);
            ShowPage("home");
            PlayButton_Click(PlayButton, new RoutedEventArgs());
        }

        /// <summary>
        /// 把顶栏的两块拖动区（<c>TitleBarDragLeft</c> / <c>TitleBarDrag</c>）注册成非客户区
        /// 「标题栏」区域，让窗口能靠拖它们来移动。
        /// </summary>
        /// <remarks>
        /// 为什么不能继续用 Window.SetTitleBar：它是**建立在 AppWindow 标题栏之上**的
        /// （官方 Remarks：「A custom title bar uses an AppWindow title bar for its implementation」），
        /// 而系统标题栏已被 SetBorderAndTitleBar(hasBorder:true, hasTitleBar:false) 摘掉，
        /// 底座没了就计算不出拖动区 —— 表现就是「窗口拖不动」；而且它还会吞掉该元素及其子元素的指针输入。
        /// 微软官方（microsoft-ui-xaml#8705）对「要移除系统按钮」给出的出口正是
        /// 「roll out own custom titlebar but use InputNonClientPointerSource to do NC heavy lifting」。
        ///
        /// 关键性质：SetRegionRects 是**纯几何**的 —— 落在矩形内的点变成拖动，矩形之外仍是
        /// 普通客户区。所以四个「岛」天然在矩形之外，里面的按钮照常收点击，不需要额外打洞。
        /// 坐标是**物理像素**、相对窗口客户区左上角，必须乘 RasterizationScale。
        /// 重算挂在**元素**的 Loaded / SizeChanged 上（官方明确要求，别挂 AppWindow.Changed：
        /// 最大化/最小化时后者会早于元素重排触发，算出来的值是错的）；跨屏改缩放比例走 XamlRoot.Changed。
        ///
        /// 自页面岛居中后拖动区被拆成左右两块，**必须在同一次调用里一起下发**：
        /// SetRegionRects 是覆盖语义，分两次调用会互相抹掉，只剩最后一块能拖。
        /// </remarks>
        private void InitializeDragRegion()
        {
            try
            {
                _nonClientSource = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Window", "取 InputNonClientPointerSource 失败，顶栏将无法拖动窗口", ex);
                return;
            }

            TitleBarDrag.Loaded += (_, _) =>
            {
                // XamlRoot 要到 Loaded 之后才非空，DPI 变化的订阅放在这里挂。
                if (TitleBarDrag.XamlRoot is { } root)
                    root.Changed += (_, _) => UpdateDragRegion();
                UpdateDragRegion();
                // 保险：WinUI 自己的布局流程里可能又按它的算法注册过一次 caption 区域，
                // 等它跑完再补设一次（SetRegionRects 是覆盖而不是追加）。
                DispatcherQueue.TryEnqueue(UpdateDragRegion);
            };
            TitleBarDrag.SizeChanged += (_, _) => UpdateDragRegion();

            // 双击拖动区 = 最大化 / 还原。两块拖动区都要挂。
            TitleBarDragLeft.DoubleTapped += DragArea_DoubleTapped;
            TitleBarDrag.DoubleTapped += DragArea_DoubleTapped;
        }

        /// <summary>把两块拖动区的位置与尺寸换算成物理像素，一次性下发给非客户区的 Caption 区域。</summary>
        private void UpdateDragRegion()
        {
            if (_nonClientSource is null) return;
            if (TitleBarDrag.XamlRoot is not { } root) return;

            // DIP -> 物理像素。TransformToVisual(null) 得到相对 XAML 根（= 客户区左上角）的坐标。
            // SetRegionRects 要的是数组（WinRT 签名），List<T> 不隐式转换，所以先收进 List 再 ToArray。
            var scale = root.RasterizationScale;
            var rects = new List<RectInt32>(2);
            foreach (var area in new[] { TitleBarDragLeft, TitleBarDrag })
            {
                if (area.ActualWidth <= 0 || area.ActualHeight <= 0) continue;
                var bounds = area.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, area.ActualWidth, area.ActualHeight));
                rects.Add(new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale)));
            }
            if (rects.Count == 0) return;

            try
            {
                _nonClientSource.SetRegionRects(NonClientRegionKind.Caption, rects.ToArray());
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Window", "设置顶栏拖动区失败", ex);
            }
        }

        /// <summary>顶栏第四个岛里的「最小化」。</summary>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Minimize();
        }

        /// <summary>顶栏第四个岛里的「最大化 / 还原」。同一条状态路径，由系统决定当前该往哪走。</summary>
        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        /// <summary>
        /// 拖动区双击 = 最大化 / 还原。系统对自绘标题栏不会自动接管这个手势，
        /// 而它和拖动一样属于「标题栏该有的行为」，所以由自绘方补上。
        /// </summary>
        private void DragArea_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            if (_appWindow.Presenter is not OverlappedPresenter presenter) return;
            if (presenter.State == OverlappedPresenterState.Maximized)
                presenter.Restore();
            else if (presenter.State == OverlappedPresenterState.Restored)
                presenter.Maximize();
        }

        /// <summary>
        /// 窗口状态变化回调，只处理与 Presenter 状态有关的那一类变化。
        /// </summary>
        /// <remarks>
        /// <c>AppWindow.Changed</c> 触发得比 XAML 重排更早，所以这里只更新「图标 + 内缩」这类
        /// 不依赖布局的视觉状态，拖动区矩形交给 <c>DispatcherQueue.TryEnqueue</c> 推到布局之后重算。
        /// </remarks>
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPresenterChange) return;
            DispatcherQueue.TryEnqueue(RefreshMaximizeVisuals);
        }

        /// <summary>按 Presenter 当前状态刷新「最大化 / 还原」的图标、提示文案与客户区内缩。</summary>
        private void RefreshMaximizeVisuals()
        {
            var maximized = (_appWindow.Presenter as OverlappedPresenter)?.State
                == OverlappedPresenterState.Maximized;
            ApplyMaximizeInset(maximized);
            // 还原态用「叠两层方框」的字形，与「最大化」的单层方框区分开，跟系统按钮一致。
            // 码位经 fontTools 从 Assets\Fonts\FluentSystemIcons-Filled.ttf 的 cmap 实测确认：
            //   ic_fluent_square_20_filled          = U+EB7E
            //   ic_fluent_square_multiple_20_filled = U+EB9F
            // 别按 Segoe MDL2 的 E922/E923 去猜 —— 在这套字体里它们分别是 people_team 系列，
            // 码位猜错不会报错，只会静默画出完全无关的图标。
            MaximizeGlyph.Glyph = maximized ? "\uEB9F" : "\uEB7E";
            MaximizeButton.SetValue(ToolTipService.ToolTipProperty, maximized ? "还原" : "最大化");
            // 客户区被内缩后拖拽区随之位移/变窄，拖动区矩形要重算。
            UpdateDragRegion();
        }

        /// <summary>
        /// 最大化时把整个客户区从工作区边缘让开 <see cref="MaximizedInset"/> 像素。
        /// </summary>
        /// <remarks>
        /// SetBorderAndTitleBar(hasBorder: true, ...) 让窗口保留了边框式边框。这种窗口最大化后，
        /// 系统会把 8px 的标题栏绘制区叠在客户区外圈之上 —— 不内缩的话，顶栏四个「岛」与底栏
        /// 都会贴到屏幕边缘被盖住一截。这条内缩只跟「窗口最大化」有关，与外框状态无关，
        /// 所以不必去判断 AppWindowTitleBar.IsCustomizationSupported。
        /// </remarks>
        private const double MaximizedInset = 8;

        private void ApplyMaximizeInset(bool maximized)
        {
            var inset = maximized ? MaximizedInset : 0;
            if (RootGrid.Padding.Left == inset && RootGrid.Padding.Top == inset) return;
            RootGrid.Padding = new Thickness(inset, inset, inset, inset);
        }

        /// <summary>顶栏第四个岛里的「关闭」。</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ==================== 全景图背景（开关驱动，启动游戏时强制显示） ====================

        private string? _panoramaVersion;
        private bool _panoramaLoaded;   // 已成功加载全景位图
        private bool _panoramaVisible;  // 当前是否已展示（透明卡片样式已应用）
        private int _panoramaFaces = 1; // 环带由几张方形侧面拼成（宽高比）；1 = 退回单面
        private bool _instanceSettingsOpen;
        private IdleSleepService? _idle;
        private Storyboard? _panoramaScroll;
        private bool _panoramaScrollActive;
        private double _panoramaScrollWidth;
        private double _panoramaScrollHeight;
        private double _panoramaScrollSeconds;
        // 环带分段亮度 + 实时色调：背景在滚，可见那一段的明暗就在变
        private float[] _panoramaLuminance = Array.Empty<float>();
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panoramaToneTimer;
        private bool _panoramaToneBright;

        /// <summary>设置或选中版本变化后刷新全景背景（RefreshChrome 调用）。</summary>
        private void RefreshPanoramaBackground()
        {
            var settings = LauncherSettings.Instance;
            if (_panoramaLoaded
                && string.Equals(_panoramaVersion, settings.SelectedVersion, StringComparison.Ordinal))
            {
                ApplyCardFrostAmount(settings.PanoramaBlurAmount);
                ApplyPanoramaVisibility();
                return;
            }
            _ = ApplyPanoramaAsync(settings.SelectedVersion, settings.GameDirectory);
        }

        private async Task ApplyPanoramaAsync(string versionId, string gameDirectory)
        {
            PanoramaService.PanoramaImage? image = null;
            try
            {
                var instanceDir = GameInstanceLayout.GetDirectory(gameDirectory, versionId);
                image = await Task.Run(() => PanoramaService.GetPanoramaAsync(gameDirectory, instanceDir, versionId));
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Theme", $"加载版本全景图失败：{versionId}", ex);
            }

            if (image is null)
            {
                LauncherLogService.Info("Theme", $"版本 {versionId} 没有可用的全景图，保持纯色背景");
                _panoramaLoaded = false;
                _panoramaVersion = null;
                ApplyPanoramaVisibility();
                return;
            }

            var bitmap = new BitmapImage { UriSource = new Uri(image.Path) };
            PanoramaImageA.Source = bitmap;
            PanoramaImageB.Source = bitmap;
            _panoramaFaces = image.Faces;
            _panoramaLoaded = true;
            _panoramaVersion = versionId;
            // 分段亮度在解图时一起算好（已缓存，这里只是取出来），滚动时按可见段实时取用
            _panoramaLuminance = await Task.Run(() => PanoramaService.GetLuminanceProfileAsync(image.Path));
            // 首帧用整条环带的均值定基调，之后交给实时跟踪慢慢修正。
            // 阈值按本机实测分布定的：白昼全景（FPS Modpack 26.1.2）约 0.48，
            // 黄昏 / 夜景（1.20.1 樱花 0.23、26.x 0.35、Sodium Plus 0.07）都在下面。
            _panoramaToneBright = image.Luminance >= 0.45;
            ThemeService.SetPanoramaTone(_panoramaToneBright);
            ApplyCardFrostAmount(LauncherSettings.Instance.PanoramaBlurAmount);
            ApplyPanoramaVisibility();
        }

        /// <summary>
        /// 休眠动作：停掉全景滚动动画（CPU/GPU 空转）、把 3D 预览的 WebView2 内存目标压到低档、
        /// 丢掉可重取的加载器元数据缓存，最后来一次激进 GC 连大对象堆一起压。
        /// 界面保持原样，只是不再空转；唤醒后逐项还原。
        /// </summary>
        private void EnterIdleMode()
        {
            _panoramaScroll?.Pause();
            _skinsPage?.SetMemoryTarget(true);
            XO_Launcher.Api.LoaderMetaApi.ClearCache();
            ModNameTranslationService.TrimSearchIndex();
            JavaRuntimeService.InvalidateScanCache();
            BedrockEditionService.InvalidatePackageCache();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            LauncherLogService.Info("App", "进入空闲休眠：已暂停全景滚动、压低 3D 预览内存目标并回收内存");
        }

        /// <summary>
        /// 唤醒：恢复 WebView2 内存目标与滚动。
        /// 这里必须用 Resume 而不是 StartPanoramaScroll —— 后者在「尺寸与速度都没变」时会
        /// 直接早退以保持进度，结果动画会一直停在暂停态。
        /// </summary>
        private void ExitIdleMode()
        {
            _skinsPage?.SetMemoryTarget(false);
            if (_panoramaVisible && _panoramaScroll is { } storyboard) storyboard.Resume();
        }

        /// <summary>
        /// 全景是否展示由三方共同决定：版本设置页打开时始终隐藏（优先级最高）；
        /// 启动游戏期间强制显示（无视开关）；其余时候由「全景图背景」开关决定。
        /// 未加载到全景图时永远不展示。
        /// </summary>
        private void ApplyPanoramaVisibility()
        {
            var settings = LauncherSettings.Instance;
            var show = _panoramaLoaded
                       && !_instanceSettingsOpen
                       && (_launching || settings.PanoramaBackground);

            if (show)
            {
                PanoramaHost.Visibility = Visibility.Visible;
                if (!_panoramaVisible)
                {
                    ThemeService.ApplyPanoramaStyle(true);
                    _panoramaVisible = true;
                }
                // 卡片底色本身就是 WinUI 原生亚克力：开全景时把浓度调到设置值透出并模糊身后的
                // 全景，关全景时恢复实心。不再需要额外挂一层磨砂视觉。
                StartPanoramaScroll();
                StartToneTracking();
            }
            else
            {
                StopToneTracking();
                PanoramaHost.Visibility = Visibility.Collapsed;
                if (_panoramaVisible)
                {
                    ThemeService.ApplyPanoramaStyle(false);
                    _panoramaVisible = false;
                }
                _panoramaScroll?.Pause();
                _panoramaScrollActive = false;
            }

        }

        /// <summary>
        /// 实时色调跟踪：背景每滚过一段，可见那几段的平均亮度就变一次，
        /// 文字与卡片底色要跟着翻，否则「白昼那面」上就是白字糊在白底上。
        /// </summary>
        private void StartToneTracking()
        {
            if (_panoramaToneTimer is not null) return;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(400);
            timer.IsRepeating = true;
            timer.Tick += (_, _) => UpdateLiveTone();
            _panoramaToneTimer = timer;
            timer.Start();
        }

        private void StopToneTracking()
        {
            if (_panoramaToneTimer is null) return;
            _panoramaToneTimer.Stop();
            _panoramaToneTimer = null;
        }

        private void UpdateLiveTone()
        {
            var profile = _panoramaLuminance;
            var stripWidth = PanoramaImageA.Width;
            if (profile.Length == 0 || stripWidth <= 0 || PanoramaHost.ActualWidth <= 0) return;

            // 动画把画布往左推（X 为负），取模折回环带坐标，再换算成可见的那几段
            var offset = ((-PanoramaShift.X % stripWidth) + stripWidth) % stripWidth;
            var start = (int)(offset / stripWidth * profile.Length) % profile.Length;
            var visible = Math.Clamp(
                (int)Math.Round(PanoramaHost.ActualWidth / stripWidth * profile.Length), 1, profile.Length);

            double sum = 0;
            for (var i = 0; i < visible; i++)
                sum += profile[(start + i) % profile.Length];
            var luminance = sum / visible;

            // 迟滞：≥0.50 才翻亮底、≤0.42 才翻回暗底，中间维持原状，免得在临界亮度来回闪
            var bright = _panoramaToneBright ? luminance >= 0.42 : luminance >= 0.50;
            if (bright == _panoramaToneBright) return;
            _panoramaToneBright = bright;
            ThemeService.SetPanoramaTone(bright);
        }

        /// <summary>
        /// 卡片毛玻璃浓度：设置滑杆的 0–40 映射到亚克力 TintOpacity 0.08–0.55。
        /// 原生亚克力的模糊半径由系统定死，能调的只有着色浓度；整段区间都留在半透明侧，
        /// 因为要的就是「通透」，拉到最大也只是略实一些。
        /// </summary>
        private static void ApplyCardFrostAmount(double amount)
            => ThemeService.SetCardFrostTint(0.08 + Math.Clamp(amount, 0, 40) / 40 * 0.47);

        /// <summary>两张全景环带首尾相接，整体左移一张宽度后无缝循环，实现缓慢横向滚动。</summary>
        private void StartPanoramaScroll()
        {
            var width = PanoramaHost.ActualWidth;
            var height = PanoramaHost.ActualHeight;
            if (width <= 0 || height <= 0) return;

            var seconds = PanoramaScrollSeconds;

            // 一张侧面 = 游戏里 90° 视野。让它正好铺满窗口宽度，取景就和单张时一致：
            // 若改成把整条 4:1 环带压进窗口高度，同屏会露出约 144° 视野，画面发「广角」。
            // 环带因此比窗口高，垂直居中后靠 PanoramaHost.Clip 裁掉上下溢出。
            var stripWidth = width * Math.Max(1, _panoramaFaces);
            var stripHeight = width;
            var top = (height - stripHeight) / 2;

            // 已在滚动、且尺寸与速度都没变：保持当前进度，绝不从头重播
            // （否则每次改设置/调模糊度都会触发 RefreshPanoramaBackground，全景图会不停回到起点）。
            if (_panoramaScrollActive
                && Math.Abs(stripWidth - _panoramaScrollWidth) < 0.5
                && Math.Abs(height - _panoramaScrollHeight) < 0.5
                && Math.Abs(seconds - _panoramaScrollSeconds) < 0.01)
                return;

            _panoramaScroll?.Stop();

            PanoramaHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };

            PanoramaImageA.Width = PanoramaImageB.Width = stripWidth;
            PanoramaImageA.Height = PanoramaImageB.Height = stripHeight;
            Canvas.SetLeft(PanoramaImageA, 0);
            Canvas.SetLeft(PanoramaImageB, stripWidth);
            Canvas.SetTop(PanoramaImageA, top);
            Canvas.SetTop(PanoramaImageB, top);

            var animation = new DoubleAnimation
            {
                From = 0,
                To = -stripWidth,
                Duration = TimeSpan.FromSeconds(seconds),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, PanoramaCanvas);
            Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(TranslateTransform.X)");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            _panoramaScroll = storyboard;
            _panoramaScrollActive = true;
            _panoramaScrollWidth = stripWidth;
            _panoramaScrollHeight = height;
            _panoramaScrollSeconds = seconds;
            storyboard.Begin();
        }

        /// <summary>环带滚完一圈（360°）所需秒数；设置页调的是速度档位，这里换算。</summary>
        private static double PanoramaScrollSeconds
            => LauncherSettings.SpeedToLoopSeconds(LauncherSettings.Instance.PanoramaScrollSpeed);

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_launching)
            {
                CompleteAiLaunch("当前已有启动或游戏进程在进行中，请等它结束或关闭后再试。");
                return;
            }

            var settings = LauncherSettings.Instance;
            var installed = InstalledVersionService.Find(settings.SelectedVersion);
            if (installed is null)
            {
                LaunchStatusText.Text = "尚未安装游戏版本，请先前往「下载」安装";
                ShowPage("download");
                CompleteAiLaunch("启动失败：尚未安装/选中有效的游戏版本。请先用 list_versions 查看，或用 install_version 安装。");
                return;
            }

            if (installed.IsBedrock)
            {
                CompleteAiLaunch($"已触发基岩版「{installed.DisplayName}」启动（基岩版不通过本流程返回同步启动结果）。");
                await LaunchBedrockAsync(installed);
                return;
            }

            var launch = GameInstanceLayout.ResolveLaunch(settings.SelectedVersion);
            var java = launch.JavaPath;
            var versionId = settings.SelectedVersion;
            var instanceDir = GameInstanceLayout.GetDirectory(settings.GameDirectory, versionId);
            var runtimeDir = GameInstanceLayout.ResolveContentRoot(settings.GameDirectory, instanceDir);
            var snapshot = GameCrashService.Snapshot(runtimeDir);
            var launchedAt = DateTime.UtcNow;

            SetLaunchVisual(true);
            UpdateLaunchStatus("正在准备启动...");

            LaunchSession session;
            try
            {
                session = await AccountStore.Instance.EnsureLaunchSessionAsync(
                    new Progress<string>(UpdateLaunchStatus));
            }
            catch (Exception ex)
            {
                SetLaunchVisual(false);
                LaunchStatusText.Text = "账号无效：" + ex.Message;
                CompleteAiLaunch("启动失败：账号无效 —— " + ex.Message + "。可让用户用 manage_account 检查/切换账号。");
                return;
            }

            try
            {
                var progress = new Progress<string>(UpdateLaunchStatus);
                var launcher = new GameLaunchService(progress);
                var windowMode = launch.Independent ? launch.WindowMode : "default";
                var playerName = session.PlayerName;
                var extraGame = launch.Independent ? launch.ExtraGameArgs : null;
                if (!string.IsNullOrWhiteSpace(_pendingQuickPlay))
                {
                    extraGame = string.IsNullOrWhiteSpace(extraGame)
                        ? "--quickPlayMultiplayer " + _pendingQuickPlay
                        : extraGame + " --quickPlayMultiplayer " + _pendingQuickPlay;
                    var joinName = TerracottaService.Instance.ResolveJoinPlayName();
                    if (TerracottaService.NamesDiffer(joinName, playerName))
                    {
                        LauncherLogService.Info("Launch", "与房主玩家名相同，已临时使用 " + joinName + " 加入，避免名称被占用");
                        playerName = joinName;
                    }
                    LauncherLogService.Info("Launch", "将通过快捷联机加入 " + _pendingQuickPlay);
                    _pendingQuickPlay = null;
                }
                LauncherLogService.Info("Launch", $"正在启动 {versionId}（{session.UserType} / {playerName}）" + (launch.Independent ? "（使用版本独立设置）" : ""));
                var process = await launcher.LaunchAsync(
                    versionId,
                    java,
                    settings.GameDirectory,
                    playerName,
                    launch.MemoryMB,
                    autoMatchJava: launch.AutoMatchJava,
                    extraJvmArgs: launch.Independent ? launch.ExtraJvmArgs : null,
                    extraGameArgs: extraGame,
                    windowWidth: windowMode == "custom" ? launch.WindowWidth : null,
                    windowHeight: windowMode == "custom" ? launch.WindowHeight : null,
                    windowMode: windowMode,
                    authUuid: session.Uuid,
                    authAccessToken: session.AccessToken,
                    userType: session.UserType,
                    authXuid: session.Xuid,
                    clientId: session.ClientId);
                if (!launch.Independent && string.IsNullOrWhiteSpace(settings.JavaPath))
                {
                    var javaHome = JavaRuntimeService.FindAnyInstalled(settings.GameDirectory);
                    if (!string.IsNullOrWhiteSpace(javaHome) && File.Exists(javaHome))
                        settings.JavaPath = javaHome;
                }
                RunningGame = process;
                RunningGameVersion = versionId;
                RunningGameStartedUtc = launchedAt;
                RunningGameWindowShown = false;
                GameClosedByUser = false;
                _ = MonitorGameAsync(process, versionId, runtimeDir, snapshot, launchedAt, windowMode);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Launch", $"启动失败：{versionId}", ex);
                SetLaunchVisual(false);
                LaunchStatusText.Text = "启动失败：" + ex.Message;
                CompleteAiLaunch($"启动失败（版本 {versionId}）：{ex.Message}");
            }
        }

        private async Task LaunchBedrockAsync(InstalledVersion installed)
        {
            SetLaunchVisual(true);
            UpdateLaunchStatus(installed.LoaderId == "bedrock-server"
                ? "正在启动基岩专用服务器..."
                : "正在启动 Minecraft 基岩版...");
            var launchedAt = DateTime.UtcNow;
            try
            {
                if (installed.LoaderId == "bedrock-server")
                {
                    var instanceDir = GameInstanceLayout.GetDirectory(LauncherSettings.Instance.GameDirectory, installed.Id);
                    var snapshot = GameCrashService.Snapshot(instanceDir);
                    var process = BedrockEditionService.LaunchServer(installed.Id, new Progress<string>(UpdateLaunchStatus));
                    RunningGame = process;
                    RunningGameVersion = installed.Id;
                    RunningGameStartedUtc = launchedAt;
                    RunningGameWindowShown = false;
                    GameClosedByUser = false;
                    _ = MonitorGameAsync(process, installed.Id, instanceDir, snapshot, launchedAt);
                    return;
                }

                await BedrockEditionService.LaunchClientAsync(installed.Id, new Progress<string>(UpdateLaunchStatus));
                SetLaunchVisual(false);
                LaunchStatusText.Text = "已启动 Minecraft 基岩版";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Launch", $"启动基岩版失败：{installed.Id}", ex);
                SetLaunchVisual(false);
                LaunchStatusText.Text = "启动失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 启动期间的界面状态：主页整体左移到页面最左边（版本号与启动按钮随之左移，启动按钮保持居中），
        /// 隐藏版本设置与切换版本按钮，底栏临时切换为启动进度条。直到游戏窗口真正显示后才由监视任务解除。
        /// </summary>
        /// <summary>
        /// 底栏是否出现：启动游戏时必须出现（进度条），其余时候由设置里的「显示底部信息栏」决定。
        /// </summary>
        private void ApplyBottomBarVisibility()
        {
            var show = _launching || LauncherSettings.Instance.ShowBottomBar;
            BottomBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetLaunchVisual(bool launching)
        {
            _launching = launching;
            PlayButton.IsEnabled = !launching;
            PlayButtonText.Text = launching ? "正在启动..." : "启动游戏";

            BottomInfoGrid.Visibility = launching ? Visibility.Collapsed : Visibility.Visible;
            BottomLaunchPanel.Visibility = launching ? Visibility.Visible : Visibility.Collapsed;
            LaunchProgressBar.IsIndeterminate = launching;
            ApplyBottomBarVisibility();

            InstanceSettingsButton.Visibility = launching ? Visibility.Collapsed : Visibility.Visible;
            SwitchVersionButton.Visibility = launching ? Visibility.Collapsed : Visibility.Visible;

            // 启动游戏期间强制展示全景背景（无视「全景图背景」开关）
            ApplyPanoramaVisibility();

            ShiftHome(launching ? LaunchShiftX() : 0);
        }

        private void UpdateLaunchStatus(string message)
        {
            LaunchStatusText.Text = message;
            BottomLaunchText.Text = message;
        }

        /// <summary>主页左移量：把居中的内容组平移到页面最左边（留 40px 边距）。
        /// HomeContent 本身横向拉伸占满全宽，需按最宽的子行计算内容实际宽度。</summary>
        private double LaunchShiftX()
        {
            var contentWidth = HomeContent.Children.Count == 0
                ? 0
                : HomeContent.Children.OfType<FrameworkElement>().Max(c => c.ActualWidth);
            var margin = (HomeView.ActualWidth - contentWidth) / 2;
            return double.IsNaN(margin) ? 0 : -Math.Max(0, margin - 40);
        }

        private void ShiftHome(double x)
        {
            try
            {
                var sb = new Storyboard();
                var anim = new DoubleAnimation
                {
                    To = x,
                    Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(anim, HomeShiftTransform);
                Storyboard.SetTargetProperty(anim, "X");
                sb.Children.Add(anim);
                sb.Begin();
            }
            catch (Exception)
            {
                // 动画不可用时直接落到目标位置
                HomeShiftTransform.X = x;
            }
        }

        private void InstanceSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = LauncherSettings.Instance;
            var installed = InstalledVersionService.Find(settings.SelectedVersion);
            if (installed is null)
            {
                LaunchStatusText.Text = "请先安装并选择一个版本";
                return;
            }

            var panel = EnsureInstanceSettings();
            panel.Load(installed.Id, installed.DisplayName);
            InstanceSettingsHost.Visibility = Visibility.Visible;
            _instanceSettingsOpen = true;
            ApplyPanoramaVisibility();
        }

        private InstanceSettingsPanel EnsureInstanceSettings()
        {
            if (_instanceSettings is not null) return _instanceSettings;
            _instanceSettings = new InstanceSettingsPanel();
            _instanceSettings.BackRequested += (_, _) => CloseInstanceSettings();
            _instanceSettings.LibraryRequested += (_, _) => OpenLibraryPage();
            InstanceSettingsHost.Children.Add(_instanceSettings);
            return _instanceSettings;
        }

        private void CloseInstanceSettings(bool refresh = true)
        {
            if (_instanceSettings is null || InstanceSettingsHost.Visibility != Visibility.Visible) return;
            _instanceSettings.Save();
            InstanceSettingsHost.Visibility = Visibility.Collapsed;
            _instanceSettingsOpen = false;
            ApplyPanoramaVisibility();
            if (refresh)
            {
                RefreshChrome();
                var installed = InstalledVersionService.Find(LauncherSettings.Instance.SelectedVersion);
                if (installed is not null)
                {
                    LaunchStatusText.Text = GameInstanceLayout.ResolveLaunch(installed.Id).Independent
                        ? "已保存本版本的独立设置"
                        : "本版本将跟随全局设置";
                }
            }
        }

        private async Task MonitorGameAsync(
            Process process,
            string versionId,
            string instanceDir,
            HashSet<string> snapshot,
            DateTime launchedAtUtc,
            string windowMode = "default",
            string readyText = "游戏已启动")
        {
            try
            {
                // 进度条与左移界面保持到游戏窗口真正显示（或进程已退出）为止
                var windowShown = await WaitForGameWindowAsync(process);
                RunningGameWindowShown = windowShown && !process.HasExited;
                if (windowShown && !process.HasExited && windowMode == "maximized")
                    await MaximizeGameWindowAsync(process);
                DispatcherQueue.TryEnqueue(() =>
                {
                    SetLaunchVisual(false);
                    if (windowShown && !process.HasExited)
                        LaunchStatusText.Text = readyText;
                });

                // 游戏窗口已出现且进程存活：判定「启动成功」，立即回灌给 AI（不等整局结束）
                if (windowShown && !process.HasExited)
                    CompleteAiLaunch($"启动成功：{versionId} 的游戏窗口已正常显示，进程运行中。");

                // 设置了「启动游戏后关闭启动器」：游戏窗口显示后自动退出（此时无法再进行故障分析）
                if (windowShown && !process.HasExited && LauncherSettings.Instance.CloseLauncherAfterLaunch)
                {
                    LauncherLogService.Info("Launch", "游戏窗口已显示，按设置自动退出启动器");
                    Application.Current.Exit();
                    return;
                }

                if (!process.HasExited)
                    await process.WaitForExitAsync();
                var exitCode = process.ExitCode;
                await Task.Delay(1200);

                if (ReferenceEquals(RunningGame, process))
                    RunningGame = null;

                // 用户在消息页手动关闭游戏时不做故障分析
                var crash = GameClosedByUser ? null : GameCrashService.Detect(versionId, instanceDir, snapshot, exitCode, launchedAtUtc);
                if (!GameClosedByUser)
                    CompleteAiLaunch(BuildAiLaunchFailureText(versionId, exitCode, crash));
                DispatcherQueue.TryEnqueue(() => HandleGameExit(crash));
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Crash", "监视游戏进程失败", ex);
                CompleteAiLaunch($"无法确认启动结果：监视游戏进程时出错 —— {ex.Message}");
                DispatcherQueue.TryEnqueue(() => SetLaunchVisual(false));
            }
        }

        /// <summary>轮询游戏进程主窗口句柄，直到游戏界面显示。进程退出或超过 5 分钟时提前返回。</summary>
        private static async Task<bool> WaitForGameWindowAsync(Process process)
        {
            for (var i = 0; i < 300 && !process.HasExited; i++)
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
                catch (Exception)
                {
                    // 句柄查询失败时按未显示继续等待
                }
                await Task.Delay(1000);
            }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SwMaximize = 3;

        /// <summary>窗口模式为「最大化」时，在游戏窗口出现后将其最大化。</summary>
        private static async Task MaximizeGameWindowAsync(Process process)
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(process.MainWindowHandle, SwMaximize);
                        LauncherLogService.Info("Launch", "已将游戏窗口最大化");
                        return;
                    }
                }
                catch (Exception)
                {
                    // 句柄不可用时稍后重试
                }
                await Task.Delay(500);
            }
            LauncherLogService.Warn("Launch", "未能找到游戏窗口，跳过最大化");
        }

        private void HandleGameExit(GameCrashInfo? crash)
        {
            if (GameClosedByUser)
            {
                GameClosedByUser = false;
                LaunchStatusText.Text = "游戏已关闭";
                return;
            }

            if (crash is null)
            {
                if (LaunchStatusText.Text.StartsWith("游戏已启动", StringComparison.Ordinal)
                    || LaunchStatusText.Text.StartsWith("专用服务器已启动", StringComparison.Ordinal)
                    || LaunchStatusText.Text.StartsWith("正在检查", StringComparison.Ordinal))
                    LaunchStatusText.Text = "游戏已退出";
                return;
            }

            LaunchStatusText.Text = crash.Kind + "，请查看「消息」中的最近一次故障分析";
            NotificationService.GameCrash(crash.Kind, crash.VersionId);
            ShowPage("messages");
            _messagesPage?.RefreshSessionCrash();
        }

        private async void SwitchVersionButton_Click(object sender, RoutedEventArgs e)
        {
            var installed = InstalledVersionService.Scan();
            if (installed.Count == 0)
            {
                var empty = await AppDialog.ShowAsync(
                    RootGrid.XamlRoot,
                    "选择版本",
                    "尚未安装游戏版本。请先前往「下载」安装原版；如需模组加载器，可在同一详情页一并安装。",
                    primary: "前往下载",
                    close: "取消");
                if (empty == AppDialogResult.Primary)
                    ShowPage("download");
                return;
            }

            var list = new ListView
            {
                ItemsSource = installed,
                Height = 420,
                SelectionMode = ListViewSelectionMode.Single,
                ItemTemplate = (DataTemplate)RootGrid.Resources["InstalledVersionItemTemplate"]
            };
            var current = installed.FirstOrDefault(v => v.Id == LauncherSettings.Instance.SelectedVersion);
            list.SelectedItem = current ?? installed[0];

            var result = await AppDialog.ShowAsync(
                RootGrid.XamlRoot,
                "已下载的版本",
                "",
                primary: "确定",
                close: "取消",
                body: list);

            if (result != AppDialogResult.Primary || list.SelectedItem is not InstalledVersion selected)
                return;

            LauncherSettings.Instance.SelectedVersion = selected.Id;
            LaunchStatusText.Text = "";
        }

        private void UpdateAccountChrome()
        {
            var account = AccountStore.Instance.Selected;
            AccountNameText.Text = string.IsNullOrWhiteSpace(account.Username) ? "Player" : account.Username;
            CurrentUserButton.SetValue(ToolTipService.ToolTipProperty, account.TypeLabel + " · " + account.Username);
            TitleAvatar.Show(account);
        }

        private bool _accountPopoverOpen;

        /// <summary>
        /// 点右上角头像 = 就地管账号。浮层锚在头像正下方（见 AccountPopover），
        /// 不再用窗口居中的弹窗："当前用谁 / 用哪张贴图"是即时决定，离头像越近越顺手。
        /// </summary>
        private void CurrentUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (_accountPopoverOpen) CloseAccountPopover();
            else OpenAccountPopover();
        }

        private void OpenAccountPopover()
        {
            CloseInstanceSettings(refresh: false);
            var switcher = new AccountSwitcherBody();
            switcher.ManageRequested += OpenAccountSettings;
            AccountPopoverHost.Content = switcher;
            // 窗口很矮时浮层不能盖住底部状态条；ScrollViewer 在 MaxHeight 内自己滚
            AccountPopover.MaxHeight = Math.Clamp(RootGrid.ActualHeight - 68, 320, 640);
            AccountPopoverScrim.Visibility = Visibility.Visible;
            AccountPopover.Visibility = Visibility.Visible;
            // 岛由胶囊改成圆角矩形：整组（岛 + 浮层）读起来就是那块「放大了一圈」
            UserIsland.CornerRadius = new CornerRadius(12);
            UserIsland.BorderBrush = ThemeService.Brush("AccentBrush");
            UserIsland.BorderThickness = new Thickness(1);
            _accountPopoverOpen = true;
        }

        /// <summary>浮层里的「账号与皮肤来源设置…」：关掉浮层，跳到设置页的账号分组。</summary>
        private void OpenAccountSettings()
        {
            CloseAccountPopover();
            CloseInstanceSettings(refresh: false);
            ShowPage("settings");
            _settingsPage?.FocusAccounts();
        }

        private void CloseAccountPopover()
        {
            if (!_accountPopoverOpen) return;
            _accountPopoverOpen = false;
            AccountPopover.Visibility = Visibility.Collapsed;
            AccountPopoverScrim.Visibility = Visibility.Collapsed;
            // 主体交给静态 AccountStore 订阅着，收起来就断开，别留一份隐藏的账号列表
            AccountPopoverHost.Content = null;
            UserIsland.ClearValue(Border.CornerRadiusProperty);
            UserIsland.ClearValue(Border.BorderBrushProperty);
            UserIsland.ClearValue(Border.BorderThicknessProperty);
        }

        private void AccountPopoverScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
            => CloseAccountPopover();

        private void AccountPopoverClose_Click(object sender, RoutedEventArgs e) => CloseAccountPopover();

        private void OnDownloadTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (DownloadTask task in e.NewItems)
                    task.PropertyChanged += OnDownloadTaskPropertyChanged;
            }
            UpdateMessagesBadge();
        }

        private void OnDownloadTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DownloadTask.Status) or nameof(DownloadTask.IsActive) or null)
                UpdateMessagesBadge();
        }

        private void UpdateMessagesBadge()
        {
            var active = DownloadService.Instance.Tasks.Any(t => t.IsActive);
            if (_badgeActive == active) return;
            _badgeActive = active;
            MessagesBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>设置了「下载时自动跳转到消息页面」时，有新任务即切换过去。</summary>
        private void AutoSwitchToMessagesOnTaskAdded(DownloadTask task)
        {
            if (!LauncherSettings.Instance.AutoSwitchToMessages) return;
            if (task.SuppressMessageSwitch) return;
            if (_currentPage == "server") return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentPage == "server") return;
                CloseInstanceSettings(refresh: false);
                ShowPage("messages");
                _messagesPage?.RefreshSessionCrash();
                _messagesPage?.RefreshGameStatus();
                UpdateMessagesBadge();
            });
        }

        /// <summary>用户点击系统通知后跳转到对应页面。</summary>
        private void OnNotificationPageRequested(string page)
        {
            CloseInstanceSettings(refresh: false);
            ShowPage(page);
            if (page == "messages")
            {
                _messagesPage?.RefreshSessionCrash();
                _messagesPage?.RefreshGameStatus();
                UpdateMessagesBadge();
            }
            else if (page == "ai")
            {
                _aiPage?.RefreshBanner();
            }
        }

        private static string FormatJavaLabel(ResolvedLaunchSettings launch)
        {
            var settings = LauncherSettings.Instance;
            var javas = JavaRuntimeService.TryGetCached(settings.GameDirectory);
            var suffix = launch.Independent ? " · 本版本" : "";
            if (javas is null)
                return (launch.AutoMatchJava ? "自动匹配" : "检测中") + suffix;
            if (launch.AutoMatchJava)
                return (javas.Count > 0 ? $"自动匹配 · {javas.Count} 个" : "自动匹配") + suffix;

            var javaPath = launch.JavaPath;
            if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
                javaPath = javas.FirstOrDefault()?.Path ?? "";
            if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
                return "将自动下载" + suffix;

            var found = javas.FirstOrDefault(j => string.Equals(j.Path, javaPath, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return $"Java {found.Major}" + suffix;
            return "已配置" + suffix;
        }
    }
}
