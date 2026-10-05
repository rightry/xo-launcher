using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.UI.Text;
using WinRT.Interop;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class SettingsPage : UserControl
    {
        private bool _syncing;
        private bool _ready;
        private bool _bound;
        private int _javaScanGen;

        /// <summary>分组 id → 该分组的容器。导航按钮的 Tag 就是这里的 key。</summary>
        private readonly Dictionary<string, UIElement> _sections = new();

        public SettingsPage()
        {
            InitializeComponent();
            _sections["AccountSection"] = AccountSection;
            _sections["PersonalizationSection"] = PersonalizationSection;
            _sections["LauncherSection"] = LauncherSection;
            _sections["NotificationSection"] = NotificationSection;
            _sections["UpdateSection"] = UpdateSection;
            _sections["AiSection"] = AiSection;
            _sections["CreditsSection"] = CreditsSection;
            CreditsList.ItemsSource = CreateCredits();
            BindAiInfo();
            LauncherSettings.Instance.Changed += () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_bound) LoadFromSettings();
                });
            };
        }

        /// <summary>
        /// AI 卡片照实反映内置清单：可选模型、说明与请求地址都从 <see cref="AiModels"/> 读，
        /// 别再写死在 XAML 里 —— 加一档模型时那几行字会悄悄变成假的。
        /// </summary>
        private void BindAiInfo()
        {
            _syncing = true;
            try
            {
                AiModelSelect.ItemsSource = AiModels.All;
                AiModelSelect.SelectedItem = AiModels.Selected;
                AiModelIconService.Start();
            }
            finally
            {
                _syncing = false;
            }
            RefreshAiInfo();
        }

        private void RefreshAiInfo()
        {
            var option = AiModels.Selected;
            var hasKey = !string.IsNullOrWhiteSpace(option.ApiKey);
            AiProviderHintText.Text = hasKey
                ? "已填入密钥，可在「AI 助手」页提问。当前档位：" + option.Label + "（OpenAI 兼容端点）。"
                : "还没有填写 API Key，AI 助手暂时不可用。在下面填入你自己的密钥后即可使用。";
            AiKeyStateText.Text = hasKey
                ? "当前状态：已配置（" + MaskKey(option.ApiKey) + "）"
                : "当前状态：未配置";
            AiModelNoteText.Text = string.IsNullOrWhiteSpace(option.Note) ? option.Id : option.Note;
            AiEndpointText.Text = AiModels.RequestUrl(option);
        }

        /// <summary>设置页只显示首尾各 4 位，避免截图或旁人看见完整密钥。</summary>
        private static string MaskKey(string key)
            => key.Length <= 8 ? "····" : key[..4] + "····" + key[^4..];

        private void AiKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.AiApiKey = AiKeyBox.Text;
            RefreshAiInfo();
        }

        private void AiModelSelect_SelectionChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            if (AiModelSelect.SelectedItem is not AiModelOption option) return;
            LauncherSettings.Instance.AiModel = option.Id;
            RefreshAiInfo();
        }

        public void EnsureReady()
        {
            if (_bound) return;
            _bound = true;
            FillFontCombo();
            LoadFromSettings();
            _ready = true;
            if (LauncherUpdateService.LastCheck is { } last)
                BindUpdateInfo(last);
            if (_currentSection is null) SelectSection("PersonalizationSection");
        }

        public void FocusAi() => SelectSection("AiSection");

        /// <summary>头像浮层里点「账号与皮肤来源设置…」时跳过来。</summary>
        public void FocusAccounts() => SelectSection("AccountSection");

        private void SettingsNav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string name }) SelectSection(name);
        }

        private string? _currentSection;

        /// <summary>
        /// 切换设置分组。左侧导航选一组、右边只看这一组：以前六个分组叠成一屏滚到底，
        /// 找一个「启动游戏后关闭启动器」要跨过整个个性化卡，分组导航形同虚设。
        /// </summary>
        private void SelectSection(string id)
        {
            if (!_sections.ContainsKey(id)) return;
            _currentSection = id;
            foreach (var (key, panel) in _sections)
                panel.Visibility = key == id ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in NavRail.Children.OfType<Button>())
                PaintSettingNav(button, (button.Tag as string) == id);
            // 换组之后必须回到该组顶部，否则带着上一组的滚动位置跳进来
            DispatcherQueue.TryEnqueue(() => SettingsScroll.ChangeView(null, 0d, null, false));
        }

        /// <summary>页签高亮，与下载页分类顶栏同一套：强调色 + 半粗，不填底色。</summary>
        private static void PaintSettingNav(Button button, bool active)
        {
            button.Foreground = ThemeService.Brush(active ? "AccentBrush" : "GhostButtonForeground");
            button.FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(active ? 600 : 400) };
        }

        private void LoadFromSettings()
        {
            _syncing = true;
            var settings = LauncherSettings.Instance;
            BindGameFolders();
            GameDirBox.Text = settings.GameDirectory;
            GameDirHint.Text = GameDirectoryService.Describe(settings.GameDirectory);
            AutoMatchToggle.IsOn = settings.AutoMatchJava;
            RefreshJavaList();
            MemorySlider.Value = settings.MemoryMB;
            MemoryValueText.Text = $"{settings.MemoryMB} MB";
            DownloadDirBox.Text = settings.DownloadDirectory;
            TranslateNamesToggle.IsOn = settings.TranslateModNames;
            CloseAfterLaunchToggle.IsOn = settings.CloseLauncherAfterLaunch;
            AutoSwitchMessagesToggle.IsOn = settings.AutoSwitchToMessages;
            AutoCheckUpdatesToggle.IsOn = settings.AutoCheckUpdates;
            ShowBottomBarToggle.IsOn = settings.ShowBottomBar;
            PanoramaToggle.IsOn = settings.PanoramaBackground;
            PanoramaBlurSlider.Value = settings.PanoramaBlurAmount;
            PanoramaBlurValueText.Text = $"{settings.PanoramaBlurAmount:0}";
            PanoramaSpeedSlider.Value = settings.PanoramaScrollSpeed;
            PanoramaSpeedValueText.Text = $"{settings.PanoramaScrollSpeed:0} 档";
            ApplyBlurPanelEnabled(settings.PanoramaBackground);
            NotificationsEnabledToggle.IsOn = settings.NotificationsEnabled;
            NotifyDownloadToggle.IsOn = settings.NotifyDownloadComplete;
            NotifyCrashToggle.IsOn = settings.NotifyGameCrash;
            NotifyAiToggle.IsOn = settings.NotifyAiConfirm;
            RefreshNotifyToggles();
            UpdateCheckUrlBox.Text = LauncherUpdateService.ManifestUrl;
            CurrentVersionText.Text = "当前版本 " + LauncherUpdateService.CurrentVersion;
            RefreshUpdateButtons();
            SelectMirrorCombo(settings.DownloadMirror);
            AiKeyBox.Text = settings.AiApiKey;
            SelectFontCombo(settings.UiFont);
            UpdateThemeCards();
            UpdateAccentSwatches();
            _syncing = false;
        }

        public void FocusUpdate() => SelectSection("UpdateSection");

        internal void ShowUpdateResult(LauncherUpdateInfo info) => BindUpdateInfo(info);

        private void AutoCheckUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_ready || _syncing) return;
            LauncherSettings.Instance.AutoCheckUpdates = AutoCheckUpdatesToggle.IsOn;
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在检查更新…";
            try
            {
                var info = await LauncherUpdateService.CheckAsync();
                BindUpdateInfo(info);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Update", "检查更新失败", ex);
                UpdateStatusText.Text = "检查失败：" + ex.Message;
                UpdateNotesText.Visibility = Visibility.Collapsed;
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
        {
            var info = LauncherUpdateService.LastCheck;
            if (info is null || !info.CanDownload)
            {
                UpdateStatusText.Text = "没有可下载的更新包。";
                return;
            }

            DownloadUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在下载 " + info.Version + "…";
            var task = DownloadService.Instance.StartInstall("启动器更新 " + info.Version, info.FileName);
            try
            {
                await LauncherUpdateService.PrepareAsync(info, task, task.Token);
                UpdateStatusText.Text = "已下载 " + info.Version + "，可立即安装并重启。";
                RefreshUpdateButtons();
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                UpdateStatusText.Text = "已取消下载。";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Update", "下载更新失败", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Detail = ex.Message;
                });
                UpdateStatusText.Text = "下载失败：" + ex.Message;
            }
            finally
            {
                DownloadUpdateButton.IsEnabled = true;
            }
        }

        private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
        {
            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "安装更新",
                "启动器将关闭并安装新版本，完成后会自动重新打开。",
                primary: "安装并重启",
                close: "取消");
            if (result != AppDialogResult.Primary)
                return;

            try
            {
                if (LauncherUpdateService.TryLaunchSetupInstead())
                {
                    Application.Current.Exit();
                    return;
                }
                LauncherUpdateService.ApplyAndRestart();
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Update", "安装更新失败", ex);
                UpdateStatusText.Text = "安装失败：" + ex.Message;
            }
        }

        private void BindUpdateInfo(LauncherUpdateInfo info)
        {
            CurrentVersionText.Text = "当前版本 " + LauncherUpdateService.CurrentVersion;
            if (info.IsNewer)
            {
                var prefix = info.ForceUpdate ? "需要更新" : "发现新版本";
                UpdateStatusText.Text = info.CanDownload
                    ? prefix + " " + info.Version + "，可以下载。"
                    : prefix + " " + info.Version + "，但更新源没有安装包，请手动下载。";
            }
            else
            {
                UpdateStatusText.Text = "已是最新版本 " + LauncherUpdateService.CurrentVersion + "。";
            }

            if (!string.IsNullOrWhiteSpace(info.Notes))
            {
                UpdateNotesText.Text = info.Notes.Length > 600 ? info.Notes[..600] + "…" : info.Notes;
                UpdateNotesText.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(info.ReleaseDate))
            {
                UpdateNotesText.Text = "发布日期：" + info.ReleaseDate;
                UpdateNotesText.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateNotesText.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(info.PageUrl)
                && Uri.TryCreate(info.PageUrl, UriKind.Absolute, out var page))
                UpdatePageLink.NavigateUri = page;

            RefreshUpdateButtons();
        }

        private void RefreshUpdateButtons()
        {
            var info = LauncherUpdateService.LastCheck;
            DownloadUpdateButton.Visibility = info is { IsNewer: true, CanDownload: true } && !LauncherUpdateService.HasPendingInstall
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyUpdateButton.Visibility = LauncherUpdateService.HasPendingInstall
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (LauncherUpdateService.HasPendingInstall)
                UpdateStatusText.Text = "更新已下载，点击「安装并重启」完成安装。";
        }

        private void SelectMirrorCombo(string mode)
        {
            foreach (var item in MirrorCombo.Items)
            {
                if (item is SelectBoxItem { Tag: string tag } && string.Equals(tag, mode, StringComparison.Ordinal))
                {
                    MirrorCombo.SelectedItem = item;
                    return;
                }
            }
            MirrorCombo.SelectedIndex = 0;
        }

        private void MirrorCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (!_ready || _syncing) return;
            if (MirrorCombo.SelectedItem is SelectBoxItem { Tag: string tag })
                LauncherSettings.Instance.DownloadMirror = tag;
        }

        private static List<CreditItem> CreateCredits()
        {
            var list = new List<CreditItem>
            {
                new()
                {
                    Name = "PCL2",
                    Description = "Plain Craft Launcher 2。面向中文用户的开源启动器，在版本下载、模组管理与界面交互上提供了大量可参考的实践。",
                    IconText = "P",
                    IconColor = "#3D8BDB",
                    Url = "https://github.com/Meloong-Git/PCL",
                    RepoText = "Meloong-Git/PCL"
                },
                new()
                {
                    Name = "HMCL",
                    Description = "Hello Minecraft! Launcher。跨平台开源启动器，完整覆盖版本管理、模组安装与账号体系，是中文社区最常用的参考实现之一。",
                    IconText = "H",
                    IconColor = "#1F6FEB",
                    Url = "https://github.com/HMCL-dev/HMCL",
                    RepoText = "HMCL-dev/HMCL"
                },
                new()
                {
                    Name = "Terracotta",
                    Description = "陶瓦联机。基于 EasyTier 的 Minecraft Java 版 P2P 联机内核，本启动器通过官方 HTTP API 调用未修改的二进制实现快捷联机。",
                    IconText = "T",
                    IconColor = "#C45C26",
                    Url = "https://github.com/burningtnt/Terracotta",
                    RepoText = "burningtnt/Terracotta"
                },
                new()
                {
                    Name = "Prism Launcher",
                    Description = "基于 MultiMC 的现代化开源启动器，原生对接 Modrinth / CurseForge，实例隔离与模组管理职责划分清晰。",
                    IconText = "P",
                    IconColor = "#7CBD4B",
                    Url = "https://github.com/PrismLauncher/PrismLauncher",
                    RepoText = "PrismLauncher/PrismLauncher"
                },
                new()
                {
                    Name = "MultiMC",
                    Description = "开创实例（Instance）隔离体系的经典开源启动器，后续 Prism、PolyMC 等项目都建立在它的架构思路上。",
                    IconText = "M",
                    IconColor = "#A371F7",
                    Url = "https://github.com/MultiMC/Launcher",
                    RepoText = "MultiMC/Launcher"
                },
                
                new()
                {
                    Name = "XMCL",
                    Description = "X Minecraft Launcher。对 Modrinth 资源浏览、版本解析与实例管理有完整实现，本启动器的下载/详情结构参考了它的职责拆分。",
                    IconText = "X",
                    IconColor = "#5B8731",
                    Url = "https://github.com/Voxelum/x-minecraft-launcher",
                    RepoText = "Voxelum/x-minecraft-launcher"
                },
                new()
                {
                    Name = "Fold Craft Launcher",
                    Description = "面向 Android 的开源 Minecraft 启动器，在跨平台启动、运行时管理与模组加载器安装上提供了可借鉴的方案。",
                    IconText = "F",
                    IconColor = "#E07A3D",
                    Url = "https://github.com/FCL-Team/FoldCraftLauncher",
                    RepoText = "FCL-Team/FoldCraftLauncher"
                },
                new()
                {
                    Name = "ATLauncher",
                    Description = "成熟的整合包与模组启动器，包管理、快速更新通道和实例配置对社区启动器设计影响很大。",
                    IconText = "A",
                    IconColor = "#E3B341",
                    Url = "https://github.com/ATLauncher/ATLauncher",
                    RepoText = "ATLauncher/ATLauncher"
                },
                new()
                {
                    Name = "GDLauncher",
                    Description = "开源桌面启动器，强调整合包浏览与一键安装，模组平台对接方式值得参考。",
                    IconText = "G",
                    IconColor = "#4EA8DE",
                    Url = "https://github.com/gorilla-devs/GDLauncher",
                    RepoText = "gorilla-devs/GDLauncher"
                },
                new()
                {
                    Name = "Modrinth App",
                    Description = "Modrinth 官方开源客户端，项目详情、按游戏版本筛选文件以及下载队列的交互是本详情页的重要参考。",
                    IconText = "M",
                    IconColor = "#1BD96A",
                    Url = "https://github.com/modrinth/code",
                    RepoText = "modrinth/code"
                },
                new()
                {
                    Name = "BedrockBoot",
                    Description = "Round Studio 的基岩版启动器。本启动器的客户端版本列表、APPX 下载、开发者模式注册与启动切换对齐 BedrockBoot / BedrockLauncher.Core。",
                    IconText = "B",
                    IconColor = "#3D7A3C",
                    Url = "https://github.com/Round-Studio/BedrockBoot",
                    RepoText = "Round-Studio/BedrockBoot"
                },
                new()
                {
                    Name = "BedrockLauncher",
                    Description = "Windows 基岩版多版本启动器。Store 正版客户端检测与启动作为本启动器的补充入口。",
                    IconText = "L",
                    IconColor = "#5B8731",
                    Url = "https://github.com/BedrockLauncher/BedrockLauncher",
                    RepoText = "BedrockLauncher/BedrockLauncher"
                },
                new()
                {
                    Name = "mc-w10-version-launcher",
                    Description = "MCMrARM 的 Windows 10 基岩版并排安装工具。历史版本库与 Store FE3 下载协议是本启动器解析 APPX 直链的参考。",
                    IconText = "W",
                    IconColor = "#496A88",
                    Url = "https://github.com/MCMrARM/mc-w10-version-launcher",
                    RepoText = "MCMrARM/mc-w10-version-launcher"
                }
            };

            // 命中本地真实图标时，用图标替换字母占位
            foreach (var item in list)
            {
                if (CreditIconFiles.TryGetValue(item.Name, out var file) && LoadCreditIcon(file) is { } icon)
                {
                    item.IconSource = icon;
                    item.IconText = "";
                }
            }
            return list;
        }

        /// <summary>项目名 → Assets/credits 下的图标文件。</summary>
        private static readonly Dictionary<string, string> CreditIconFiles = new()
        {
            ["PCL2"] = "pcl.png",
            ["HMCL"] = "hmcl.png",
            ["Prism Launcher"] = "prism.svg",
            ["MultiMC"] = "multimc.svg",
            ["XMCL"] = "xmcl.png",
            ["Fold Craft Launcher"] = "fcl.png",
            ["ATLauncher"] = "atlauncher.png",
            ["GDLauncher"] = "gdlauncher.ico",
            ["Modrinth App"] = "modrinth.png"
        };

        private static ImageSource? LoadCreditIcon(string fileName)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "credits", fileName);
                if (!File.Exists(path)) return null;
                var uri = new Uri(path);
                return fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                    ? new SvgImageSource(uri)
                    : new BitmapImage(uri);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void BindGameFolders()
        {
            if (GameFolderCombo is null) return;
            var folders = LauncherSettings.Instance.GameFolders.ToList();
            GameFolderCombo.ItemsSource = folders;
            var current = folders.FirstOrDefault(f =>
                string.Equals(f.Path, LauncherSettings.Instance.GameDirectory, StringComparison.OrdinalIgnoreCase))
                ?? folders.FirstOrDefault();
            GameFolderCombo.SelectedItem = current;
        }

        private void GameFolderCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (!_ready || _syncing) return;
            if (GameFolderCombo.SelectedItem is not GameFolderEntry folder) return;
            if (string.Equals(folder.Path, LauncherSettings.Instance.GameDirectory, StringComparison.OrdinalIgnoreCase))
                return;
            ApplyGameDirectory(folder.Path);
        }

        private async void AddGameFolder_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, GetHwnd());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ApplyGameDirectory(folder.Path);
        }

        private void RemoveGameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (LauncherSettings.Instance.GameFolders.Count <= 1)
            {
                GameDirHint.Text = "至少保留一个游戏目录。";
                return;
            }
            var selected = GameFolderCombo.SelectedItem as GameFolderEntry;
            var id = selected?.Id ?? LauncherSettings.Instance.ActiveFolderId;
            if (string.IsNullOrWhiteSpace(id) || !LauncherSettings.Instance.RemoveGameFolder(id))
                return;
            ApplyGameDirectory(LauncherSettings.Instance.GameDirectory);
        }

        private async void BrowseGameDir_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, GetHwnd());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ApplyGameDirectory(folder.Path);
        }

        private async void BrowseJava_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, GetHwnd());
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            LauncherSettings.Instance.JavaPath = file.Path;
            JavaRuntimeService.InvalidateScanCache();
            RefreshJavaList();
        }

        private void DetectJava_Click(object sender, RoutedEventArgs e)
        {
            JavaRuntimeService.InvalidateScanCache();
            RefreshJavaList();
            if (!JavaCombo.HasOptions)
                JavaStatusText.Text = "未检测到 Java，请浏览 javaw.exe 或下载官方运行时。";
        }

        private void ApplyGameDirectory(string path)
        {
            try
            {
                var resolved = GameDirectoryService.Apply(path, out var summary);
                GameDirBox.Text = resolved;
                GameDirHint.Text = summary;
                RefreshJavaList();
            }
            catch (Exception ex)
            {
                GameDirBox.Text = LauncherSettings.Instance.GameDirectory;
                GameDirHint.Text = "无法使用该目录：" + ex.Message;
            }
        }

        private void AutoMatchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.AutoMatchJava = AutoMatchToggle.IsOn;
        }

        private void JavaCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            if (JavaCombo.SelectedItem is JavaInstallation java)
                LauncherSettings.Instance.JavaPath = java.Path;
        }

        private async void RefreshJavaList()
        {
            var cached = JavaRuntimeService.TryGetCached();
            if (cached is not null)
            {
                BindJavaList(cached);
                return;
            }

            JavaStatusText.Text = "正在检测 Java…";
            var gen = ++_javaScanGen;
            IReadOnlyList<JavaInstallation> javas;
            try
            {
                javas = await Task.Run(() => JavaRuntimeService.ScanAll());
            }
            catch (Exception)
            {
                javas = Array.Empty<JavaInstallation>();
            }
            if (gen != _javaScanGen || JavaCombo is null) return;
            var wasSyncing = _syncing;
            _syncing = true;
            try { BindJavaList(javas); }
            finally { _syncing = wasSyncing; }
        }

        private void BindJavaList(IReadOnlyList<JavaInstallation> javas)
        {
            JavaCombo.ItemsSource = javas;
            JavaCombo.DisplayMemberPath = nameof(JavaInstallation.Display);
            var current = LauncherSettings.Instance.JavaPath;
            var selected = javas.FirstOrDefault(j => string.Equals(j.Path, current, StringComparison.OrdinalIgnoreCase));
            JavaCombo.SelectedItem = selected;
            if (selected is null && !string.IsNullOrWhiteSpace(current) && File.Exists(current))
                JavaStatusText.Text = "当前指定：" + current;
            else if (javas.Count == 0)
                JavaStatusText.Text = "尚未检测到 Java，启动时会按游戏版本自动下载官方运行时。";
            else
            {
                var labels = javas.Select(j => j.Display).Distinct().ToList();
                var list = string.Join("、", labels);
                if (LauncherSettings.Instance.AutoMatchJava)
                    JavaStatusText.Text = $"已检测到 {javas.Count} 个 Java：{list}。启动时会按游戏所需版本自动选择。";
                else if (selected is not null)
                    JavaStatusText.Text = "当前使用：" + selected.Display;
                else
                    JavaStatusText.Text = $"已检测到 {javas.Count} 个 Java：{list}，请选择一个或开启自动匹配。";
            }
        }

        private async void DownloadJava_Click(object sender, RoutedEventArgs e)
        {
            DownloadJavaButton.IsEnabled = false;
            JavaStatusText.Text = "正在下载 Mojang 官方 Java 21，进度请到「消息」查看…";
            var task = DownloadService.Instance.StartInstall("Java 虚拟机", "Java 21");
            try
            {
                var path = await JavaRuntimeService.Instance.EnsureAsync(
                    new JavaRequirement("java-runtime-delta", 21),
                    LauncherSettings.Instance.GameDirectory,
                    null,
                    task,
                    null,
                    cancellationToken: task.Token);
                LauncherSettings.Instance.JavaPath = path;
                JavaRuntimeService.InvalidateScanCache();
                RefreshJavaList();
                var major = JavaRuntimeService.QueryMajorVersion(path);
                JavaStatusText.Text = major > 0 ? $"已安装：Java {major}" : "已安装官方 Java 运行时。";
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                JavaStatusText.Text = "已终止 Java 下载";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Java", "下载官方 Java 失败", ex);
                JavaStatusText.Text = "下载失败：" + ex.Message;
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Detail = ex.Message;
                    t.ProgressHint = "安装失败";
                });
            }
            finally
            {
                DownloadJavaButton.IsEnabled = true;
            }
        }

        private void GameDirBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_syncing || string.IsNullOrWhiteSpace(GameDirBox.Text)) return;
            ApplyGameDirectory(GameDirBox.Text.Trim());
        }

        private void DownloadDirBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_syncing || string.IsNullOrWhiteSpace(DownloadDirBox.Text)) return;
            ApplyDownloadDirectory(DownloadDirBox.Text.Trim());
        }

        private async void BrowseDownloadDir_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, GetHwnd());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            ApplyDownloadDirectory(folder.Path);
        }

        private void ApplyDownloadDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                ModLibraryService.EnsureLayout(path);
                LauncherSettings.Instance.DownloadDirectory = path;
                DownloadDirBox.Text = path;
                DownloadDirHint.Text = "模组、光影与资源包将保存至此目录下的 mods、shaderpacks、resourcepacks 子目录。下载完成后不会自动安装，请在版本设置中选择并安装。";
            }
            catch (Exception ex)
            {
                DownloadDirBox.Text = LauncherSettings.Instance.DownloadDirectory;
                DownloadDirHint.Text = "无法使用该目录：" + ex.Message;
            }
        }

        private async void ExportLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = await LogExportHelper.PickAndExportAsync();
                LogStatusText.Text = path is null ? "已取消导出。" : "已导出：" + path;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Settings", "导出错误日志失败", ex);
                LogStatusText.Text = "导出失败：" + ex.Message;
            }
        }

        private async void AnalyzeCrash_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await CrashAnalysisPanel.ShowLatestDialogAsync(XamlRoot);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Settings", "故障分析失败", ex);
                LogStatusText.Text = "故障分析失败：" + ex.Message;
            }
        }

        private async void ExportCrash_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var crash = GameCrashService.FindLatest();
                if (crash is null)
                {
                    LogStatusText.Text = "没有找到崩溃报告。启动游戏并在崩溃后可在此导出。";
                    return;
                }
                var path = await LogExportHelper.PickAndExportCrashAsync(crash);
                LogStatusText.Text = path is null ? "已取消导出。" : "已导出崩溃日志：" + path;
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Settings", "导出崩溃日志失败", ex);
                LogStatusText.Text = "导出失败：" + ex.Message;
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(LauncherLogService.LogDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", LauncherLogService.LogDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogStatusText.Text = "无法打开日志文件夹：" + ex.Message;
            }
        }

        private void ThemeCard_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            if (sender is FrameworkElement { Tag: string theme })
                LauncherSettings.Instance.AppTheme = theme;
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            if (sender is FrameworkElement { Tag: string hex })
                LauncherSettings.Instance.AccentColor = hex;
        }

        private async void CustomAccent_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var body = new ColorPickBody(ThemeService.ParseAccent(LauncherSettings.Instance.AccentColor));
            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "自定义主题色",
                "",
                primary: "应用",
                close: "取消",
                body: body);
            if (result == AppDialogResult.Primary)
                LauncherSettings.Instance.AccentColor = ThemeService.FormatAccent(body.Color);
        }

        private void TranslateNamesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.TranslateModNames = TranslateNamesToggle.IsOn;
        }

        private void AutoSwitchMessagesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.AutoSwitchToMessages = AutoSwitchMessagesToggle.IsOn;
        }

        private void ShowBottomBarToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.ShowBottomBar = ShowBottomBarToggle.IsOn;
        }

        private void PanoramaToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.PanoramaBackground = PanoramaToggle.IsOn;
            ApplyBlurPanelEnabled(PanoramaToggle.IsOn);
        }

        /// <summary>全景开关关闭时置灰模糊调节组：StackPanel 无 IsEnabled，故禁用滑块并调低整组透明度。</summary>
        private void ApplyBlurPanelEnabled(bool enabled)
        {
            PanoramaBlurSlider.IsEnabled = enabled;
            PanoramaSpeedSlider.IsEnabled = enabled;
            PanoramaBlurPanel.Opacity = enabled ? 1.0 : 0.5;
        }

        private void PanoramaBlurSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            var value = Math.Round(e.NewValue);
            PanoramaBlurValueText.Text = $"{value:0}";
            LauncherSettings.Instance.PanoramaBlurAmount = value;
        }

        private void PanoramaSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncing) return;
            var value = Math.Round(e.NewValue / 5) * 5;
            PanoramaSpeedValueText.Text = $"{value:0} 档";
            LauncherSettings.Instance.PanoramaScrollSpeed = value;
        }

        /// <summary>总开关关闭时，各分类通知开关置灰。</summary>
        private void RefreshNotifyToggles()
        {
            var on = NotificationsEnabledToggle.IsOn;
            NotifyDownloadToggle.IsEnabled = on;
            NotifyCrashToggle.IsEnabled = on;
            NotifyAiToggle.IsEnabled = on;
        }

        private void NotificationsEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            RefreshNotifyToggles();
            if (_syncing) return;
            LauncherSettings.Instance.NotificationsEnabled = NotificationsEnabledToggle.IsOn;
        }

        private void NotifyDownloadToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.NotifyDownloadComplete = NotifyDownloadToggle.IsOn;
        }

        private void NotifyCrashToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.NotifyGameCrash = NotifyCrashToggle.IsOn;
        }

        private void NotifyAiToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            LauncherSettings.Instance.NotifyAiConfirm = NotifyAiToggle.IsOn;
        }

        private async void CloseAfterLaunchToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var enabling = CloseAfterLaunchToggle.IsOn;
            LauncherSettings.Instance.CloseLauncherAfterLaunch = enabling;
            if (!enabling) return;

            await AppDialog.ShowMessageAsync(XamlRoot, "无法进行故障分析",
                "已开启「启动游戏后关闭启动器」。游戏窗口显示时启动器会自动退出，此后游戏崩溃将无法自动分析，也不会生成崩溃报告。");
        }

        private void FillFontCombo()
        {
            if (FontCombo is null) return;
            FontCombo.ItemsSource = ThemeService.ListUiFonts();
        }

        private void SelectFontCombo(string family)
        {
            if (FontCombo is null) return;
            UiFontOption? match = null;
            if (FontCombo.ItemsSource is IEnumerable<UiFontOption> items)
            {
                foreach (var item in items)
                {
                    if (string.Equals(item.Family, family, StringComparison.OrdinalIgnoreCase))
                    {
                        match = item;
                        break;
                    }
                }
                match ??= items.FirstOrDefault();
            }
            FontCombo.SelectedItem = match;
            if (match is not null)
                FontCombo.FontFamily = new FontFamily(match.Family);
            UpdateFontPreview();
        }

        private void UpdateFontPreview()
        {
            if (FontPreviewText is null) return;
            FontPreviewText.FontFamily = ThemeService.AppFont;
        }

        private void FontCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (!_ready || _syncing) return;
            if (FontCombo.SelectedItem is not UiFontOption option) return;
            LauncherSettings.Instance.UiFont = option.Family;
            ThemeService.ApplyFont();
            FontCombo.FontFamily = new FontFamily(option.Family);
            UpdateFontPreview();
        }

        private void UpdateThemeCards()
        {
            if (SystemThemeButton is null || DarkThemeButton is null || LightThemeButton is null) return;
            var theme = LauncherSettings.Instance.AppTheme;
            SetThemeCard(SystemThemeButton, string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase));
            SetThemeCard(DarkThemeButton, string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));
            SetThemeCard(LightThemeButton, string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase));
        }

        private static void SetThemeCard(Button button, bool selected)
        {
            button.BorderBrush = selected
                ? ThemeService.Brush("AccentBrush")
                : ThemeService.Brush("DividerBrush");
        }

        private void UpdateAccentSwatches()
        {
            if (AccentSwatchHost is null || CustomAccentButton is null || AccentHexText is null) return;
            var current = LauncherSettings.Instance.AccentColor;
            AccentHexText.Text = current;
            AccentSwatchHost.Children.Clear();
            foreach (var (name, hex) in ThemeService.AccentPresets)
            {
                var selected = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase);
                var button = new Button
                {
                    Tag = hex,
                    Width = 32,
                    Height = 32,
                    MinWidth = 32,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(16),
                    BorderThickness = new Thickness(selected ? 3 : 2),
                    BorderBrush = selected
                        ? ThemeService.Brush("TextPrimaryBrush")
                        : ThemeService.Brush("DividerBrush"),
                    Background = new SolidColorBrush(ThemeService.ParseAccent(hex))
                };
                ToolTipService.SetToolTip(button, name);
                button.Click += AccentSwatch_Click;
                AccentSwatchHost.Children.Add(button);
            }

            var custom = !ThemeService.IsPreset(current);
            CustomAccentButton.BorderThickness = new Thickness(custom ? 2 : 1);
            CustomAccentButton.BorderBrush = custom
                ? ThemeService.Brush("AccentBrush")
                : ThemeService.Brush("DividerBrush");
        }

        private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = ModLibraryService.RootDirectory;
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 打开资源管理器失败时忽略
            }
        }

        private void MemorySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            MemoryValueText.Text = $"{(int)e.NewValue} MB";
            if (_syncing || !_ready) return;
            LauncherSettings.Instance.MemoryMB = (long)e.NewValue;
        }

        private static IntPtr GetHwnd()
        {
            return App.MainWindow is not null ? WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
        }

        // 账号与皮肤来源已迁到点头像弹出的 AccountSettingsBody（窗口右上角），
        // 本页不再管账号：那属于「当前用谁」的即时决定，和主题 / 更新不是一个层级。
    }

    internal static class LogExportHelper
    {
        public static async Task<string?> PickAndExportAsync()
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("日志文件", new List<string> { ".txt" });
            picker.SuggestedFileName = $"XO-Launcher-log-{DateTime.Now:yyyyMMdd-HHmmss}";
            InitializeWithWindow.Initialize(picker, App.MainWindow is not null
                ? WindowNative.GetWindowHandle(App.MainWindow)
                : IntPtr.Zero);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            LauncherLogService.ExportTo(file.Path);
            return file.Path;
        }

        public static async Task<string?> PickAndExportCrashAsync(GameCrashInfo crash)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("崩溃日志", new List<string> { ".txt" });
            picker.SuggestedFileName = $"XO-crash-{GameInstanceLayout.Sanitize(crash.VersionId)}-{DateTime.Now:yyyyMMdd-HHmmss}";
            InitializeWithWindow.Initialize(picker, App.MainWindow is not null
                ? WindowNative.GetWindowHandle(App.MainWindow)
                : IntPtr.Zero);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return null;
            GameCrashService.ExportTo(file.Path, crash);
            return file.Path;
        }
    }
}
