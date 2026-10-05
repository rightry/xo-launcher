using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using XO_Launcher.Api;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class ModDetailPage : UserControl
    {
        private readonly ModrinthApi _api = new();
        private readonly CurseForgeApi _curseForgeApi = new();
        private CancellationTokenSource? _cts;
        private string _currentTitle = "";
        private string _iconUrl = "";
        private string _projectType = "";
        private string _projectId = "";
        private string _itemKind = "";
        private int _curseForgeId;
        private string _channel = "release";
        private IReadOnlyList<ModrinthVersion> _allVersions = Array.Empty<ModrinthVersion>();
        private DownloadTask? _installTask;
        private string _pendingBody = "";
        private bool _bodyRendered;
        private int _loadGeneration;

        public event EventHandler? BackRequested;

        public ModDetailPage()
        {
            InitializeComponent();
            IconImage.ImageFailed += (_, _) =>
            {
                IconImage.Source = null;
                IconFallback.Visibility = Visibility.Visible;
            };
            BackButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
            UpdateChannelButtons();
            UpdateLibraryPath();
        }

        /// <summary>加载 mod 详情并展示。在用户点击列表项后调用。</summary>
        public async Task ShowAsync(DownloadItem item)
        {
            _currentTitle = item.Title;
            _iconUrl = item.IconUrl ?? "";
            _projectType = item.ProjectType;
            _itemKind = item.ItemKind;
            _curseForgeId = item.CurseForgeId;
            _channel = "release";
            _allVersions = Array.Empty<ModrinthVersion>();
            _pendingBody = "";
            _bodyRendered = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var generation = Interlocked.Increment(ref _loadGeneration);

            TitleText.Text = item.Title;
            // 只显示译名，不再附英文原名（和列表一致：汉化后原名对用户没意义）
            BadgeText.Text = item.Badge;
            StatsText.Text = item.Meta;
            DescriptionText.Text = item.Description;
            BodyMarkdown.Blocks.Clear();
            LoaderItemsText.Text = "";
            GameVersionItemsText.Text = "";
            VersionGroups.ItemsSource = null;
            EmptyVersionText.Visibility = Visibility.Collapsed;
            QueueTipBorder.Visibility = Visibility.Collapsed;
            IconFallback.Visibility = Visibility.Visible;
            IconFallback.Text = item.IconText;
            CollapseIntro();
            UpdateChannelButtons();
            UpdateLibraryPath();

            if (item.Icon is not null)
            {
                IconImage.Source = item.Icon;
                IconFallback.Visibility = Visibility.Collapsed;
            }
            else
            {
                IconImage.Source = null;
                IconBorder.Background = new SolidColorBrush(ParseColor(item.IconColor));
            }

            LoadingPanel.Visibility = Visibility.Visible;
            ContentScrollViewer.Visibility = Visibility.Collapsed;

            try
            {
                if (item.ItemKind == "curseforge")
                    await LoadCurseForgeAsync(item, token);
                else
                    await LoadModrinthAsync(item, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LauncherLogService.Error("ModDetail", "加载模组版本列表失败", ex);
                MarkdownRenderer.Render(BodyMarkdown, "");
                EmptyVersionText.Text = "加载失败：" + ex.Message;
                EmptyVersionText.Visibility = Visibility.Visible;
            }
            finally
            {
                if (generation == _loadGeneration && !token.IsCancellationRequested)
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    ContentScrollViewer.Visibility = Visibility.Visible;
                }
            }
        }

        private async Task LoadModrinthAsync(DownloadItem item, CancellationToken token)
        {
            var id = !string.IsNullOrWhiteSpace(item.ProjectId) ? item.ProjectId
                : !string.IsNullOrWhiteSpace(item.Slug) ? item.Slug
                : throw new InvalidOperationException("缺少项目 ID。");
            var projectTask = _api.GetProjectAsync(id, token);
            var versionsTask = _api.GetVersionsAsync(id, token);

            ModrinthProject? project = null;
            IReadOnlyList<ModrinthVersion> versions = Array.Empty<ModrinthVersion>();
            Exception? loadError = null;

            try { project = await projectTask; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                loadError = ex;
                LauncherLogService.Error("ModDetail", "加载 Modrinth 项目信息失败", ex);
            }

            try { versions = await versionsTask; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                loadError = ex;
                LauncherLogService.Error("ModDetail", "加载 Modrinth 版本列表失败", ex);
            }

            if (project is null && versions.Count == 0)
                throw loadError ?? new InvalidOperationException("无法加载该项目。");

            if (project is not null)
            {
                if (!string.IsNullOrWhiteSpace(project.Id))
                    _projectId = project.Id;
                if (!string.IsNullOrWhiteSpace(project.ProjectType))
                    _projectType = project.ProjectType;

                StatsText.Text = $"下载 {FormatHelper.FormatCompactNumber(project.Downloads)} · 关注 {FormatHelper.FormatCompactNumber(project.Followers)} · 更新于 {FormatHelper.FormatDate(project.Updated)}";
                DescriptionText.Text = project.Description;
                _pendingBody = project.Body ?? "";
                CollapseIntro();
                LoaderItemsText.Text = project.Loaders.Count > 0
                    ? string.Join("  ·  ", project.Loaders)
                    : "未标明";
                var games = project.GameVersions.Take(16).ToList();
                GameVersionItemsText.Text = games.Count > 0
                    ? string.Join("  ·  ", games) + (project.GameVersions.Count > games.Count ? "  …" : "")
                    : "未标明";
                UpdateLibraryPath();
            }

            _allVersions = versions;
            ApplyChannelFilter();
            if (versions.Count == 0 && loadError is not null)
            {
                EmptyVersionText.Text = "版本列表加载失败：" + loadError.Message;
                EmptyVersionText.Visibility = Visibility.Visible;
            }
        }

        private async Task LoadCurseForgeAsync(DownloadItem item, CancellationToken token)
        {
            var id = _curseForgeId > 0 ? _curseForgeId : item.CurseForgeId;
            var modTask = _curseForgeApi.GetModAsync(id, token);
            var filesTask = _curseForgeApi.GetFilesAsync(id, token);

            CurseForgeMod? mod = null;
            IReadOnlyList<CurseForgeFile> files = Array.Empty<CurseForgeFile>();
            Exception? loadError = null;

            try { mod = await modTask; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                loadError = ex;
                LauncherLogService.Error("ModDetail", "加载 CurseForge 项目信息失败", ex);
            }

            try { files = await filesTask; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                loadError = ex;
                LauncherLogService.Error("ModDetail", "加载 CurseForge 文件列表失败", ex);
            }

            if (mod is null && files.Count == 0)
                throw loadError ?? new InvalidOperationException("无法加载该项目。");

            _projectId = id.ToString();
            if (mod is not null)
            {
                StatsText.Text = $"下载 {FormatHelper.FormatCompactNumber(mod.DownloadCount)} · CurseForge · 更新于 {mod.DateModified:yyyy-MM-dd}";
                DescriptionText.Text = mod.Summary;
                _pendingBody = string.IsNullOrWhiteSpace(mod.Summary) ? "" : mod.Summary;
                CollapseIntro();
                var cats = mod.Categories.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(8).ToList();
                LoaderItemsText.Text = cats.Count > 0 ? string.Join("  ·  ", cats) : "CurseForge";
                GameVersionItemsText.Text = "版本列表按游戏版本与加载器分组";
                UpdateLibraryPath();
            }

            _allVersions = files.Select(f => CurseForgeApi.ToVersion(id, f)).ToList();
            ApplyChannelFilter();
            if (files.Count == 0 && loadError is not null)
            {
                EmptyVersionText.Text = "文件列表加载失败：" + loadError.Message;
                EmptyVersionText.Visibility = Visibility.Visible;
            }
        }

        // 详情页背景图（原 CoverBanner + LoadCover + ApplyCoverHeight）已整体删除：
        // 现在详情页不显示任何背景图，也不再为此下载图片。

        private void IntroToggle_Click(object sender, RoutedEventArgs e)
        {
            var open = IntroBody.Visibility != Visibility.Visible;
            IntroBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            IntroChevron.Glyph = open ? "\uF2A3" : "\uF2B0";
            if (open)
                EnsureIntroRendered();
        }

        private void EnsureIntroRendered()
        {
            if (_bodyRendered) return;
            _bodyRendered = true;
            try
            {
                MarkdownRenderer.Render(BodyMarkdown, _pendingBody);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("ModDetail", "渲染项目介绍失败", ex);
                BodyMarkdown.Blocks.Clear();
                try
                {
                    MarkdownRenderer.Render(BodyMarkdown, DescriptionText.Text);
                }
                catch
                {
                    BodyMarkdown.Blocks.Clear();
                }
            }
        }

        private void CollapseIntro()
        {
            if (IntroBody is null) return;
            IntroBody.Visibility = Visibility.Collapsed;
            if (IntroChevron is not null)
                IntroChevron.Glyph = "\uF2B0";
        }

        private void ChannelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string channel }) return;
            if (_channel == channel) return;
            _channel = channel;
            UpdateChannelButtons();
            ApplyChannelFilter();
        }

        private void UpdateChannelButtons()
        {
            VersionSectionTitle.Text = _channel == "snapshot"
                ? "按游戏版本与加载器选择快照版"
                : "按游戏版本与加载器选择正式版";

            foreach (var button in new[] { ChannelReleaseButton, ChannelSnapshotButton })
            {
                var active = button.Tag?.ToString() == _channel;
                button.Foreground = active
                    ? ThemeService.Brush("AccentBrush")
                    : ThemeService.Brush("GhostButtonForeground");
            }
        }

        private void UpdateLibraryPath()
        {
            var noun = ContentKind.DisplayName(_projectType);
            var dir = ModLibraryService.GetDirectory(_projectType);
            LibraryPathText.Text = $"保存位置：{dir}";
            VersionSectionHint.Text = _itemKind == "curseforge"
                ? _projectType == ContentKind.Modpack
                    ? "CurseForge 整合包将保存到下载库（zip）。自动安装目前仅支持 Modrinth 的 .mrpack。"
                    : $"文件将保存至资源下载目录。请在版本设置的「{ContentKind.ManagementTitle(_projectType)}」中选择并安装。"
                : _projectType == ContentKind.Modpack
                    ? "选择游戏版本后，将先安装对应的 Minecraft 与加载器，再下载整合包所需模组。进度请在「消息」中查看。"
                    : $"文件将保存至资源下载目录，不会自动安装。请在版本设置的「{ContentKind.ManagementTitle(_projectType)}」中选择并安装。进度请在「消息」中查看。";
            if (IntroTitleText is not null)
                IntroTitleText.Text = noun + "介绍";
            if (LoadingStatusText is not null)
                LoadingStatusText.Text = "正在加载详情…";
        }

        private void ApplyChannelFilter()
        {
            var groups = BuildGroups(_allVersions, _channel);
            VersionGroups.ItemsSource = groups;
            if (groups.Count == 0)
            {
                EmptyVersionText.Text = _channel == "snapshot"
                    ? "暂无适用于快照版的文件"
                    : "暂无适用于 Minecraft 正式版的文件";
                EmptyVersionText.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyVersionText.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>模组/光影等加入下载库；整合包则按所选游戏版本安装游戏后再下模组。</summary>
        private async void DownloadVersionButton_Click(object sender, RoutedEventArgs e)
        {
            var version = (sender as Button)?.Tag as ModrinthVersion
                          ?? (sender as Button)?.DataContext as ModrinthVersion;
            if (version is null)
            {
                ShowQueueTip("无法识别所选文件。");
                return;
            }

            var file = version.PrimaryFile;
            if (file is null || string.IsNullOrWhiteSpace(file.Filename))
            {
                ShowQueueTip("该版本暂无可下载文件。");
                return;
            }

            var url = ResolveFileUrl(version, file);
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowQueueTip("下载地址为空，请稍后重试。");
                return;
            }

            if (_projectType == ContentKind.Modpack && _itemKind != "curseforge"
                && file.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            {
                await InstallModpackAsync(version, file, url);
                return;
            }

            if (DownloadService.Instance.IsDuplicate(file.Filename))
            {
                ShowQueueTip("该文件已在下载队列中，请至「消息」查看进度。");
                return;
            }

            var dest = ModLibraryService.GetDirectory(_projectType);
            try
            {
                DownloadService.Instance.Enqueue(
                    _currentTitle,
                    file.Filename,
                    url,
                    file.Size,
                    dest,
                    _projectType);
                // 与「一键下载含前置」一样留下溯源信息，资源库才能按版本分类并回头查新版本。
                LibraryIndexService.RecordDownload(
                    _projectType, file.Filename,
                    string.IsNullOrWhiteSpace(version.ProjectId) ? "" : version.ProjectId,
                    version.Id, version.Loaders.FirstOrDefault() ?? "", version.Loaders, _currentTitle,
                    version.VersionNumber, version.GameVersions,
                    file.Hashes is { } hashes && hashes.TryGetValue("sha1", out var sha) ? sha : "",
                    url);
                ShowQueueTip($"已加入下载队列：{file.Filename}。完成后将保存至下载库，请在版本设置中安装。");
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("ModDetail", "加入资源下载失败", ex);
                ShowQueueTip("无法开始下载：" + ex.Message);
            }
        }

        /// <summary>一键安装：解析该版本的必需前置，连同本体一起下载进资源库（仅 Modrinth）。</summary>
        private async void OneClickInstallButton_Click(object sender, RoutedEventArgs e)
        {
            var version = (sender as Button)?.Tag as ModrinthVersion
                          ?? (sender as Button)?.DataContext as ModrinthVersion;
            if (version is null)
            {
                ShowQueueTip("无法识别所选文件。");
                return;
            }
            if (version.PrimaryFile is null || string.IsNullOrWhiteSpace(version.PrimaryFile.Url))
            {
                ShowQueueTip("该版本暂无可下载文件。");
                return;
            }

            var gameVersion = version.GameVersions.FirstOrDefault() ?? "";
            var loader = version.Loaders.FirstOrDefault(l => l is "fabric" or "quilt" or "forge" or "neoforge")
                         ?? version.Loaders.FirstOrDefault() ?? "";

            ShowQueueTip("正在解析前置模组…");
            List<ModDownloadPlan> plans;
            try
            {
                plans = await ModDependencyService.ResolveAsync(_api, version, gameVersion, loader, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("ModDetail", "解析前置模组失败", ex);
                ShowQueueTip("解析前置失败：" + ex.Message);
                return;
            }

            if (plans.Count == 0)
            {
                ShowQueueTip("没有可下载的文件。");
                return;
            }

            var fingerprint = ModDependencyService.ScanLibrary(_projectType);
            var fresh = plans.Where(p => !fingerprint.Contains(p)).ToList();
            var inLibrary = plans.Count - fresh.Count;

            var depCount = plans.Count(p => p.IsDependency);
            // 前置名也走汉化：一键安装弹窗里中英文混排时，用户更容易认出「这个前置我装过没有」
            var lines = fresh.Take(40)
                .Select(p => (p.IsDependency ? "前置 · " : "本体 · ")
                    + ModNameTranslationService.Translate(p.ProjectTitle, p.ProjectTitle)
                    + "（" + p.Filename + "）");
            var head = (fresh.Count > 0
                    ? $"将下载 {fresh.Count} 个文件到资源库"
                    : "没有新文件需要下载")
                       + (depCount > 0 ? $"（共 {plans.Count} 个，含 {depCount} 个前置模组）" : $"（共 {plans.Count} 个，无前置）")
                       + (inLibrary > 0 ? $"\n已有 {inLibrary} 个在资源库中，将自动跳过：\n" : "：\n\n");
            var more = fresh.Count > 40 ? $"\n…等共 {fresh.Count} 个" : "";
            var body = head + string.Join("\n", lines) + more;

            if (fresh.Count == 0)
            {
                ShowQueueTip($"这些文件（含前置）都已在资源库中，无需重复下载。");
                return;
            }

            var result = await AppDialog.ShowAsync(
                XamlRoot, "一键安装（含前置）", body,
                primary: "下载", close: "取消");
            if (result != AppDialogResult.Primary) return;

            var enqueue = ModDependencyService.EnqueueToLibrary(plans, _projectType, fingerprint);
            ShowQueueTip(enqueue.Added > 0
                ? $"已加入下载队列：{enqueue.Added} 个文件"
                  + (enqueue.InLibrary > 0 ? $"（{enqueue.InLibrary} 个已在库中跳过）" : "")
                  + "。完成后请在版本设置的「" + ContentKind.ManagementTitle(_projectType) + "」中安装。"
                : "没有新文件需要下载（已在库中或队列中）。");
        }

        private async Task InstallModpackAsync(ModrinthVersion version, ModrinthVersionFile file, string url)
        {
            if (DownloadService.Instance.IsDuplicate(file.Filename))
            {
                ShowQueueTip("该整合包已在安装队列中，请至「消息」查看进度。");
                return;
            }

            var mc = version.GameVersions.FirstOrDefault() ?? "";
            var loader = version.Loaders.FirstOrDefault(l => l is "fabric" or "quilt" or "forge" or "neoforge") ?? "";
            var task = DownloadService.Instance.StartInstall(
                _currentTitle,
                string.IsNullOrWhiteSpace(mc) ? file.Filename : _currentTitle + " " + mc);
            BindInstallTask(task);
            ShowQueueTip("已开始安装整合包：先安装 " + mc + (string.IsNullOrWhiteSpace(loader) ? "" : " / " + FormatHelper.FormatLoaderName(loader)) + "，再下载模组。");
            try
            {
                var id = await ModpackInstallService.InstallAsync(
                    _currentTitle, version, url, task, task.Token, _iconUrl);
                ShowQueueTip("整合包已安装：" + id + "。可返回首页启动。");
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                ShowQueueTip("已取消整合包安装。");
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Modpack", "安装整合包失败", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Detail = ex.Message;
                    t.ProgressHint = "安装失败";
                });
                ShowQueueTip("整合包安装失败：" + ex.Message);
            }
            finally
            {
                UnbindInstallTask();
            }
        }

        private string ResolveFileUrl(ModrinthVersion version, ModrinthVersionFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.Url)) return file.Url;
            var projectId = !string.IsNullOrWhiteSpace(version.ProjectId) ? version.ProjectId : _projectId;
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(file.Filename))
                return "";
            return $"https://cdn.modrinth.com/data/{projectId}/versions/{version.Id}/{Uri.EscapeDataString(file.Filename)}";
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

        private void ShowQueueTip(string message)
        {
            QueueTip.Text = message;
            QueueTipBorder.Visibility = Visibility.Visible;
        }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        private static List<McVersionGroup> BuildGroups(IReadOnlyList<ModrinthVersion> versions, string channel)
        {
            bool includeMc(string mc)
            {
                if (mc == "其他版本") return channel == "release";
                return channel == "snapshot"
                    ? FormatHelper.IsMinecraftSnapshot(mc)
                    : FormatHelper.IsMinecraftRelease(mc);
            }

            var filtered = versions
                .Where(v => v.PrimaryFile is not null && IsPublicStatus(v.Status))
                .ToList();

            var groups = filtered
                .SelectMany(v =>
                {
                    var mcs = v.GameVersions.Count > 0 ? v.GameVersions : new List<string> { "其他版本" };
                    var loaders = v.Loaders.Count > 0 ? v.Loaders : new List<string> { "其他加载器" };
                    return mcs
                        .Where(includeMc)
                        .SelectMany(mc => loaders.Select(loader => (Mc: mc, Loader: loader, Version: v)));
                })
                .GroupBy(x => x.Mc, StringComparer.OrdinalIgnoreCase)
                .Select(mcGroup => new McVersionGroup
                {
                    MinecraftVersion = mcGroup.Key,
                    Loaders = mcGroup
                        .GroupBy(x => x.Loader, StringComparer.OrdinalIgnoreCase)
                        .Select(lg => new LoaderVersionGroup
                        {
                            LoaderId = lg.Key,
                            LoaderName = FormatHelper.FormatLoaderName(lg.Key),
                            Versions = lg.Select(x => x.Version)
                                .DistinctBy(v => v.Id)
                                .OrderByDescending(v => v.DatePublished)
                                .ToList()
                        })
                        .OrderBy(lg => FormatHelper.LoaderSortOrder(lg.LoaderId))
                        .ThenBy(lg => lg.LoaderName, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                })
                .OrderBy(g => g.MinecraftVersion == "其他版本")
                .ThenByDescending(g => g.MinecraftVersion, Comparer<string>.Create(FormatHelper.CompareMcVersion))
                .ToList();

            var installedMc = new HashSet<string>(
                InstalledVersionService.Scan().Select(v => v.MinecraftVersion),
                StringComparer.OrdinalIgnoreCase);

            var anyInstalled = false;
            for (var i = 0; i < groups.Count; i++)
            {
                groups[i].IsExpanded = installedMc.Contains(groups[i].MinecraftVersion);
                if (groups[i].IsExpanded) anyInstalled = true;
            }

            if (!anyInstalled && groups.Count > 0)
                groups[0].IsExpanded = true;

            foreach (var group in groups.Where(g => g.IsExpanded))
            {
                if (group.Loaders.Count > 0)
                    group.Loaders[0].IsExpanded = true;
            }

            return groups;
        }

        private static bool IsPublicStatus(string? status) =>
            string.IsNullOrEmpty(status) || status is "listed" or "approved" or "archived";

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
            return Color.FromArgb(255, 45, 45, 45);
        }
    }
}
