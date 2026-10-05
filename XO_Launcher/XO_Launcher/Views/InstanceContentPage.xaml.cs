using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class InstanceContentPage : UserControl
    {
        private string _instanceDir = "";
        private string _contentDir = "";
        private string _projectType = ContentKind.Mod;

        /// <summary>用户点击「前往资源库」时触发，由宿主面板转交主窗口切到资源库页面。</summary>
        public event EventHandler? OpenLibraryRequested;

        public InstanceContentPage()
        {
            InitializeComponent();
        }

        public void Bind(string instanceDir, string projectType)
            => Bind(LauncherSettings.Instance.GameDirectory, instanceDir, projectType);

        public void Bind(string gameDir, string instanceDir, string projectType)
        {
            _instanceDir = instanceDir;
            _contentDir = GameInstanceLayout.ResolveContentRoot(gameDir, instanceDir);
            _projectType = projectType;
            TitleText.Text = ContentKind.ManagementTitle(projectType);
            // 「导出整合包」只对模组管理页有意义：整合包 = 模组清单 + config。
            if (ExportModpackButton is not null)
                ExportModpackButton.Visibility = projectType == ContentKind.Mod
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            Refresh();
        }

        public void HidePicker()
        {
            if (LibraryPickerOverlay is null) return;
            LibraryPickerOverlay.Visibility = Visibility.Collapsed;
        }

        private string Noun => ContentKind.DisplayName(_projectType);

        private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        public void Refresh()
        {
            if (string.IsNullOrWhiteSpace(_contentDir) || ContentList is null) return;
            HidePicker();
            var items = InstanceModService.Scan(_contentDir, _projectType);
            ContentList.ItemsSource = items;
            var empty = items.Count == 0;
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ContentList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            EmptyText.Text = $"当前版本尚未安装{Noun}。请前往「下载」获取文件，再于此处从下载库安装。";
            var enabled = items.Count(m => m.IsEnabled);
            CountText.Text = empty
                ? $"请前往「下载」获取{Noun}，下载完成后在此安装。"
                : $"共 {items.Count} 项，已启用 {enabled} 项";
            StatusText.Text = "";
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = GameInstanceLayout.GetSubFolder(_contentDir, ContentKind.LibraryFolder(_projectType));
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开目录：" + ex.Message;
            }
        }

        private void InstallFromLibrary_Click(object sender, RoutedEventArgs e) => ShowLibrary();

        /// <summary>资源库管理已独立成顶层页面（资源库），这里只负责跳过去。</summary>
        private void OpenLibrary_Click(object sender, RoutedEventArgs e)
            => OpenLibraryRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>从资源库挑选内容安装到当前版本（库的增删在资源库页面里做）。</summary>
        private void ShowLibrary()
        {
            var library = ModLibraryService.Scan(_projectType)
                .Select(item => new LibraryPickItem { Item = item })
                .ToList();
            PickerList.ItemsSource = library;
            var empty = library.Count == 0;
            PickerEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            PickerList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            ConfirmPickerButton.IsEnabled = false;
            ConfirmPickerButton.Content = "安装";
            PickerTitle.Text = $"从资源库安装{Noun}";
            PickerHint.Text = empty
                ? $"资源库中暂无{Noun}。请先前往「下载」获取，文件将保存至资源下载目录。"
                : $"点击选择要安装的{Noun}，选中项将显示绿色边框，可多选。已存在的同名文件将被跳过。";
            PickerEmpty.Text = $"资源库中暂无{Noun}。请先前往「下载」获取。";
            LibraryPickerOverlay.Visibility = Visibility.Visible;
        }

        private void PickerList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not LibraryPickItem item) return;
            item.IsSelected = !item.IsSelected;
            ConfirmPickerButton.IsEnabled = PickerItems().Any(i => i.IsSelected);
        }

        private void CancelPicker_Click(object sender, RoutedEventArgs e) => HidePicker();

        private void ConfirmPicker_Click(object sender, RoutedEventArgs e)
        {
            var selected = PickerItems().Where(i => i.IsSelected).Select(i => i.Item).ToList();
            HidePicker();
            if (selected.Count == 0)
            {
                StatusText.Text = $"未选择要安装的{Noun}";
                return;
            }

            try
            {
                var (installed, skipped) = ModLibraryService.InstallToInstance(_contentDir, _projectType, selected);
                Refresh();
                StatusText.Text = skipped == 0
                    ? $"已安装 {installed} 项{Noun}"
                    : $"已安装 {installed} 项{Noun}，已跳过 {skipped} 个同名文件";
            }
            catch (Exception ex)
            {
                StatusText.Text = "安装失败：" + ex.Message;
            }
        }

        /// <summary>「加入资源库」：把该版本里已安装的这一项反向收进资源库。</summary>
        private void AddToLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: InstanceModItem item }) return;
            try
            {
                var (added, skipped) = ModLibraryService.AddToLibrary(item, _projectType);
                StatusText.Text = added
                    ? $"已加入资源库：{item.Name}"
                    : skipped
                        ? $"资源库中已存在同名文件：{item.Name}"
                        : $"未找到源文件：{item.Name}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "加入资源库失败：" + ex.Message;
            }
        }

        private IEnumerable<LibraryPickItem> PickerItems()
            => PickerList.ItemsSource as IEnumerable<LibraryPickItem> ?? Array.Empty<LibraryPickItem>();

        private void Enabled_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch { DataContext: InstanceModItem item } toggle) return;
            if (item.IsEnabled == toggle.IsOn) return;
            try
            {
                InstanceModService.SetEnabled(item, toggle.IsOn);
                Refresh();
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法更改启用状态：" + ex.Message;
                Refresh();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: InstanceModItem item }) return;
            try
            {
                InstanceModService.Delete(item);
                Refresh();
                StatusText.Text = "已移除 " + item.Name;
            }
            catch (Exception ex)
            {
                StatusText.Text = "移除失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 导出整合包：把当前实例的 mods（在线解析成 Modrinth 下载地址）+ config 编成清单，
        /// gzip+base64url 塞进一条 xolauncher:// 分享链接，复制到剪贴板并展示。
        /// </summary>
        private async void ExportModpack_Click(object sender, RoutedEventArgs e)
        {
            var meta = GameInstanceLayout.ReadMeta(_instanceDir);
            var gameVersion = meta?.MinecraftVersion ?? "";
            if (string.IsNullOrWhiteSpace(gameVersion))
            {
                await AppDialog.ShowMessageAsync(XamlRoot, "无法导出",
                    "读不到该版本的游戏信息（缺少 xo-instance.json 或未记录 Minecraft 版本）。" +
                    "整合包链接需要标明游戏版本，接收方才能装出一致的实例。");
                return;
            }

            var loader = string.IsNullOrWhiteSpace(meta!.Loader) ? "vanilla" : meta.Loader!;
            var loaderVersion = meta.LoaderVersion ?? "";
            var folderName = Path.GetFileName(_instanceDir.TrimEnd('\\', '/'));
            var packName = string.IsNullOrWhiteSpace(meta.DisplayName) ? folderName : meta.DisplayName!;

            ExportModpackButton.IsEnabled = false;
            StatusText.Text = "正在生成整合包清单…";
            ModpackExportService.ExportOutcome outcome;
            try
            {
                var progress = new Progress<string>(m => StatusText.Text = m);
                outcome = await ModpackExportService.BuildAsync(
                    _contentDir, packName, gameVersion, loader, loaderVersion, progress);
            }
            catch (Exception ex)
            {
                ExportModpackButton.IsEnabled = true;
                StatusText.Text = "";
                await AppDialog.ShowMessageAsync(XamlRoot, "导出失败", ex.Message);
                return;
            }
            ExportModpackButton.IsEnabled = true;
            StatusText.Text = "";

            if (outcome.Manifest.Mods.Count == 0 && outcome.Manifest.Config.Count == 0)
            {
                await AppDialog.ShowMessageAsync(XamlRoot, "没有可导出的内容",
                    "该版本没有已启用的模组，也没有可内联的配置文件。先安装一些模组再导出整合包。");
                return;
            }

            var link = ModpackExportService.ToShareLink(outcome.Manifest);
            CopyToClipboard(link);
            await ShowExportResultAsync(outcome, link);
        }

        private async Task ShowExportResultAsync(ModpackExportService.ExportOutcome outcome, string link)
        {
            var m = outcome.Manifest;
            var loaderLabel = m.Loader == "vanilla" ? "原版" : FormatHelper.FormatLoaderName(m.Loader);
            var summary = $"整合包「{m.Name}」 · {m.GameVersion} · {loaderLabel}\n" +
                          $"已收录 {m.Mods.Count} 个模组、{outcome.ConfigFileCount} 个配置文件。链接已复制到剪贴板。";

            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(new TextBlock
            {
                Text = summary,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = ThemeService.Brush("TextPrimaryBrush")
            });

            if (outcome.Unresolved.Count > 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"以下 {outcome.Unresolved.Count} 个模组在 Modrinth 上没匹配到，未包含进链接" +
                           "（接收方需自行放置）：\n· " + string.Join("\n· ", outcome.Unresolved.Take(30)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                });
            }
            if (outcome.ConfigTruncated || outcome.ConfigSkipped.Count > 0)
            {
                var note = outcome.ConfigTruncated
                    ? $"配置体积超出内联上限，已跳过 {outcome.ConfigSkipped.Count} 个文件（超大或非文本）。"
                    : $"已跳过 {outcome.ConfigSkipped.Count} 个非文本 / 空配置文件。";
                body.Children.Add(new TextBlock
                {
                    Text = note,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                });
            }

            var linkBox = new TextInput
            {
                Text = link,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MaxHeight = 120,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            body.Children.Add(linkBox);

            var result = await AppDialog.ShowAsync(
                XamlRoot, "整合包分享链接", "", primary: "复制链接", close: "关闭", body: body, maxWidth: 620);
            if (result == AppDialogResult.Primary)
            {
                CopyToClipboard(link);
                StatusText.Text = "分享链接已复制到剪贴板";
            }
        }

        private static void CopyToClipboard(string text)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Modpack", "复制分享链接到剪贴板失败", ex);
            }
        }
    }
}
