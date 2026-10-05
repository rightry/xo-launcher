using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using XO_Launcher.Api;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class VanillaDetailPage : UserControl
    {
        private readonly LoaderMetaApi _loaderApi = new();
        private readonly GameInstallService _installer = new();
        private CancellationTokenSource? _loaderCts;
        private DownloadItem? _item;
        private bool _installing;
        private bool _nameTouched;
        private bool _updatingName;
        private string _selectedLoader = "vanilla";
        private IReadOnlyList<LoaderBuild> _loaderBuilds = Array.Empty<LoaderBuild>();
        private DownloadTask? _installTask;
        private int _step = 1;
        private bool _installStarted;

        private static readonly string[] StepTitles = { "模组加载器", "加载器版本", "游戏名称", "确认安装", "安装结果" };

        public event EventHandler? BackRequested;

        public VanillaDetailPage()
        {
            InitializeComponent();
            BackButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public void Show(DownloadItem item)
        {
            _item = item;
            _installing = false;
            _selectedLoader = "vanilla";
            _loaderBuilds = Array.Empty<LoaderBuild>();
            QueueTipBorder.Visibility = Visibility.Collapsed;
            ShowResult(null);
            _installStarted = false;
            InstallRing.IsActive = false;
            Step5Title.Text = "正在安装…";
            TitleText.Text = item.Title;
            TypeText.Text = item.Badge;
            TypeText.Foreground = item.IsAlpha
                ? new SolidColorBrush(Color.FromArgb(255, 229, 72, 77))
                : ThemeService.Brush("AccentBrush");
            TypeChip.Background = item.IsAlpha
                ? new SolidColorBrush(Color.FromArgb(255, 58, 21, 24))
                : new SolidColorBrush(Color.FromArgb(255, 27, 42, 27));
            MetaText.Text = string.IsNullOrWhiteSpace(item.Meta) ? "Mojang 官方版本清单" : item.Meta + "  ·  Mojang 官方";
            DescriptionText.Text = item.IsAlpha
                ? "这是远古 Alpha 版本，可能无法在现代 Java 上运行。将与所选加载器写入同一个游戏文件夹。"
                : "游戏本体与加载器会安装到同一个 versions 文件夹。资源文件在首次启动时也可自动补齐。";
            IconBorder.Background = new SolidColorBrush(ParseColor(item.IconColor));
            HighlightLoaderCards();
            LoaderVersionPanel.Visibility = Visibility.Collapsed;
            FabricApiPanel.Visibility = Visibility.Collapsed;
            ShowAllLoaderToggle.Visibility = Visibility.Collapsed;
            LoaderVersionBox.ItemsSource = null;
            _nameTouched = false;
            SuggestGameName();
            InstallButton.IsEnabled = true;
            InstallButtonText.Text = "下载并安装";
            GoStep(1);
        }

        // ==================== 向导步骤 ====================

        /// <summary>第 2 步只在装了加载器时存在；第 5 步要按「下载并安装」才会到达。</summary>
        private bool StepReachable(int step) => step switch
        {
            2 => _selectedLoader != "vanilla" && _selectedLoader is not null,
            5 => _installStarted,
            _ => true,
        };

        private void GoStep(int step)
        {
            if (step < 1 || step > StepTitles.Length) return;
            if (!StepReachable(step)) return;
            _step = step;

            Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
            Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
            Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
            Step4Panel.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
            Step5Panel.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

            ApplyStepChip(Step1Button, Step1Badge, Step1BadgeText, Step1Label, 1);
            ApplyStepChip(Step2Button, Step2Badge, Step2BadgeText, Step2Label, 2);
            ApplyStepChip(Step3Button, Step3Badge, Step3BadgeText, Step3Label, 3);
            ApplyStepChip(Step4Button, Step4Badge, Step4BadgeText, Step4Label, 4);
            ApplyStepChip(Step5Button, Step5Badge, Step5BadgeText, Step5Label, 5);

            // 安装开始后不给回头：改配置得等这轮装完，否则半途的下载任务指向的已是改掉的参数。
            PrevButton.IsEnabled = step > 1 && step < StepTitles.Length;
            // 结果页只剩居中的「完成」：上一步和步骤提示都收掉，终点不再摆一排导航
            PrevButton.Visibility = step == StepTitles.Length ? Visibility.Collapsed : Visibility.Visible;
            StepIndicatorText.Visibility = step == StepTitles.Length ? Visibility.Collapsed : Visibility.Visible;
            NextButton.Visibility = step is 1 or 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
            InstallButton.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
            FinishButton.Visibility = step == StepTitles.Length ? Visibility.Visible : Visibility.Collapsed;
            StepIndicatorText.Text = $"第 {step} / {StepTitles.Length} 步 · {StepTitles[step - 1]}";

            if (step == 4) UpdateSummary();
        }

        private void ApplyStepChip(Button button, Border badge, TextBlock badgeText, TextBlock label, int index)
        {
            bool current = index == _step;
            bool done = index < _step;
            bool reachable = StepReachable(index);

            button.IsEnabled = reachable || done;
            badge.Background = current
                ? ThemeService.Brush("AccentBrush")
                : done ? ThemeService.Brush("AccentSoftBrush") : ThemeService.Brush("ChipBackgroundBrush");
            badgeText.Foreground = current
                ? ThemeService.Brush("AccentOnBrush")
                : done ? ThemeService.Brush("AccentBrush") : ThemeService.Brush("TextSecondaryBrush");
            label.Foreground = current
                ? ThemeService.Brush("TextPrimaryBrush")
                : reachable ? ThemeService.Brush("TextSecondaryBrush") : ThemeService.Brush("TextTertiaryBrush");
            label.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            // 不可达的步骤（原版时的第 2 步）整块调暗，避免看起来像「可以点但点了没反应」
            button.Opacity = reachable || done ? 1.0 : 0.45;
        }

        private void StepChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            if (int.TryParse(tag, out var step)) GoStep(step);
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            GoStep(_step switch
            {
                4 => 3,
                3 => StepReachable(2) ? 2 : 1,
                _ => 1,
            });
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_step)
            {
                case 1:
                    GoStep(_selectedLoader == "vanilla" ? 3 : 2);
                    break;
                case 2:
                    if (LoaderVersionBox.SelectedItem is null)
                    {
                        LoaderStatusText.Text = "请先从列表里选一个加载器版本（没有可用版本时无法继续）。";
                        return;
                    }
                    GoStep(3);
                    break;
                default:
                    GoStep(_step + 1);
                    break;
            }
        }

        private void UpdateSummary()
        {
            if (_item is null) return;
            var loaderName = _selectedLoader == "vanilla"
                ? "不装加载器（原版）"
                : FormatHelper.FormatLoaderName(_selectedLoader);
            var build = LoaderVersionBox.SelectedItem as LoaderBuild;
            var folder = GameInstanceLayout.Sanitize(GameNameBox.Text ?? "");
            if (string.IsNullOrWhiteSpace(folder)) folder = SuggestedName();

            var lines = new List<string>
            {
                $"Minecraft 版本：{_item.Title}",
                $"模组加载器：{loaderName}" + (build is null ? "" : $"  {build.Version}"),
            };
            if (_selectedLoader == "fabric")
                lines.Add($"Fabric API：{(FabricApiToggle.IsOn ? "一起安装" : "不装")}");
            lines.Add($"实例名称：{GameNameBox.Text}");
            lines.Add($"游戏目录：versions\\{folder}");
            SummaryText.Text = string.Join(Environment.NewLine, lines);
        }

        public void Cancel()
        {
            _loaderCts?.Cancel();
        }

        private void LoaderCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;
            _selectedLoader = tag;
            HighlightLoaderCards();
            // 不自动跳下一步：用户往往还要回头比较别的加载器，选中就翻页等于替人做决定。
            _ = LoadLoaderBuildsAsync();
        }

        private void HighlightLoaderCards()
        {
            StyleCard(CardVanilla, _selectedLoader == "vanilla");
            StyleCard(CardFabric, _selectedLoader == "fabric");
            StyleCard(CardForge, _selectedLoader == "forge");
            StyleCard(CardNeoForge, _selectedLoader == "neoforge");
            StyleCard(CardQuilt, _selectedLoader == "quilt");
        }

        private static void StyleCard(Button button, bool active)
        {
            // 必须走主题画刷，别写死 ARGB：写死的深色在浅色主题下会变成「深底 + 深字」看不见，
            // 而且选中边框也不会跟随用户在设置里挑的主题色。
            button.Background = ThemeService.Brush(active ? "AccentSoftBrush" : "ChipBackgroundBrush");
            button.BorderBrush = ThemeService.Brush(active ? "AccentBrush" : "DividerBrush");
            button.BorderThickness = new Thickness(active ? 2 : 1);
        }

        private async Task LoadLoaderBuildsAsync()
        {
            if (_item is null) return;
            var loader = _selectedLoader;
            if (loader is "vanilla" or null)
            {
                LoaderVersionPanel.Visibility = Visibility.Collapsed;
                FabricApiPanel.Visibility = Visibility.Collapsed;
                ShowAllLoaderToggle.Visibility = Visibility.Collapsed;
                SuggestGameName();
                return;
            }

            LoaderVersionPanel.Visibility = Visibility.Visible;
            FabricApiPanel.Visibility = loader == "fabric" ? Visibility.Visible : Visibility.Collapsed;
            // 四类加载器都可能有测试版（MC 26.x 的 NeoForge 目前只有 beta 构建），
            // 所以「显示全部」开关对所有加载器都开放，不再只给 Fabric / Quilt。
            ShowAllLoaderToggle.Visibility = Visibility.Visible;
            LoaderVersionLabel.Text = FormatHelper.FormatLoaderName(loader) + " 版本";

            // 切加载器时不清空 _loaderBuilds：缓存层（LoaderMetaApi.BuildCache）保证
            // 同 (loader, mc) 二次调用复用上次的 Task；如果本地 _loaderBuilds 恰好是
            // 同 mc 的同 loader 列表，先渲染它占位，避免「白屏 → 列表」闪烁。
            if (_loaderBuilds.Count > 0)
                BindLoaderVersions();

            LoaderStatusText.Text = "正在查询该游戏版本可用的加载器...";
            LoaderStatusText.Visibility = Visibility.Visible;
            _loaderCts?.Cancel();
            _loaderCts = new CancellationTokenSource();
            var token = _loaderCts.Token;

            try
            {
                var builds = await _loaderApi.GetBuildsAsync(loader, _item.Title, token);
                if (token.IsCancellationRequested) return;
                _loaderBuilds = builds;
                BindLoaderVersions();
                SuggestGameName();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // 拉取失败（网络抖动/限流）要和「该版本确实没有这个加载器」区分开，
                // 否则用户会误以为这个游戏版本不支持该加载器。
                if (_loaderBuilds.Count == 0)
                {
                    LoaderVersionBox.ItemsSource = null;
                    LoaderStatusText.Text = "加载器列表获取失败：" + ex.Message + "　点击此处重试";
                    LoaderStatusText.Visibility = Visibility.Visible;
                }
                else
                {
                    // 已有缓存：保留显示，但提示后台刷新失败，避免用户以为当前列表是最新的
                    LoaderStatusText.Text = "后台刷新失败：" + ex.Message + "（仍显示上次缓存）";
                }
            }
        }

        private void LoaderStatusText_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_loaderBuilds.Count == 0 && _selectedLoader is not ("vanilla" or null))
            {
                // 失败重试前先清掉 (loader, mc) 维度的缓存，避免拉的还是上次失败时的占位
                LoaderMetaApi.ClearCache(_selectedLoader, _item?.Title);
                _ = LoadLoaderBuildsAsync();
            }
        }

        private void ShowAllLoaderToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loaderBuilds.Count == 0) return;
            BindLoaderVersions();
        }

        private void BindLoaderVersions()
        {
            IEnumerable<LoaderBuild> list = _loaderBuilds;
            if (ShowAllLoaderToggle.IsOn == false)
            {
                // 优先用元数据算出的 Stable（Fabric 有 stable 字段、其余按版本号语义判断）；
                // 一个稳定版都没有时不过滤，否则 MC 26.x 这类只有 beta 构建的版本会显示成空列表。
                var stable = _loaderBuilds.Where(b => b.Stable).ToList();
                if (stable.Count == 0)
                {
                    stable = _loaderBuilds.Where(b =>
                        !b.Version.Contains("beta", StringComparison.OrdinalIgnoreCase)
                        && !b.Version.Contains("pre", StringComparison.OrdinalIgnoreCase)
                        && !b.Version.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                        && !b.Version.Contains("rc", StringComparison.OrdinalIgnoreCase)).ToList();
                }
                if (stable.Count > 0) list = stable;
            }

            var bound = list.ToList();
            if (bound.Count == 0)
            {
                LoaderStatusText.Text = $"该 Minecraft 版本暂无 {FormatHelper.FormatLoaderName(_selectedLoader)}";
                LoaderVersionBox.ItemsSource = null;
                return;
            }

            LoaderVersionBox.ItemsSource = bound;
            LoaderVersionBox.DisplayMemberPath = nameof(LoaderBuild.Display);
            LoaderVersionBox.SelectedIndex = 0;
            var loaderName = FormatHelper.FormatLoaderName(_selectedLoader);
            var hasBeta = _loaderBuilds.Any(b => !b.Stable);
            LoaderStatusText.Text = ShowAllLoaderToggle.IsOn
                ? $"共 {bound.Count} 个 {loaderName} 版本（含测试版），可单独选择"
                : hasBeta
                    ? $"共 {bound.Count} 个 {loaderName} 正式版，可单独选择；打开开关可看测试版"
                    : $"共 {bound.Count} 个 {loaderName} 版本，可单独选择";
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item is null || _installing) return;

            var loader = _selectedLoader;
            LoaderBuild? build = null;
            if (loader != "vanilla")
            {
                build = LoaderVersionBox.SelectedItem as LoaderBuild;
                if (build is null)
                {
                    // 回第 2 步去选，而不是在确认页提示「请先选择」——用户在这儿没有可选项
                    GoStep(2);
                    LoaderStatusText.Text = "请先选择加载器版本。";
                    return;
                }
            }

            if (_item.IsAlpha && !await ConfirmAlphaAsync())
                return;

            var instanceName = GameNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(instanceName))
                instanceName = SuggestedName();

            _installing = true;
            _installStarted = true;
            // 安装是独立一步：进来就只有「完成」，不再留可重复触发的下载按钮
            GoStep(5);
            InstallRing.IsActive = true;
            Step5Title.Text = "正在安装…";
            InstallButton.IsEnabled = false;
            InstallButtonText.Text = "正在加入队列...";
            ShowResult(null);

            var vanilla = new MinecraftVersion
            {
                Id = _item.Title,
                Type = _item.VersionType,
                Url = _item.DownloadUrl,
                ReleaseTime = _item.Meta
            };

            var title = loader == "vanilla"
                ? instanceName
                : $"{instanceName}  ·  {FormatHelper.FormatLoaderName(loader)}";
            var task = DownloadService.Instance.StartInstall(title, vanilla.Id);
            BindInstallTask(task);

            ShowTip("已开始安装，下方会显示详细步骤，也可到「消息」查看。");

            try
            {
                var installedId = await _installer.InstallAsync(
                    vanilla, loader, build, task, instanceName,
                    installFabricApi: loader == "fabric" && FabricApiToggle.IsOn,
                    cancellationToken: task.Token);
                InstalledVersionService.Invalidate();
                LauncherSettings.Instance.SelectedVersion = installedId;
                ShowResult("success", "安装完成",
                    $"{instanceName} 已设为当前版本（目录 versions\\{installedId}），回首页即可启动。");
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                ShowResult("cancel", "已终止下载", "任务已取消，可重新点「下载并安装」。");
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Install", $"安装失败：{title}", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Progress = 0;
                    t.Detail = ex.Message;
                    t.ProgressHint = "安装失败";
                });
                ShowResult("fail", "安装失败", ex.Message);
            }
            finally
            {
                UnbindInstallTask();
                _installing = false;
                InstallButton.IsEnabled = true;
                InstallButtonText.Text = "下载并安装";
                InstallRing.IsActive = false;
                Step5Title.Text = "安装已结束";
            }
        }

        private void FinishButton_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private async Task<bool> ConfirmAlphaAsync()
        {
            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "远古 / Alpha 版本提示",
                "该版本属于远古 Alpha，可能无法在当前 Java 上运行，也更容易出现兼容性问题。确定仍要下载吗？",
                primary: "仍要下载",
                close: "取消");
            return result == AppDialogResult.Primary;
        }

        private void LoaderVersionBox_SelectionChanged(object sender, EventArgs e)
            => SuggestGameName();

        private void GameNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingName) _nameTouched = true;
            UpdateFolderHint();
        }

        private void SuggestGameName()
        {
            if (_nameTouched || _item is null) { UpdateFolderHint(); return; }
            _updatingName = true;
            GameNameBox.Text = SuggestedName();
            _updatingName = false;
            UpdateFolderHint();
        }

        private string SuggestedName()
        {
            if (_item is null) return "Minecraft";
            var loader = _selectedLoader;
            var build = LoaderVersionBox.SelectedItem as LoaderBuild;
            return GameInstanceLayout.MakeId(_item.Title, loader, build?.Version);
        }

        private void UpdateFolderHint()
        {
            var folder = GameInstanceLayout.Sanitize(GameNameBox.Text ?? "");
            if (string.IsNullOrWhiteSpace(folder)) folder = SuggestedName();
            FolderHintText.Text = $"游戏目录将同步为  versions\\{folder}";
        }

        private void BindInstallTask(DownloadTask task)
        {
            UnbindInstallTask();
            _installTask = task;
            task.PropertyChanged += OnInstallTaskChanged;
            UpdateInstallSteps(task);
        }

        private void UnbindInstallTask()
        {
            if (_installTask is null) return;
            _installTask.PropertyChanged -= OnInstallTaskChanged;
            _installTask = null;
        }

        private void OnInstallTaskChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not DownloadTask task) return;
            if (e.PropertyName is not (nameof(DownloadTask.StepLog) or nameof(DownloadTask.StepTitle) or nameof(DownloadTask.ShowSteps) or null))
                return;
            DispatcherQueue.TryEnqueue(() => UpdateInstallSteps(task));
        }

        private void UpdateInstallSteps(DownloadTask task)
        {
            if (InstallStepText is null) return;
            if (!task.ShowSteps || string.IsNullOrWhiteSpace(task.StepLog))
            {
                InstallStepText.Visibility = Visibility.Collapsed;
                return;
            }

            InstallStepText.Text = task.StepTitle + Environment.NewLine + task.StepLog;
            InstallStepText.Visibility = Visibility.Visible;
            QueueTip.Text = task.StepTitle;
            QueueTipBorder.Visibility = Visibility.Visible;
        }

        private void ShowTip(string message)
        {
            QueueTip.Text = message;
            QueueTipBorder.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 安装结果横幅（success / fail / cancel，null 收起）。
        /// 系统 Toast 弹的是「下载完成 / 名称」，这页要给同一句 —— 否则结果只能去「消息」页确认。
        /// </summary>
        private void ShowResult(string? kind, string? title = null, string? detail = null)
        {
            if (kind is null)
            {
                ResultBanner.Visibility = Visibility.Collapsed;
                return;
            }

            var (glyph, color) = kind switch
            {
                "success" => ("\uF298", Color.FromArgb(255, 63, 185, 80)),
                "fail" => ("\uF3F0", Color.FromArgb(255, 229, 72, 77)),
                _ => ("\uF36D", Color.FromArgb(255, 128, 128, 128)),
            };
            ResultIcon.Glyph = glyph;
            ResultIcon.Foreground = new SolidColorBrush(color);
            ResultTitleText.Text = title ?? "";
            ResultDetailText.Text = detail ?? "";
            ResultDetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
            ResultBanner.Visibility = Visibility.Visible;
        }

        private static Color ParseColor(string hex)
        {
            if (hex.StartsWith("#")) hex = hex[1..];
            if (hex.Length == 6)
            {
                var r = Convert.ToByte(hex[..2], 16);
                var g = Convert.ToByte(hex[2..4], 16);
                var b = Convert.ToByte(hex[4..], 16);
                return Color.FromArgb(255, r, g, b);
            }
            return Color.FromArgb(255, 91, 135, 49);
        }
    }
}
