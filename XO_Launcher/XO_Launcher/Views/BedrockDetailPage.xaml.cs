using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class BedrockDetailPage : UserControl
    {
        private DownloadItem? _item;
        private bool _busy;
        private bool _installed;

        public event EventHandler? BackRequested;

        public BedrockDetailPage()
        {
            InitializeComponent();
            BackButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public void Show(DownloadItem item)
        {
            _item = item;
            _busy = false;
            QueueTipBorder.Visibility = Visibility.Collapsed;
            TitleText.Text = item.Title;
            TypeText.Text = item.Badge;
            MetaText.Text = string.IsNullOrWhiteSpace(item.Meta) ? "Minecraft 基岩版" : item.Meta;
            DescriptionText.Text = item.Description;
            IconText.Text = item.KindIsServer ? "S" : "B";
            IconBorder.Background = new SolidColorBrush(ParseColor(item.IconColor));
            RefreshStatus();
        }

        public void Cancel() { }

        private bool IsServer => _item?.KindIsServer == true;
        private bool IsPreview =>
            string.Equals(_item?.BedrockKind, "preview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_item?.VersionType, "snapshot", StringComparison.OrdinalIgnoreCase)
            || BedrockIds.IsPreviewChannel(_item?.Slug);
        private bool CanSideload => !IsServer && !string.IsNullOrWhiteSpace(_item?.UpdateId);

        private void RefreshStatus()
        {
            if (_item is null) return;
            var developer = BedrockEditionService.IsDeveloperModeEnabled();
            if (IsServer)
            {
                _installed = BedrockEditionService.IsServerInstalled(_item.Slug);
                StatusText.Text = _installed
                    ? "已安装，可从本页或首页启动 bedrock_server.exe"
                    : "尚未下载该专用服务器";
                StatusHintText.Text = "服务器文件保存在游戏目录的 versions 文件夹中，与 Java 版实例并列。";
                PrimaryButtonText.Text = _installed ? "启动服务器" : "下载并安装";
                PrimaryIcon.Glyph = _installed ? "\uF60F" : "\uF150";
                SecondaryButton.Visibility = Visibility.Collapsed;
            }
            else if (CanSideload)
            {
                _installed = BedrockEditionService.IsSideloadInstalled(_item.Slug);
                StatusText.Text = _installed
                    ? "已下载到游戏目录，启动时会注册并切换到该版本"
                    : "尚未下载该版本";
                StatusHintText.Text = developer
                    ? "下载需要已购买 Minecraft for Windows 的微软账号。多个版本可并排存放，同时只能注册其中一个。"
                    : "请先开启 Windows 开发人员模式，才能像 BedrockBoot 一样并排安装历史版本。";
                PrimaryButtonText.Text = _installed ? "启动游戏" : "下载并安装";
                PrimaryIcon.Glyph = _installed ? "\uF60F" : "\uF150";
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButtonText.Text = developer ? "在 Microsoft Store 中查看" : "打开开发人员设置";
            }
            else
            {
                var pkg = BedrockEditionService.FindPackage(IsPreview);
                _installed = pkg is not null;
                StatusText.Text = _installed
                    ? $"已安装 · Store 版本 {pkg!.Version}"
                    : "尚未从 Microsoft Store 安装";
                StatusHintText.Text = _installed
                    ? "启动将打开当前已注册的 UWP 客户端。"
                    : "点击下方按钮打开 Microsoft Store。安装完成后返回启动器即可启动。";
                PrimaryButtonText.Text = _installed ? "启动游戏" : "在 Microsoft Store 中安装";
                PrimaryIcon.Glyph = _installed ? "\uF60F" : "\uF58C";
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButtonText.Text = _installed ? "在 Microsoft Store 中查看更新" : "打开 Microsoft Store";
            }

            PrimaryButton.IsEnabled = true;
        }

        private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item is null || _busy) return;
            _busy = true;
            PrimaryButton.IsEnabled = false;
            try
            {
                if (IsServer)
                {
                    if (_installed)
                        await LaunchServerAsync();
                    else
                        await DownloadServerAsync();
                }
                else if (CanSideload)
                {
                    if (_installed)
                    {
                        await BedrockEditionService.LaunchSideloadAsync(_item.Slug);
                        LauncherSettings.Instance.SelectedVersion = _item.Slug;
                        ShowTip("已切换并启动该基岩版");
                    }
                    else
                    {
                        await DownloadClientAsync();
                    }
                }
                else if (_installed)
                {
                    await BedrockEditionService.LaunchClientAsync(_item.Slug);
                    LauncherSettings.Instance.SelectedVersion = _item.Slug;
                    ShowTip("已启动基岩版");
                }
                else
                {
                    await BedrockEditionService.OpenStoreAsync(IsPreview);
                    ShowTip("已打开 Microsoft Store。安装完成后返回首页即可启动。");
                }
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "基岩版操作失败", ex);
                ShowTip(ex.Message);
            }
            finally
            {
                _busy = false;
                RefreshStatus();
            }
        }

        private async void SecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_item is null) return;
            try
            {
                if (CanSideload && !BedrockEditionService.IsDeveloperModeEnabled())
                {
                    await BedrockEditionService.OpenDeveloperSettingsAsync();
                    ShowTip("已打开开发人员设置。启用后再回来下载。");
                    return;
                }
                await BedrockEditionService.OpenStoreAsync(IsPreview);
                ShowTip("已打开 Microsoft Store");
            }
            catch (Exception ex)
            {
                ShowTip(ex.Message);
            }
        }

        private async Task LaunchServerAsync()
        {
            if (_item is null) return;
            BedrockEditionService.LaunchServer(_item.Slug);
            LauncherSettings.Instance.SelectedVersion = _item.Slug;
            ShowTip("已启动基岩专用服务器（控制台窗口）");
            await Task.CompletedTask;
        }

        private async Task DownloadClientAsync()
        {
            if (_item is null) return;
            var catalog = ToCatalog();
            if (string.IsNullOrWhiteSpace(catalog.Version) || string.IsNullOrWhiteSpace(catalog.UpdateId))
                throw new InvalidOperationException("无法识别该基岩版的下载信息");

            PrimaryButtonText.Text = "正在加入队列...";
            var task = DownloadService.Instance.StartInstall(catalog.Title + " " + catalog.Version, catalog.Id);
            ShowTip("已开始下载基岩版客户端，进度请到顶部「消息」查看");
            try
            {
                await BedrockEditionService.InstallClientAsync(catalog, task, task.Token);
                BedrockEditionService.InvalidatePackageCache();
                InstalledVersionService.Invalidate();
                LauncherSettings.Instance.SelectedVersion = catalog.Id;
                ShowTip($"安装完成：{catalog.Version}。可返回首页启动。");
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                ShowTip("已终止下载");
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "基岩版客户端安装失败", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Progress = 0;
                    t.Detail = ex.Message;
                    t.ProgressHint = "安装失败";
                });
                ShowTip("安装失败：" + ex.Message);
            }
        }

        private async Task DownloadServerAsync()
        {
            if (_item is null) return;
            var catalog = ToCatalog();
            if (string.IsNullOrWhiteSpace(catalog.Version))
                throw new InvalidOperationException("无法识别服务器版本号");

            PrimaryButtonText.Text = "正在加入队列...";
            var task = DownloadService.Instance.StartInstall(catalog.Title + " " + catalog.Version, catalog.Id);
            ShowTip("已开始下载官方专用服务器，进度请到顶部「消息」查看");
            try
            {
                await BedrockEditionService.InstallServerAsync(catalog, task, task.Token);
                InstalledVersionService.Invalidate();
                LauncherSettings.Instance.SelectedVersion = catalog.Id;
                ShowTip($"安装完成：{catalog.Title} {catalog.Version}。可返回首页启动。");
            }
            catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token))
            {
                DownloadService.Instance.MarkCancelled(task);
                ShowTip("已终止下载");
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Bedrock", "专用服务器安装失败", ex);
                DownloadService.Instance.UpdateTask(task, t =>
                {
                    t.Status = "下载失败";
                    t.Progress = 0;
                    t.Detail = ex.Message;
                    t.ProgressHint = "安装失败";
                });
                ShowTip("安装失败：" + ex.Message);
            }
        }

        private BedrockCatalogItem ToCatalog()
        {
            var item = _item!;
            return new BedrockCatalogItem
            {
                Id = item.Slug,
                Title = item.Title,
                Version = ExtractVersion(item.Slug, item.DownloadUrl, item.Meta),
                Kind = IsServer ? "server" : (IsPreview ? "preview" : "client"),
                Channel = IsPreview ? "preview" : "release",
                DownloadUrl = string.IsNullOrWhiteSpace(item.DownloadUrl) ? null : item.DownloadUrl,
                UpdateId = string.IsNullOrWhiteSpace(item.UpdateId) ? null : item.UpdateId
            };
        }

        private static string ExtractVersion(string id, string? url, string? meta)
        {
            foreach (var prefix in new[] { "bedrock-preview-", "bedrock-server-", "bedrock-" })
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Length > prefix.Length)
                    return id[prefix.Length..];
            }
            if (!string.IsNullOrWhiteSpace(url))
            {
                const string serverPrefix = "bedrock-server-";
                var name = System.IO.Path.GetFileNameWithoutExtension(url);
                var idx = name.IndexOf(serverPrefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) return name[(idx + serverPrefix.Length)..];
            }
            if (!string.IsNullOrWhiteSpace(meta))
            {
                var token = meta.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (token.Length > 0 && char.IsDigit(token[0][0]))
                    return token[0];
            }
            return "";
        }

        private void ShowTip(string message)
        {
            QueueTip.Text = message;
            QueueTipBorder.Visibility = Visibility.Visible;
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
            return Color.FromArgb(255, 61, 122, 60);
        }
    }
}
