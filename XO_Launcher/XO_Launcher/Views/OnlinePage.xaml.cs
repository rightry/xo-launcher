using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class OnlinePage : UserControl
    {
        private bool _busy;
        private bool _watching;

        public OnlinePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public void Refresh()
        {
            ApplySnapshot(TerracottaService.Instance.Current);
            _ = EnsureAsync(installIfMissing: false);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_watching) return;
            _watching = true;
            TerracottaService.Instance.Changed += OnStateChanged;
            ApplySnapshot(TerracottaService.Instance.Current);
            _ = EnsureAsync(installIfMissing: false);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_watching) return;
            _watching = false;
            TerracottaService.Instance.Changed -= OnStateChanged;
            TerracottaService.Instance.StopPolling();
        }

        private void OnStateChanged(TerracottaSnapshot snapshot) => ApplySnapshot(snapshot);

        private async void Install_Click(object sender, RoutedEventArgs e) => await EnsureAsync(installIfMissing: true);

        private async void Host_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var installed = InstalledVersionService.Find(LauncherSettings.Instance.SelectedVersion);
            if (installed?.IsBedrock == true)
            {
                StatusDetail.Text = "陶瓦联机仅支持 Java 版。请先选择一个 Java 版本。";
                return;
            }

            _busy = true;
            try
            {
                StatusTitle.Text = "正在创建房间…";
                await TerracottaService.Instance.EnsureReadyAsync(new Progress<string>(msg =>
                    DispatcherQueue.TryEnqueue(() => StatusDetail.Text = msg)));
                await TerracottaService.Instance.HostAsync();
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Online", "创建房间失败", ex);
                StatusTitle.Text = "创建房间失败";
                StatusDetail.Text = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async void Join_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                StatusTitle.Text = "正在加入房间…";
                await TerracottaService.Instance.EnsureReadyAsync(new Progress<string>(msg =>
                    DispatcherQueue.TryEnqueue(() => StatusDetail.Text = msg)));
                await TerracottaService.Instance.JoinAsync(JoinCodeBox.Text);
                var guest = TerracottaService.Instance.LastGuestPlayerName;
                if (TerracottaService.NamesDiffer(guest, AccountStore.Instance.Selected.Username))
                    StatusDetail.Text = "已加入。游戏里将使用「" + guest + "」，避免和房主同名被踢。请点「启动并加入」。";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Online", "加入房间失败", ex);
                StatusTitle.Text = "加入房间失败";
                StatusDetail.Text = ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await TerracottaService.Instance.StopSessionAsync();
            }
            catch (Exception ex)
            {
                StatusDetail.Text = ex.Message;
            }
        }

        private void CopyRoom_Click(object sender, RoutedEventArgs e) => Copy(RoomCodeBox.Text, "房间码");

        private void CopyUrl_Click(object sender, RoutedEventArgs e) => Copy(JoinUrlBox.Text, "连接地址");

        private void LaunchJoin_Click(object sender, RoutedEventArgs e)
        {
            var url = (JoinUrlBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                StatusDetail.Text = "还没有连接地址。请先加入房间。";
                return;
            }
            if (App.MainWindow is MainWindow window)
                window.RequestLaunch(url);
        }

        private async Task EnsureAsync(bool installIfMissing)
        {
            if (_busy) return;
            var service = TerracottaService.Instance;
            if (!installIfMissing && !service.IsInstalled && !service.IsReady)
            {
                ApplySnapshot(TerracottaSnapshot.Uninitialized());
                InstallButton.Visibility = Visibility.Visible;
                StatusTitle.Text = "尚未安装陶瓦联机内核";
                StatusDetail.Text = "首次使用需要下载约几十 MB 的官方内核，之后可直接创建或加入房间。";
                return;
            }

            _busy = true;
            InstallButton.IsEnabled = false;
            var failed = false;
            try
            {
                StatusTitle.Text = "正在准备陶瓦联机…";
                await service.EnsureReadyAsync(new Progress<string>(msg =>
                    DispatcherQueue.TryEnqueue(() => StatusDetail.Text = msg)));
            }
            catch (Exception ex)
            {
                failed = true;
                LauncherLogService.Error("Online", "准备陶瓦联机失败", ex);
                StatusTitle.Text = "无法启动陶瓦联机";
                StatusDetail.Text = ex.Message;
                InstallButton.Visibility = Visibility.Visible;
            }
            finally
            {
                _busy = false;
                InstallButton.IsEnabled = true;
                if (!failed)
                    ApplySnapshot(service.Current);
            }
        }

        private void ApplySnapshot(TerracottaSnapshot snapshot)
        {
            var ready = TerracottaService.Instance.IsReady || TerracottaService.Instance.IsInstalled;
            StatusTitle.Text = snapshot.Headline();
            var detail = snapshot.Detail();
            if (!string.IsNullOrWhiteSpace(detail))
                StatusDetail.Text = detail;
            else if (snapshot.Kind is "waiting" or "uninitialized")
                StatusDetail.Text = ready
                    ? "内核 " + TerracottaService.KernelVersion + " 已就绪。"
                    : "首次使用需要下载官方内核。";

            InstallButton.Visibility = TerracottaService.Instance.IsReady ? Visibility.Collapsed : Visibility.Visible;
            InstallButton.Content = TerracottaService.Instance.IsInstalled ? "启动内核" : "安装内核";
            StopButton.Visibility = snapshot.HasSession || snapshot.Kind == "exception"
                ? Visibility.Visible : Visibility.Collapsed;

            // 一次只给一块面板：没在房间里就别把房间码 / 连接地址 / 成员的空壳一起摊出来
            var inRoom = snapshot.IsHosting || snapshot.IsJoining;
            IdlePanel.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
            HostPanel.Visibility = snapshot.IsHosting ? Visibility.Visible : Visibility.Collapsed;
            GuestPanel.Visibility = snapshot.IsJoining ? Visibility.Visible : Visibility.Collapsed;
            MembersPanel.Visibility = snapshot.HasSession ? Visibility.Visible : Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(snapshot.Room))
                RoomCodeBox.Text = snapshot.Room;
            else if (snapshot.Kind is "waiting" or "uninitialized")
                RoomCodeBox.Text = "尚未创建房间";
            if (!string.IsNullOrWhiteSpace(snapshot.Url))
                JoinUrlBox.Text = snapshot.Url;

            if (snapshot.IsHosting)
            {
                JoinHintText.Text = "你正在主持房间。把上方房间码发给好友，让对方点「加入」。本机加入自己的房间会找不到房主。";
            }
            else if (snapshot.IsJoining)
            {
                var guest = TerracottaService.Instance.LastGuestPlayerName;
                JoinHintText.Text = TerracottaService.NamesDiffer(guest, AccountStore.Instance.Selected.Username)
                    ? "已连通。请点「启动并加入」。游戏将使用「" + guest + "」，避免和房主同名被占用。"
                    : "已连通。请点「启动并加入」。不要填写自己创建的房间码。";
            }
            else
            {
                JoinHintText.Text = "输入好友给你的房间码。连通后请点「启动并加入」。和房主同名时会自动加后缀，否则游戏会提示名称被占用。不要填写自己创建的房间码。";
            }

            PlayerHost.Children.Clear();
            EmptyPlayersText.Visibility = snapshot.Players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var player in snapshot.Players)
            {
                var kind = string.Equals(player.Kind, "HOST", StringComparison.OrdinalIgnoreCase) ? "房主" : "成员";
                var info = new StackPanel { Spacing = 2 };
                info.Children.Add(new TextBlock
                {
                    Text = player.Name,
                    FontSize = 13,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
                    Foreground = ThemeService.Brush("TextPrimaryBrush")
                });
                info.Children.Add(new TextBlock
                {
                    Text = kind + (string.IsNullOrWhiteSpace(player.Vendor) ? "" : " · " + player.Vendor),
                    FontSize = 11,
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                });
                PlayerHost.Children.Add(new Border
                {
                    // 无卡片布局后成员行不再用 chip 底色，改成一条极淡的底 + 左侧竖线，
                    // 保持「列表项」的可读性而不引入第二层卡片感。
                    Background = ThemeService.Brush("RecessedBackgroundBrush"),
                    BorderBrush = ThemeService.Brush("DividerBrush"),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = info
                });
            }
        }

        private void Copy(string? text, string label)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusDetail.Text = "还没有可复制的" + label + "。";
                return;
            }
            var data = new DataPackage();
            data.SetText(text.Trim());
            Clipboard.SetContent(data);
            StatusDetail.Text = "已复制" + label + "。";
        }
    }
}
