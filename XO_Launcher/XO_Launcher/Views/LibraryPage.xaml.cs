using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 资源库（独立页面）。列出已保存到本机资源库的模组 / 光影 / 资源包 / 整合包，
    /// 可删除条目，或把某一条直接安装到指定游戏版本。
    /// 「下载」页面获取的文件都保存到这里；版本设置里也仍可从资源库安装。
    /// </summary>
    public sealed partial class LibraryPage : UserControl
    {
        private string _projectType = ContentKind.Mod;
        private List<InstalledVersion> _versions = new();

        public LibraryPage()
        {
            InitializeComponent();
            ApplyKindVisual();
            Refresh();
        }

        /// <summary>进入资源库页面时调用：重新扫描游戏版本与资源库目录。</summary>
        public void Refresh()
        {
            if (GroupList is null) return;
            LoadVersions();
            LoadEntries();
        }

        /// <summary>目标版本下拉：只列 Java 版（基岩版没有 mods / shaderpacks / resourcepacks 目录）。</summary>
        private void LoadVersions()
        {
            var previous = (TargetVersionCombo.SelectedItem as InstalledVersion)?.Id;
            _versions = InstalledVersionService.Scan()
                .Where(v => !v.IsBedrock)
                .ToList();

            TargetVersionCombo.ItemsSource = _versions;
            var target = _versions.FirstOrDefault(v => v.Id == previous)
                         ?? _versions.FirstOrDefault(v => v.Id == LauncherSettings.Instance.SelectedVersion)
                         ?? _versions.FirstOrDefault();
            TargetVersionCombo.SelectedItem = target;
            TargetVersionCombo.IsEnabled = _versions.Count > 0;
        }

        private void LoadEntries()
        {
            // 先清理再扫盘：被新版本取代的旧文件在库里留 3 天供回滚，到期就删。
            // 没有后台定时器，用户下次打开资源库时顺手完成，行为可预期。
            try
            {
                LibraryIndexService.PruneExpiredSuperseded();
                LibraryIndexService.DropMissing(_projectType);
            }
            catch (Exception ex)
            {
                StatusText.Text = "清理过期旧版失败：" + ex.Message;
            }

            var canInstall = ContentKind.IsInstallable(_projectType);
            var entries = ModLibraryService.Scan(_projectType)
                .Select(item => new LibraryEntry
                {
                    Item = item,
                    CanInstall = canInstall,
                    Record = LibraryIndexService.Find(_projectType, item.Name)
                })
                .ToList();

            var groups = BuildVersionGroups(entries);
            GroupList.ItemsSource = groups;
            var empty = groups.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            GroupList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            var noun = ContentKind.DisplayName(_projectType);
            EmptyText.Text = $"资源库中暂无{noun}。\n在「下载」页面获取的内容会自动保存到这里。";
            CountText.Text = empty ? "" : $"共 {entries.Count} 个文件，分属 {groups.Count} 个游戏版本";

            HeaderHint.Text = "这里是已下载到本机的资源：模组与资源包按适配的 Minecraft 版本分卡片，" +
                              "光影按它需要的加载器（Iris / OptiFine）分卡片；一个资源适配多项时会在每张相关卡片里各出现一次。" +
                              "「更新」只在资源库里下载新版本，并可同步替换已装它的版本；被取代的旧文件保留 3 天后自动清理。\n" +
                              $"保存位置：{ModLibraryService.RootDirectory}";
        }

        /// <summary>
        /// 分卡片。模组与资源包按适配的 Minecraft 版本分（版本取自资源库索引里下载时记下的
        /// game_versions，缺了就用 jar 元数据文本拆）；光影按它需要的**光影加载器**分
        /// （Iris / OptiFine 是两种不同的使用场景，同一个包常同时支持两者，于是两张卡片都出现）。
        /// 两者都给不出的统一进「未标注」卡片，排在最后。
        /// </summary>
        private List<LibraryVersionGroup> BuildVersionGroups(List<LibraryEntry> entries)
        {
            var byLoader = _projectType == ContentKind.Shader;
            var unlabeled = byLoader ? "未知加载器" : "未标注版本";
            var buckets = new Dictionary<string, List<LibraryEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var keys = byLoader
                    ? LoaderKeys(entry)
                    : entry.Record?.GameVersions is { Count: > 0 } recorded
                        ? recorded
                        : LibraryIndexService.ParseVersions(entry.Item.GameVersions);
                if (keys.Count == 0) keys = new List<string> { unlabeled };

                foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!buckets.TryGetValue(key, out var list))
                        buckets[key] = list = new List<LibraryEntry>();
                    list.Add(entry);
                }
            }

            return buckets
                .OrderByDescending(kv => kv.Key.Equals(unlabeled, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(kv => byLoader ? "" : kv.Key, VersionLabelComparer.Instance)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new LibraryVersionGroup
                {
                    VersionLabel = kv.Key,
                    KindLabel = byLoader ? "光影加载器" : "游戏版本",
                    SummaryText = $"共 {kv.Value.Count} 个文件",
                    Entries = kv.Value.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()
                })
                .ToList();
        }

        /// <summary>光影条目对应的加载器卡片集合（Modrinth 的 loaders 常是 iris + optifine 两个）。</summary>
        private static List<string> LoaderKeys(LibraryEntry entry)
        {
            var raw = entry.Record?.Loaders is { Count: > 0 } list
                ? list
                : string.IsNullOrWhiteSpace(entry.Record?.Loader)
                    ? new List<string>()
                    : entry.Record!.Loader.Split(',').Select(s => s.Trim()).ToList();

            return raw
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(NormalizeLoaderLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeLoaderLabel(string loader) => loader.Trim().ToLowerInvariant() switch
        {
            "iris" => "Iris",
            "optifine" => "OptiFine",
            "vanilla" => "原版",
            _ => FormatHelper.FormatLoaderName(loader)
        };

        /// <summary>版本标签的语义比较：按数字段逐段比大小，让 26.3 &gt; 1.21.1 &gt; 1.20.1。</summary>
        private sealed class VersionLabelComparer : IComparer<string>
        {
            public static readonly VersionLabelComparer Instance = new();

            public int Compare(string? a, string? b)
            {
                var left = Segments(a);
                var right = Segments(b);
                for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
                {
                    var l = i < left.Count ? left[i] : 0;
                    var r = i < right.Count ? right[i] : 0;
                    if (l != r) return l.CompareTo(r);
                }
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }

            private static List<long> Segments(string? version)
            {
                var list = new List<long>();
                if (string.IsNullOrWhiteSpace(version)) return list;
                foreach (var part in version.Split('.', '-', '+', '_'))
                    if (long.TryParse(part, out var value)) list.Add(value);
                return list;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        /// <summary>
        /// 导入整合包链接：粘贴好友分享的 xolauncher:// 链接，解析出清单后
        /// 安装对应游戏与加载器、在线下载模组、还原配置。进度在「消息」中查看。
        /// </summary>
        private async void ImportLink_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextInput
            {
                PlaceholderText = "粘贴 xolauncher:// 分享链接",
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MaxHeight = 120,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock
            {
                Text = "粘贴好友发来的整合包分享链接：启动器会安装对应的 Minecraft 与加载器，" +
                       "在线下载清单里的模组，并还原内联的配置文件。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = ThemeService.Brush("TextSecondaryBrush")
            });
            body.Children.Add(input);

            var result = await AppDialog.ShowAsync(
                XamlRoot, "导入整合包链接", "", primary: "导入安装", close: "取消", body: body, maxWidth: 600);
            if (result != AppDialogResult.Primary) return;

            var manifest = ModpackExportService.FromShareLink(input.Text);
            if (manifest is null)
            {
                await AppDialog.ShowMessageAsync(XamlRoot, "链接无效",
                    "无法解析这条分享链接。请确认复制完整，且它来自 XO Launcher 的「导出整合包」。");
                return;
            }

            var title = string.IsNullOrWhiteSpace(manifest.Name) ? "整合包" : manifest.Name;
            var task = DownloadService.Instance.StartInstall(title, $"{title} {manifest.GameVersion}");
            try
            {
                await ModpackInstallService.InstallFromManifestAsync(manifest, task, task.Token);
                InstalledVersionService.Invalidate();
                Refresh();
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Modpack", "从链接安装整合包失败", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Detail = ex.Message;
                    t.ProgressHint = "安装失败";
                });
            }
        }

        private void Kind_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string kind }) return;
            if (kind == _projectType) return;
            _projectType = kind;
            ApplyKindVisual();
            StatusText.Text = "";
            LoadEntries();
        }

        /// <summary>当前选中的资源类型用主题色高亮，其余为中性色。</summary>
        private void ApplyKindVisual()
        {
            var chips = new[]
            {
                (Button: ModKindButton, Kind: ContentKind.Mod),
                (Button: ShaderKindButton, Kind: ContentKind.Shader),
                (Button: PackKindButton, Kind: ContentKind.ResourcePack),
                (Button: ModpackKindButton, Kind: ContentKind.Modpack),
            };
            foreach (var chip in chips)
            {
                var active = chip.Kind == _projectType;
                chip.Button.Background = active
                    ? ThemeService.Brush("AccentSoftBrush")
                    : ThemeService.Brush("NeutralButtonBrush");
                chip.Button.Foreground = active
                    ? ThemeService.Brush("AccentSoftForegroundBrush")
                    : ThemeService.Brush("NeutralButtonForegroundBrush");
                chip.Button.FontWeight = active ? new FontWeight { Weight = 600 } : new FontWeight { Weight = 400 };
            }

            // 整合包是整包归档，没有「安装到版本」的说法
            TargetPanel.Visibility = ContentKind.IsInstallable(_projectType)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = ModLibraryService.GetDirectory(_projectType);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                StatusText.Text = "已打开 " + path;
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开目录：" + ex.Message;
            }
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LibraryEntry entry }) return;
            if (TargetVersionCombo.SelectedItem is not InstalledVersion version)
            {
                StatusText.Text = "请先在右上角选择要安装到的游戏版本";
                return;
            }

            try
            {
                var gameDir = LauncherSettings.Instance.GameDirectory;
                var instanceDir = GameInstanceLayout.GetDirectory(gameDir, version.Id);
                var contentDir = GameInstanceLayout.ResolveContentRoot(gameDir, instanceDir);
                var (installed, skipped) = ModLibraryService.InstallToInstance(
                    contentDir, _projectType, new[] { entry.Item });

                StatusText.Text = installed > 0
                    ? $"已安装到 {version.DisplayName}：{entry.Name}"
                    : skipped > 0
                        ? $"「{version.DisplayName}」中已存在同名文件，已跳过：{entry.Name}"
                        : $"未找到源文件：{entry.Item.Name}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "安装失败：" + ex.Message;
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LibraryEntry entry }) return;
            try
            {
                ModLibraryService.DeleteFromLibrary(entry.Item, _projectType);
                StatusText.Text = "已从资源库删除 " + entry.Name;
            }
            catch (Exception ex)
            {
                StatusText.Text = "删除失败：" + ex.Message;
            }
            LoadEntries();
        }

        // ==================== 清理重复 ====================

        /// <summary>
        /// 删掉「同一个模组 + 同一个模组版本 + 适配同一批游戏版本」的重复文件，
        /// 每组只保留最后写入时间最新的那份；被删文件已有的安装记录转移到保留项上。
        /// </summary>
        private async void Dedupe_Click(object sender, RoutedEventArgs e)
        {
            var groups = LibraryIndexService.FindDuplicates(_projectType);
            if (groups.Count == 0)
            {
                StatusText.Text = $"没有发现重复的{ContentKind.DisplayName(_projectType)}条目";
                return;
            }

            var body = new StackPanel { Spacing = 6 };
            var doomed = new List<(LibraryFileRecord Keep, LibraryFileRecord Drop)>();
            foreach (var group in groups)
            {
                var keep = group[0];
                foreach (var drop in group.Skip(1))
                {
                    doomed.Add((keep, drop));
                    var scope = string.IsNullOrWhiteSpace(keep.GameVersionText) ? "版本未标注" : keep.GameVersionText;
                    body.Children.Add(new TextBlock
                    {
                        Text = $"{keep.DisplayName} {keep.ModVersion}（{scope}）：保留 {keep.FileName}，删除 {drop.FileName}",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = ThemeService.Brush("TextSecondaryBrush")
                    });
                }
            }

            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "清理重复条目",
                $"发现 {groups.Count} 组重复（同一模组的同一版本、适配同一批游戏版本）。" +
                $"每组保留最新的一份，其余 {doomed.Count} 个文件将从资源库删除；已经装进游戏版本的副本不受影响。",
                primary: "删除 " + doomed.Count + " 个重复文件",
                close: "取消",
                body: body);
            if (result != AppDialogResult.Primary) return;

            var deleted = 0;
            foreach (var (keep, drop) in doomed)
            {
                try
                {
                    if (File.Exists(drop.AbsolutePath))
                    {
                        File.Delete(drop.AbsolutePath);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"删除 {drop.FileName} 失败：" + ex.Message;
                }
                // 安装记录跟着走，否则「这个版本装过该模组」的事实会随记录一起丢掉
                foreach (var install in drop.Installs)
                    LibraryIndexService.RecordInstall(_projectType, keep.FileName, install.InstanceDir, install.FileName);
                LibraryIndexService.Forget(_projectType, drop.FileName);
            }
            StatusText.Text = $"已删除 {deleted} 个重复文件";
            LoadEntries();
        }

        // ==================== 在资源库里更新，并同步到已装的版本 ====================

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: LibraryEntry entry }) return;
            var record = entry.Record;
            if (record is null || string.IsNullOrWhiteSpace(record.ProjectRef)) return;

            var mc = record.GameVersions.FirstOrDefault() ?? "";
            var loader = record.Loader ?? "";
            if (string.IsNullOrWhiteSpace(mc) || string.IsNullOrWhiteSpace(loader))
            {
                StatusText.Text = "缺少适配的游戏版本或加载器信息，无法查新版本";
                return;
            }

            StatusText.Text = "正在查询 " + entry.Name + " 的新版本…";
            ModrinthVersion? newer;
            try
            {
                newer = await new Api.ModrinthApi()
                    .GetLatestCompatibleAsync(record.ProjectRef, mc, loader, CancellationToken.None);
            }
            catch (Exception ex)
            {
                StatusText.Text = "查询新版本失败：" + ex.Message;
                return;
            }

            if (newer is null)
            {
                StatusText.Text = $"没查到 {entry.Name} 适配 {mc} / {loader} 的可下载版本";
                return;
            }
            if (string.Equals(newer.Id, record.VersionId, StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = $"{entry.Name} 已是最新（{record.ModVersion}）";
                return;
            }

            var newFile = newer.PrimaryFile;
            if (newFile is null || string.IsNullOrWhiteSpace(newFile.Url)
                || string.IsNullOrWhiteSpace(newFile.Filename))
            {
                StatusText.Text = "新版本没有可下载的文件";
                return;
            }

            // 同步目标：索引记录只覆盖「通过资源库装过」的实例，用户手动拷进去的靠扫 mod id 找出来，
            // 两者合并后交给用户在弹窗里勾选——不勾选就只更新资源库，不动任何版本。
            var detected = ModLibraryService.FindInstancesWithMod(_projectType, record.ModId);
            var rows = new List<(CheckBox Box, string ContentDir, IReadOnlyList<string> OldFiles, string Label)>();
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(new TextBlock
            {
                Text = $"新版本 {newer.VersionNumber}（当前 {record.ModVersion}）。勾选要一起更新的版本；" +
                       "勾选的版本里，同模组的旧文件（含被改过名或被禁用的）会被替换掉。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            if (detected.Count == 0)
                body.Children.Add(new TextBlock
                {
                    Text = "没有检测到已经安装该模组的版本（可能还没安装过，或它不是通过资源库装的）。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
            foreach (var (version, contentDir, files) in detected)
            {
                var label = $"{version.DisplayName}  ·  {string.Join("、", files)}";
                var box = new CheckBox { Content = label, IsChecked = true, FontSize = 12 };
                rows.Add((box, contentDir, files, label));
                body.Children.Add(box);
            }

            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "更新" + ContentKind.DisplayName(_projectType),
                $"{entry.Name}：{record.ModVersion} → {newer.VersionNumber}",
                primary: rows.Count > 0 ? "下载并同步到所选版本" : "只下载到资源库",
                close: "取消",
                body: body);
            if (result != AppDialogResult.Primary) return;

            // 1) 新版本文件下进资源库（复用下载队列，等它真的下完再继续）
            var libraryDir = ModLibraryService.GetDirectory(_projectType);
            var destPath = Path.Combine(libraryDir, newFile.Filename);
            if (!File.Exists(destPath) || new FileInfo(destPath).Length <= 0)
            {
                var task = DownloadService.Instance.Enqueue(entry.Name, newFile.Filename, newFile.Url,
                    newFile.Size, libraryDir, _projectType);
                try
                {
                    await DownloadService.WaitForAsync(new List<DownloadTask> { task }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    StatusText.Text = "下载中断：" + ex.Message;
                    return;
                }
                if (!File.Exists(destPath))
                {
                    StatusText.Text = "新版本下载失败：" + (task.Detail ?? task.Status);
                    return;
                }
            }
            LibraryIndexService.RecordDownload(_projectType, newFile.Filename,
                string.IsNullOrWhiteSpace(newer.ProjectId) ? record.ProjectRef : newer.ProjectId,
                newer.Id, loader, newer.Loaders, entry.Name, newer.VersionNumber, newer.GameVersions,
                newFile.Hashes is { } hashes && hashes.TryGetValue("sha1", out var sha) ? sha : "",
                newFile.Url);

            // 2) 同步到勾选的实例：先删同 mod id 的旧文件，再覆盖式拷入新版
            var synced = 0;
            foreach (var (box, contentDir, oldFiles, _) in rows.Where(r => r.Box.IsChecked == true))
            {
                var folder = Path.Combine(contentDir, ContentKind.LibraryFolder(_projectType));
                try
                {
                    Directory.CreateDirectory(folder);
                    foreach (var old in oldFiles)
                    {
                        var oldPath = Path.Combine(folder, old);
                        if (string.Equals(old, newFile.Filename, StringComparison.OrdinalIgnoreCase)) continue;
                        try { if (File.Exists(oldPath)) File.Delete(oldPath); }
                        catch (Exception ex)
                        {
                            StatusText.Text = $"无法移除旧文件 {old}：" + ex.Message;
                        }
                    }
                    File.Copy(destPath, Path.Combine(folder, newFile.Filename), overwrite: true);
                    LibraryIndexService.RecordInstall(_projectType, newFile.Filename, contentDir, newFile.Filename);
                    synced++;
                }
                catch (Exception ex)
                {
                    StatusText.Text = "同步到版本失败：" + ex.Message;
                }
            }

            // 3) 旧库文件标记为被取代：库里再留 3 天，到期由 LoadEntries 的清理自动删除
            LibraryIndexService.MarkSuperseded(_projectType, record.FileName, newFile.Filename);
            StatusText.Text = $"已更新 {entry.Name} → {newer.VersionNumber}，同步到 {synced} 个版本；" +
                              "旧文件在资源库里保留 3 天后可回滚";
            LoadEntries();
        }
    }
}
