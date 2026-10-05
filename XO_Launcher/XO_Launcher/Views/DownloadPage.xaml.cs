using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using XO_Launcher.Api;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class DownloadPage : UserControl
    {
        private readonly ModrinthApi _modrinthApi = new();
        private readonly CurseForgeApi _curseForgeApi = new();
        private readonly MojangApi _mojangApi = new();
        private readonly BedrockApi _bedrockApi = new();
        private readonly LoaderMetaApi _loaderApi = new();
        private readonly ObservableCollection<DownloadItem> _visibleItems = new();
        private CancellationTokenSource? _loadCancellation;
        private string _currentCategory = "vanilla";
        private string _vanillaTypeFilter = "all";
        private string _bedrockTypeFilter = "all";
        private IReadOnlyList<DownloadItem> _allVanillaItems = Array.Empty<DownloadItem>();
        private IReadOnlyList<DownloadItem> _allBedrockItems = Array.Empty<DownloadItem>();
        private ScrollViewer? _listScroll;
        private int _modrinthOffset;
        private long _modrinthTotal;
        private bool _loadingMore;
        private bool _modrinthHasMore;
        private string _modrinthQuery = "";
        private string _resourceSource = "modrinth";
        private string _resourceSort = "downloads";
        private string _resourceCategoryId = "";
        private bool _syncingResourceFilters;
        private bool _started;
        private DispatcherTimer? _searchDebounce;
        private int _iconLoadGeneration;

        public DownloadPage()
        {
            InitializeComponent();
            ItemsList.ItemsSource = _visibleItems;
            VanillaButton.Click += CategoryButton_Click;
            BedrockButton.Click += CategoryButton_Click;
            LoaderButton.Click += CategoryButton_Click;
            ModsButton.Click += CategoryButton_Click;
            ShadersButton.Click += CategoryButton_Click;
            ResourcePacksButton.Click += CategoryButton_Click;
            ModpacksButton.Click += CategoryButton_Click;
            SearchBox.QuerySubmitted += SearchBox_QuerySubmitted;
            SearchBox.TextChanged += SearchBox_TextChanged;
            SearchBox.Cleared += SearchBox_Cleared;
            DetailPage.BackRequested += (_, _) => CloseDetail();
            VanillaDetail.BackRequested += (_, _) => CloseDetail();
            BedrockDetail.BackRequested += (_, _) => CloseDetail();
        }

        public void EnsureLoaded()
        {
            if (_started) return;
            _started = true;
            _ = LoadCategoryAsync("vanilla");
        }

        /// <summary>原版进入版本详情；Modrinth 的 mod 进入详情页。</summary>
        private async void ItemsGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not DownloadItem item)
                return;

            if (item.ItemKind == "vanilla")
            {
                OpenDetail(VanillaDetail);
                VanillaDetail.Show(item);
                return;
            }

            if (item.ItemKind == "bedrock")
            {
                OpenDetail(BedrockDetail);
                BedrockDetail.Show(item);
                return;
            }

            if (item.ItemKind == "curseforge")
            {
                if (item.CurseForgeId <= 0 && item.Slug.Length == 0)
                    return;
                OpenDetail(DetailPage);
                await DetailPage.ShowAsync(item);
                return;
            }

            if (string.IsNullOrWhiteSpace(item.Slug) && string.IsNullOrWhiteSpace(item.ProjectId))
                return;

            OpenDetail(DetailPage);
            await DetailPage.ShowAsync(item);
        }

        /// <summary>
        /// 打开二级详情页。详情页背景在全景模式下被 ThemeService 改成透明，
        /// 所以必须把一级的分类顶栏与整个列表区折叠，否则列表、搜索框、筛选条会透出来叠在详情上。
        /// </summary>
        private void OpenDetail(FrameworkElement detail)
        {
            DetailPage.Visibility = ReferenceEquals(detail, DetailPage) ? Visibility.Visible : Visibility.Collapsed;
            VanillaDetail.Visibility = ReferenceEquals(detail, VanillaDetail) ? Visibility.Visible : Visibility.Collapsed;
            BedrockDetail.Visibility = ReferenceEquals(detail, BedrockDetail) ? Visibility.Visible : Visibility.Collapsed;
            CategoryBar.Visibility = Visibility.Collapsed;
            ListRegion.Visibility = Visibility.Collapsed;
            DetailOverlay.Visibility = Visibility.Visible;
        }

        private void CloseDetail()
        {
            DetailPage.Cancel();
            VanillaDetail.Cancel();
            BedrockDetail.Cancel();
            DetailOverlay.Visibility = Visibility.Collapsed;
            VanillaDetail.Visibility = Visibility.Collapsed;
            BedrockDetail.Visibility = Visibility.Collapsed;
            DetailPage.Visibility = Visibility.Visible;
            CategoryBar.Visibility = Visibility.Visible;
            ListRegion.Visibility = Visibility.Visible;
        }

        private void VanillaFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string filter })
            {
                _vanillaTypeFilter = filter;
                ApplyVanillaFilter();
            }
        }

        private async void CategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string category)
                await LoadCategoryAsync(category);
        }

        private async void SearchBox_QuerySubmitted(object? sender, EventArgs e)
            => await RunSearchAsync();

        private async void SearchBox_Cleared(object? sender, EventArgs e)
            => await RunSearchAsync();

        private void SearchBox_TextChanged(object? sender, EventArgs e)
        {
            if (_currentCategory is not ("vanilla" or "bedrock" or "loader"))
                return;

            _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            _searchDebounce.Tick -= SearchDebounce_Tick;
            _searchDebounce.Tick += SearchDebounce_Tick;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private void SearchDebounce_Tick(object? sender, object e)
        {
            _searchDebounce?.Stop();
            if (_currentCategory == "vanilla")
                ApplyVanillaFilter();
            else if (_currentCategory == "bedrock")
                ApplyBedrockFilter();
            else if (_currentCategory == "loader")
                ApplyLoaderFilter();
        }

        private async Task RunSearchAsync()
        {
            if (_currentCategory == "vanilla")
            {
                ApplyVanillaFilter();
                return;
            }
            if (_currentCategory == "bedrock")
            {
                ApplyBedrockFilter();
                return;
            }
            if (_currentCategory == "loader")
            {
                ApplyLoaderFilter();
                return;
            }
            await LoadCategoryAsync(_currentCategory);
        }

        private async Task LoadCategoryAsync(string category)
        {
            CloseDetail();
            _currentCategory = category;
            _iconLoadGeneration++;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = new CancellationTokenSource();
            var token = _loadCancellation.Token;

            UpdateCategoryState(category);
            _visibleItems.Clear();
            HideLoadMore();
            var isCatalog = category is "mod" or "shader" or "resourcepack" or "modpack";
            VanillaFilterPanel.Visibility = category == "vanilla" ? Visibility.Visible : Visibility.Collapsed;
            BedrockFilterPanel.Visibility = category == "bedrock" ? Visibility.Visible : Visibility.Collapsed;
            ResourceFilterPanel.Visibility = isCatalog ? Visibility.Visible : Visibility.Collapsed;
            if (isCatalog)
                BindResourceFilters(category);
            ItemsList.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            SourceStatus.Text = "正在获取最新内容...";

            try
            {
                var query = SearchBox.Text;
                if (category is "mod" or "shader" or "resourcepack" or "modpack")
                {
                    query = ModNameTranslationService.ResolveSearchQuery(query);
                    await LoadCatalogItemsAsync(category, query, token);
                }
                else
                {
                    var items = category switch
                    {
                        "vanilla" => await LoadVanillaAsync(token),
                        "bedrock" => await LoadBedrockAsync(token),
                        "loader" => CreateLoaderItems(),
                        _ => Array.Empty<DownloadItem>()
                    };

                    if (category == "vanilla")
                    {
                        _allVanillaItems = items;
                        ApplyVanillaFilter();
                        SourceStatus.Text = $"Mojang 官方清单 · {items.Count} 个版本";
                    }
                    else if (category == "bedrock")
                    {
                        _allBedrockItems = items;
                        ApplyBedrockFilter();
                        SourceStatus.Text = $"Microsoft Store · 官方专用服务器 · {items.Count} 项";
                    }
                    else
                    {
                        BindItems(items);
                        SourceStatus.Text = "加载器可在原版详情页与游戏一起安装";
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("DownloadPage", "获取版本或模组列表失败", ex);
                SourceStatus.Text = "连接失败，请检查网络后重试";
                BindItems(new[]
                {
                    new DownloadItem
                    {
                        Title = "暂时无法获取内容",
                        Description = ex.Message,
                        Badge = "网络错误",
                        Meta = "点击搜索按钮重试",
                        IconText = "!",
                        IconColor = "#6B3030"
                    }
                });
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    ItemsList.Visibility = Visibility.Visible;
                }
            }
        }

        private void ApplyVanillaFilter()
        {
            IEnumerable<DownloadItem> filtered = _allVanillaItems;
            filtered = _vanillaTypeFilter switch
            {
                "release" => filtered.Where(v => v.VersionType == "release"),
                "snapshot" => filtered.Where(v => v.VersionType == "snapshot"),
                "legacy" => filtered.Where(v => v.VersionType is "old_alpha" or "old_beta"),
                _ => filtered
            };

            var search = SearchBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = filtered.Where(v =>
                    v.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (v.Badge?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (v.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var list = filtered.ToList();
            BindItems(list);
            VanillaCountText.Text = string.IsNullOrWhiteSpace(search)
                ? $"共 {list.Count} 个版本，点击进入详情后下载"
                : $"找到 {list.Count} 个版本";

            foreach (var button in new[] { FilterAllButton, FilterReleaseButton, FilterSnapshotButton, FilterLegacyButton })
            {
                var active = button.Tag?.ToString() == _vanillaTypeFilter;
                button.Foreground = active
                    ? ThemeService.Brush("AccentBrush")
                    : ThemeService.Brush("GhostButtonForeground");
            }
        }

        private void BedrockFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string filter })
            {
                _bedrockTypeFilter = filter;
                ApplyBedrockFilter();
            }
        }

        private void ApplyBedrockFilter()
        {
            IEnumerable<DownloadItem> filtered = _allBedrockItems;
            filtered = _bedrockTypeFilter switch
            {
                "game" => filtered.Where(v => v.BedrockKind is "client" or "preview"),
                "server" => filtered.Where(v => v.KindIsServer),
                "preview" => filtered.Where(v =>
                    v.BedrockKind is "preview" || v.VersionType is "snapshot"),
                _ => filtered
            };

            var search = SearchBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = filtered.Where(v =>
                    v.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (v.Badge?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (v.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (v.Slug?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var list = filtered.ToList();
            BindItems(list);
            BedrockCountText.Text = string.IsNullOrWhiteSpace(search)
                ? $"共 {list.Count} 项，点击进入详情"
                : $"找到 {list.Count} 项";

            foreach (var button in new[] { BedrockFilterAllButton, BedrockFilterGameButton, BedrockFilterServerButton, BedrockFilterPreviewButton })
            {
                var active = button.Tag?.ToString() == _bedrockTypeFilter;
                button.Foreground = active
                    ? ThemeService.Brush("AccentBrush")
                    : ThemeService.Brush("GhostButtonForeground");
            }
        }

        private async Task<IReadOnlyList<DownloadItem>> LoadBedrockAsync(CancellationToken token)
        {
            PageTitle.Text = "基岩版";
            PageSubtitle.Text = "按 BedrockBoot 方式下载历史基岩版并并排安装，或下载 Mojang 官方专用服务器。客户端需要已购买的正版账号与开发人员模式。";
            var catalog = await _bedrockApi.GetCatalogAsync(token);
            return catalog.Select(ToBedrockItem).ToList();
        }

        private static DownloadItem ToBedrockItem(BedrockCatalogItem item)
        {
            var versionType = item.Channel == "preview" ? "snapshot" : "release";
            var meta = !string.IsNullOrWhiteSpace(item.Version)
                ? (string.IsNullOrWhiteSpace(item.Date) ? item.Version : item.Version + "  ·  " + FormatDate(item.Date))
                : (string.IsNullOrWhiteSpace(item.Date) ? "Microsoft Store" : FormatDate(item.Date));
            return new DownloadItem
            {
                Title = item.Title,
                Description = item.Description,
                Badge = item.Badge,
                Meta = meta,
                IconText = item.Kind == "server" ? "S" : "B",
                IconColor = item.IconColor,
                DownloadUrl = item.DownloadUrl ?? "",
                UpdateId = item.UpdateId ?? "",
                Slug = item.Id,
                ItemKind = "bedrock",
                ProjectType = item.Kind,
                BedrockKind = item.Kind,
                VersionType = versionType,
                HasDetail = true
            };
        }

        private async Task<IReadOnlyList<DownloadItem>> LoadVanillaAsync(CancellationToken token)
        {
            PageTitle.Text = "原版游戏";
            PageSubtitle.Text = "显示 Mojang 清单中的全部版本。列表中会标出该版本可用的模组加载器。";
            var manifestTask = _mojangApi.GetVersionManifestAsync(token);
            var supportTask = _loaderApi.GetSupportIndexAsync(token);
            await Task.WhenAll(manifestTask, supportTask);
            var manifest = manifestTask.Result;
            var support = supportTask.Result;

            return manifest.Versions
                .Select(v =>
                {
                    var (badge, desc, color) = v.Type switch
                    {
                        "release" => ("正式版", "Minecraft Java Edition 正式版", "#7CBD4B"),
                        "snapshot" => ("快照版", "Minecraft Java Edition 快照版", "#496A88"),
                        "old_beta" => ("远古 Beta", "Minecraft 远古 Beta 版本", "#8A6A3A"),
                        "old_alpha" => ("远古 Alpha", "Minecraft 远古 Alpha 版本，可能存在兼容性问题", "#6B3030"),
                        _ => (v.Type, "Minecraft Java Edition", "#2D2D2D")
                    };
                    return new DownloadItem
                    {
                        Title = v.Id,
                        Description = desc,
                        Badge = badge,
                        Meta = FormatDate(v.ReleaseTime),
                        IconText = "MC",
                        IconColor = color,
                        DownloadUrl = v.Url,
                        ItemKind = "vanilla",
                        ProjectType = "vanilla",
                        VersionType = v.Type,
                        HasDetail = true,
                        IsAlpha = v.Type is "old_alpha",
                        LoaderSupport = support.Format(v.Id)
                    };
                })
                .ToList();
        }

        private IReadOnlyList<DownloadItem> CreateLoaderItems()
        {
            PageTitle.Text = "模组加载器";
            PageSubtitle.Text = "在「原版游戏」中打开某个版本的详情页，即可与游戏本体一起安装 Fabric、Forge、NeoForge 或 Quilt。";
            return FilterLoaders(AllLoaderItems());
        }

        private void ApplyLoaderFilter()
        {
            var items = FilterLoaders(AllLoaderItems());
            BindItems(items);
            SourceStatus.Text = "加载器可在原版详情页与游戏一起安装";
        }

        private IReadOnlyList<DownloadItem> FilterLoaders(IReadOnlyList<DownloadItem> items)
        {
            var search = SearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(search)) return items;
            return items.Where(v =>
                    v.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (v.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (v.Badge?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        private static IReadOnlyList<DownloadItem> AllLoaderItems()
        {
            return new[]
            {
                new DownloadItem { Title = "Fabric", Description = "轻量、快速且模块化的模组加载器。请到原版详情页与游戏一起安装。", Badge = "推荐 · Fabric", Meta = "支持 1.14 - 1.21.x", IconText = "F", IconColor = "#DB7E35", ItemKind = "loader" },
                new DownloadItem { Title = "Forge", Description = "历史悠久、生态丰富的模组加载器。请到原版详情页与游戏一起安装。", Badge = "Forge", Meta = "支持 1.5 - 1.21.x", IconText = "F", IconColor = "#6B4F3A", ItemKind = "loader" },
                new DownloadItem { Title = "NeoForge", Description = "面向新版本的 Forge 社区分支。请到原版详情页与游戏一起安装。", Badge = "NeoForge", Meta = "支持 1.20.2 - 1.21.x", IconText = "N", IconColor = "#A65D35", ItemKind = "loader" },
                new DownloadItem { Title = "Quilt", Description = "兼容 Fabric 生态的现代模组加载器。请到原版详情页与游戏一起安装。", Badge = "Quilt", Meta = "支持 1.18 - 1.21.x", IconText = "Q", IconColor = "#8D6CC7", ItemKind = "loader" }
            };
        }

        private void BindResourceFilters(string category)
        {
            _syncingResourceFilters = true;
            HighlightChip(SourceModrinthButton, _resourceSource == "modrinth");
            HighlightChip(SourceCurseForgeButton, _resourceSource == "curseforge");
            HighlightChip(SortRelevanceButton, _resourceSort == "relevance");
            HighlightChip(SortDownloadsButton, _resourceSort == "downloads");
            var categories = ResourceCatalog.CategoriesFor(category, _resourceSource);
            CategoryBox.ItemsSource = categories;
            CategoryBox.SelectedItem = categories.FirstOrDefault(c => c.Id == _resourceCategoryId) ?? categories[0];
            _resourceCategoryId = (CategoryBox.SelectedItem as ResourceCategory)?.Id ?? "";
            DispatcherQueue.TryEnqueue(() => _syncingResourceFilters = false);
        }

        private async void SourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string source } || _resourceSource == source) return;
            _resourceSource = source;
            _resourceCategoryId = "";
            await LoadCategoryAsync(_currentCategory);
        }

        private async void SortButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string sort } || _resourceSort == sort) return;
            _resourceSort = sort;
            await LoadCategoryAsync(_currentCategory);
        }

        private async void CategoryBox_SelectionChanged(object sender, EventArgs e)
        {
            if (_syncingResourceFilters) return;
            var id = (CategoryBox.SelectedItem as ResourceCategory)?.Id ?? "";
            if (_resourceCategoryId == id) return;
            _resourceCategoryId = id;
            if (_currentCategory is "mod" or "shader" or "resourcepack" or "modpack")
                await LoadCategoryAsync(_currentCategory);
        }

        private static void HighlightChip(Button button, bool active)
        {
            button.Foreground = active
                ? ThemeService.Brush("AccentBrush")
                : ThemeService.Brush("GhostButtonForeground");
            button.FontWeight = active
                ? new Windows.UI.Text.FontWeight { Weight = 600 }
                : new Windows.UI.Text.FontWeight { Weight = 400 };
        }

        private async Task LoadCatalogItemsAsync(string category, string query, CancellationToken token)
        {
            var typeName = ResourceCatalog.TypeName(category);
            var sourceLabel = _resourceSource == "curseforge" ? "CurseForge" : "Modrinth";
            var sortLabel = _resourceSort == "relevance" ? "相关性" : "下载量";
            PageTitle.Text = typeName;
            PageSubtitle.Text = $"从 {sourceLabel} 按{sortLabel}列出{typeName}，可筛选分类。名称将尽量显示为中文。";

            _modrinthQuery = query ?? "";
            _modrinthOffset = 0;
            _modrinthTotal = 0;
            _modrinthHasMore = false;

            var translation = ModNameTranslationService.EnsureLoadedAsync(token);
            if (_resourceSource == "curseforge")
            {
                var selected = CategoryBox.SelectedItem as ResourceCategory;
                var page = await _curseForgeApi.SearchAsync(category, query, _resourceSort, selected?.CurseForgeId, 0, token);
                await translation;
                BindItems((page.Data ?? new List<CurseForgeMod>()).Select(m => ToDownloadItem(m, category)));
                _modrinthOffset = page.Data?.Count ?? 0;
                _modrinthTotal = page.Pagination?.TotalCount ?? _modrinthOffset;
                _modrinthHasMore = _modrinthOffset < _modrinthTotal && _modrinthOffset > 0;
            }
            else
            {
                var selected = CategoryBox.SelectedItem as ResourceCategory;
                var page = await _modrinthApi.SearchAsync(
                    category, query, 0, ModrinthApi.MaxPageSize, _resourceSort, selected?.ModrinthSlug, token);
                await translation;
                BindItems((page.Hits ?? new List<ModrinthHit>()).Select(ToDownloadItem));
                _modrinthOffset = page.Hits?.Count ?? 0;
                _modrinthTotal = page.TotalHits;
                _modrinthHasMore = _modrinthOffset < _modrinthTotal && _modrinthOffset > 0;
            }

            UpdateModrinthStatus(typeName);
            UpdateLoadMoreFooter();
        }

        private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
            => await LoadMoreModrinthAsync();

        private void ItemsList_Loaded(object sender, RoutedEventArgs e)
        {
            _listScroll ??= FindScrollViewer(ItemsList);
            if (_listScroll is not null)
            {
                _listScroll.ViewChanged -= ItemsList_ViewChanged;
                _listScroll.ViewChanged += ItemsList_ViewChanged;
            }
        }

        private void ItemsList_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_listScroll is null || !_modrinthHasMore || _loadingMore) return;
            if (_listScroll.ScrollableHeight <= 0) return;
            if (_listScroll.VerticalOffset >= _listScroll.ScrollableHeight - 180)
                _ = LoadMoreModrinthAsync();
        }

        private async Task LoadMoreModrinthAsync()
        {
            if (!_modrinthHasMore || _loadingMore) return;
            if (_currentCategory is not ("mod" or "shader" or "resourcepack" or "modpack")) return;
            if (_loadCancellation is null || _loadCancellation.IsCancellationRequested) return;

            _loadingMore = true;
            SetLoadMoreBusy(true);
            try
            {
                var token = _loadCancellation.Token;
                var added = 0;
                if (_resourceSource == "curseforge")
                {
                    var selected = CategoryBox.SelectedItem as ResourceCategory;
                    var page = await _curseForgeApi.SearchAsync(
                        _currentCategory, _modrinthQuery, _resourceSort, selected?.CurseForgeId, _modrinthOffset, token);
                    var extra = (page.Data ?? new List<CurseForgeMod>()).Select(m => ToDownloadItem(m, _currentCategory)).ToList();
                    foreach (var item in extra)
                    {
                        AttachLocalIcon(item);
                        _visibleItems.Add(item);
                    }
                    QueueRemoteIcons(extra);
                    added = page.Data?.Count ?? 0;
                    if (page.Pagination?.TotalCount > 0) _modrinthTotal = page.Pagination.TotalCount;
                }
                else
                {
                    var selected = CategoryBox.SelectedItem as ResourceCategory;
                    var page = await _modrinthApi.SearchAsync(
                        _currentCategory, _modrinthQuery, _modrinthOffset, ModrinthApi.MaxPageSize,
                        _resourceSort, selected?.ModrinthSlug, token);
                    var extra = (page.Hits ?? new List<ModrinthHit>()).Select(ToDownloadItem).ToList();
                    foreach (var item in extra)
                    {
                        AttachLocalIcon(item);
                        _visibleItems.Add(item);
                    }
                    QueueRemoteIcons(extra);
                    added = page.Hits?.Count ?? 0;
                    if (page.TotalHits > 0) _modrinthTotal = page.TotalHits;
                }
                _modrinthOffset += added;
                _modrinthHasMore = added > 0 && _modrinthOffset < _modrinthTotal;
                var typeName = _currentCategory switch
                {
                    "mod" => "模组",
                    "shader" => "光影",
                    "resourcepack" => "资源包",
                    "modpack" => "整合包",
                    _ => "资源"
                };
                UpdateModrinthStatus(typeName);
                UpdateLoadMoreFooter();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("DownloadPage", "加载更多模组失败", ex);
                SourceStatus.Text = "继续加载失败，可点击底部按钮重试";
                _modrinthHasMore = true;
                UpdateLoadMoreFooter();
            }
            finally
            {
                _loadingMore = false;
                SetLoadMoreBusy(false);
            }
        }

        private void UpdateModrinthStatus(string typeName)
        {
            if (_modrinthTotal > 0)
            {
                var source = _resourceSource == "curseforge" ? "CurseForge" : "Modrinth";
                SourceStatus.Text = _modrinthHasMore
                    ? $"{source} · 已显示 {_visibleItems.Count} / {_modrinthTotal}"
                    : $"{source} · 已显示全部 {_visibleItems.Count} 个{typeName}";
            }
            else
            {
                var source = _resourceSource == "curseforge" ? "CurseForge" : "Modrinth";
                SourceStatus.Text = $"{source} · {_visibleItems.Count} 个{typeName}";
            }
        }

        private void UpdateLoadMoreFooter()
        {
            var show = _currentCategory is "mod" or "shader" or "resourcepack" or "modpack" && _modrinthHasMore;
            LoadMoreFooter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !_loadingMore)
                LoadMoreText.Text = _modrinthTotal > _visibleItems.Count
                    ? $"加载更多（还有 {_modrinthTotal - _visibleItems.Count}）"
                    : "加载更多";
        }

        private void HideLoadMore()
        {
            _modrinthHasMore = false;
            _loadingMore = false;
            LoadMoreFooter.Visibility = Visibility.Collapsed;
            SetLoadMoreBusy(false);
        }

        private void SetLoadMoreBusy(bool busy)
        {
            LoadMoreButton.IsEnabled = !busy;
            LoadMoreRing.IsActive = busy;
            LoadMoreRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) LoadMoreText.Text = "正在加载…";
        }

        private void BindItems(IEnumerable<DownloadItem> items)
        {
            _visibleItems.Clear();
            foreach (var item in items)
            {
                AttachLocalIcon(item);
                _visibleItems.Add(item);
            }
            QueueRemoteIcons(_visibleItems);
        }

        private void QueueRemoteIcons(IEnumerable<DownloadItem> items)
        {
            var token = _loadCancellation?.Token ?? CancellationToken.None;
            var generation = _iconLoadGeneration;
            var pending = items
                .Where(item => item.Icon is null && !string.IsNullOrWhiteSpace(item.IconUrl))
                .ToList();
            if (pending.Count == 0) return;
            _ = LoadRemoteIconsAsync(pending, generation, token);
        }

        private async Task LoadRemoteIconsAsync(List<DownloadItem> items, int generation, CancellationToken token)
        {
            using var gate = new SemaphoreSlim(4);
            try
            {
                await Task.WhenAll(items.Select(async item =>
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        if (generation != _iconLoadGeneration) return;
                        var bytes = await RemoteIconLoader.DownloadAsync(item.IconUrl, token);
                        if (bytes is null || generation != _iconLoadGeneration) return;
                        DispatcherQueue.TryEnqueue(() => _ = AssignIconAsync(item, bytes, generation));
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task AssignIconAsync(DownloadItem item, byte[] bytes, int generation)
        {
            if (generation != _iconLoadGeneration) return;
            try
            {
                item.Icon = await RemoteIconLoader.CreateBitmapAsync(bytes);
            }
            catch (Exception)
            {
                item.Icon = null;
            }
        }

        private static void AttachLocalIcon(DownloadItem item)
        {
            if (item.Icon is not null) return;
            // 有远端图标地址的条目（Modrinth / CurseForge）一律留给远端图标。
            // 它们的 IconPath 会退化成通用原版图标（GetAutoIconName("vanilla","release")
            // → Assets/icons/Grass.png），一旦在这里写进 Icon，紧接着的
            // QueueRemoteIcons 会因为「Icon is null」的过滤条件把真正的 mod 图标永久跳过
            // —— 表现就是资源列表里每个模组都显示同一个草方块（或字母占位），
            // 永远加载不出项目自己的图标。
            if (!string.IsNullOrWhiteSpace(item.IconUrl)) return;
            try
            {
                item.Icon = RemoteIconLoader.TryCreateFromFile(item.IconPath, 40);
            }
            catch (Exception)
            {
                // 本地图标缺失时保留字母占位
            }
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer) return viewer;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found is not null) return found;
            }
            return null;
        }

        // 卡片态不再加载背景图：原先的悬停背景展示（Card_PointerEntered /
        // LoadCoverAsync）已整体移除，列表项只显示图标、标题与元信息。

        private static DownloadItem ToDownloadItem(ModrinthHit hit)
        {
            var color = hit.Color is null or 0 ? "#2D2D2D" : $"#{hit.Color.Value:X6}";
            var translated = ModNameTranslationService.Translate(hit.Title, hit.Slug, hit.Title);
            return new DownloadItem
            {
                Title = translated,
                Description = hit.Description,
                Badge = $"{hit.Author} · {hit.ProjectType}",
                Meta = $"下载 {FormatDownloads(hit.Downloads)}  ·  关注 {FormatDownloads(hit.Follows)}",
                GameVersions = FormatGameVersions(hit.Versions),
                IconText = translated.Length > 0 ? translated[..1].ToUpperInvariant() : "?",
                IconColor = color,
                IconUrl = hit.IconUrl ?? "",
                CoverUrl = hit.Gallery?.FirstOrDefault() ?? "",
                DownloadUrl = $"https://modrinth.com/{hit.ProjectType}/{hit.Slug}",
                Slug = string.IsNullOrWhiteSpace(hit.Slug) ? hit.ProjectId : hit.Slug,
                ProjectId = hit.ProjectId,
                ProjectType = hit.ProjectType,
                ItemKind = "modrinth",
                HasDetail = true
            };
        }

        private static DownloadItem ToDownloadItem(CurseForgeMod mod, string projectType)
        {
            var title = string.IsNullOrWhiteSpace(mod.Name) ? mod.Slug : mod.Name;
            var translated = ModNameTranslationService.Translate(title, mod.Slug, title);
            var iconUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url;
            return new DownloadItem
            {
                Title = translated,
                Description = mod.Summary,
                Badge = (string.IsNullOrWhiteSpace(mod.AuthorName) ? "CurseForge" : mod.AuthorName) + " · CurseForge",
                Meta = $"下载 {FormatDownloads(mod.DownloadCount)}  ·  更新 {mod.DateModified:yyyy-MM-dd}",
                GameVersions = FormatGameVersions(mod.LatestFiles.SelectMany(f => f.GameVersions)),
                IconText = translated.Length > 0 ? translated[..1].ToUpperInvariant() : "?",
                IconColor = "#F16436",
                IconUrl = iconUrl ?? "",
                CoverUrl = mod.CoverUrl,
                DownloadUrl = mod.Links?.WebsiteUrl ?? $"https://www.curseforge.com/minecraft/mc-mods/{mod.Slug}",
                Slug = mod.Slug,
                ProjectType = projectType,
                ItemKind = "curseforge",
                CurseForgeId = mod.Id,
                HasDetail = true
            };
        }

        private void UpdateCategoryState(string category)
        {
            SearchBox.PlaceholderText = category switch
            {
                "vanilla" => "搜索版本号，例如 1.21.5",
                "bedrock" => "搜索基岩版、预览版或服务器",
                "loader" => "搜索加载器名称，例如 Fabric",
                "mod" => "搜索模组名称、作者或中文译名",
                "shader" => "搜索光影名称或关键词",
                "resourcepack" => "搜索资源包名称或关键词",
                "modpack" => "搜索整合包名称或关键词",
                _ => "搜索…"
            };
            foreach (var button in new[] { VanillaButton, BedrockButton, LoaderButton, ModsButton, ShadersButton, ResourcePacksButton, ModpacksButton })
            {
                button.Foreground = button.Tag?.ToString() == category
                    ? ThemeService.Brush("AccentBrush")
                    : ThemeService.Brush("GhostButtonForeground");
                button.FontWeight = button.Tag?.ToString() == category
                    ? new Windows.UI.Text.FontWeight { Weight = 600 }
                    : new Windows.UI.Text.FontWeight { Weight = 400 };
            }
        }

        /// <summary>
        /// 卡片上「适配的游戏版本」的文本，如 “适配 1.20.1 ~ 1.21.4”。
        /// 版本来自各平台元数据（Modrinth 的 hit.versions / CurseForge 的 latestFiles[].gameVersions），
        /// 因此是**自动标记**的，不需要先下载到本地读 jar。取不到版本时返回空串（该位不显示）。
        /// </summary>
        private static string FormatGameVersions(IEnumerable<string>? versions)
        {
            var text = FormatHelper.SummarizeGameVersions(versions);
            return text.Length == 0 ? "" : $"适配 {text}";
        }

        private static string FormatDownloads(long value)
        {
            return value switch
            {
                >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
                >= 1_000 => $"{value / 1_000d:0.#}K",
                _ => value.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static string FormatDate(string value)
        {
            return DateTimeOffset.TryParse(value, out var date) ? date.ToString("yyyy-MM-dd") : "官方版本";
        }
    }
}
