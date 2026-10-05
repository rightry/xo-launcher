using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 账号设置主体：当前账号、皮肤来源（SkinSourceEditor）、账号列表与登录入口。
    /// 宿主是设置页的「账号」分组；头像浮层只做切换，见 AccountSwitcherBody。
    /// </summary>
    public sealed partial class AccountSettingsBody : UserControl
    {
        private bool _loginBusy;

        public AccountSettingsBody()
        {
            InitializeComponent();
            AccountStore.Instance.Changed += OnAccountsChanged;
            // 浮层关掉后这个控件就没人了，但 AccountStore 是静态的：不摘钩子就会一直攥着它
            Unloaded += (_, _) => AccountStore.Instance.Changed -= OnAccountsChanged;
            ReloadAccounts();
        }

        private void OnAccountsChanged()
            => DispatcherQueue.TryEnqueue(ReloadAccounts);

        private void ReloadAccounts()
        {
            var selected = AccountStore.Instance.Selected;
            if (selected is null)
            {
                CurrentNameText.Text = "还没有账号";
                CurrentTypeText.Text = "";
                CurrentSkinSourceText.Text = "";
                CurrentAvatar.Show(null);
            }
            else
            {
                CurrentNameText.Text = selected.Username;
                CurrentTypeText.Text = selected.TypeLabel;
                CurrentSkinSourceText.Text = "当前来源：" + selected.SkinSourceLabel;
                CurrentAvatar.Show(selected);
            }

            AccountListHost.Children.Clear();
            foreach (var account in AccountStore.Instance.Accounts)
            {
                var isSelected = selected is not null
                    && string.Equals(account.Id, selected.Id, StringComparison.OrdinalIgnoreCase);
                AccountListHost.Children.Add(CreateAccountRow(account, isSelected));
            }
        }

        // ==================== 账号列表 / 登录 ====================

        private async void MicrosoftLogin_Click(object sender, RoutedEventArgs e)
        {
            if (_loginBusy) return;
            _loginBusy = true;
            MicrosoftLoginButton.IsEnabled = false;
            AccountStatusText.Text = "已打开系统浏览器，请在浏览器里完成微软登录…";
            try
            {
                var progress = new Progress<string>(text => DispatcherQueue.TryEnqueue(() => AccountStatusText.Text = text));
                var account = await MicrosoftAuthService.LoginWithBrowserAsync(progress, CancellationToken.None);
                AccountStore.Instance.AddOrReplaceMicrosoft(account);
                AccountStatusText.Text = "已登录微软正版账号 " + account.Username;
            }
            catch (OperationCanceledException)
            {
                AccountStatusText.Text = "已取消微软登录。";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Account", "微软登录失败", ex);
                AccountStatusText.Text = "登录失败：" + ex.Message;
            }
            finally
            {
                MicrosoftLoginButton.IsEnabled = true;
                _loginBusy = false;
            }
        }

        private void AddOffline_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AccountStore.Instance.AddOffline(OfflineNameBox.Text);
                OfflineNameBox.Text = "";
                AccountStatusText.Text = "已添加离线账号。";
            }
            catch (Exception ex)
            {
                AccountStatusText.Text = ex.Message;
            }
        }

        private UIElement CreateAccountRow(GameAccount account, bool selected)
        {
            var card = new Border
            {
                Background = ThemeService.Brush("ChipBackgroundBrush"),
                BorderBrush = selected ? ThemeService.Brush("AccentBrush") : ThemeService.Brush("DividerBrush"),
                BorderThickness = new Thickness(selected ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 10, 10)
            };
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new PlayerAvatar { AvatarSize = 36 };
            avatar.Show(account);

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = account.Username,
                FontSize = 14,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
                Foreground = ThemeService.Brush("TextPrimaryBrush")
            });
            info.Children.Add(new TextBlock
            {
                Text = selected ? account.TypeLabel + " · 当前使用" : account.TypeLabel,
                FontSize = 11,
                Foreground = account.IsMicrosoft ? ThemeService.Brush("AccentBrush") : ThemeService.Brush("TextSecondaryBrush")
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (!selected)
            {
                var use = IconActionButton("使用此账号", ThemeService.Brush("NeutralButtonBrush"),
                    ThemeService.Brush("NeutralButtonForegroundBrush"));
                use.Click += (_, _) => AccountStore.Instance.Select(account.Id);
                actions.Children.Add(use);
            }
            var remove = IconActionButton("删除账号", ThemeService.Brush("DangerButtonBrush"),
                ThemeService.Brush("DangerButtonForegroundBrush"));
            remove.Click += async (_, _) => await ConfirmRemoveAccountAsync(account);
            actions.Children.Add(remove);

            Grid.SetColumn(info, 1);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(avatar);
            grid.Children.Add(info);
            grid.Children.Add(actions);
            card.Child = grid;
            return card;
        }

        private static Button IconActionButton(string tip, Brush background, Brush foreground)
        {
            var button = new Button
            {
                Width = 32,
                Height = 28,
                Padding = new Thickness(0),
                Background = background,
                Foreground = foreground,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                // 选中 = checkmark(F294)，删除 = dismiss(F369)：码点已在图标字体里核对过
                Content = new FontIcon
                {
                    FontFamily = ThemeService.IconFontFamily,
                    Glyph = tip == "使用此账号" ? "\uF294" : "\uF34C",
                    FontSize = 12
                }
            };
            ToolTipService.SetToolTip(button, tip);
            return button;
        }

        private async Task ConfirmRemoveAccountAsync(GameAccount account)
        {
            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "删除账号",
                "确定删除「" + account.Username + "」？此操作无法撤销。",
                primary: "删除",
                close: "取消");
            if (result == AppDialogResult.Primary)
                AccountStore.Instance.Remove(account.Id);
        }
    }
}
