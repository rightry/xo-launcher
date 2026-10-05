using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 头像浮层的主体：只显示当前账号 + 一键切换。设置项都在设置页的「账号」分组里。
    /// </summary>
    public sealed partial class AccountSwitcherBody : UserControl
    {
        private bool _syncing;

        public AccountSwitcherBody()
        {
            InitializeComponent();
            AccountStore.Instance.Changed += OnAccountsChanged;
            // AccountStore 是静态的，浮层关掉后不摘钩子就会一直攥着这份控件
            Unloaded += (_, _) => AccountStore.Instance.Changed -= OnAccountsChanged;
            Reload();
        }

        /// <summary>点了「账号与皮肤来源设置…」，由主窗口收起浮层并跳到设置页的账号分组。</summary>
        public event Action? ManageRequested;

        private void OnAccountsChanged() => DispatcherQueue.TryEnqueue(Reload);

        private void Reload()
        {
            var selected = AccountStore.Instance.Selected;
            CurrentNameText.Text = selected?.Username ?? "还没有账号";
            CurrentTypeText.Text = selected?.TypeLabel ?? "";
            CurrentAvatar.Show(selected);

            _syncing = true;
            try
            {
                AccountList.ItemsSource = AccountStore.Instance.Accounts;
                AccountList.SelectedItem = selected;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing) return;
            if (AccountList.SelectedItem is not GameAccount account) return;
            var current = AccountStore.Instance.Selected;
            if (current is not null
                && string.Equals(current.Id, account.Id, StringComparison.OrdinalIgnoreCase)) return;
            AccountStore.Instance.Select(account.Id);
            StatusText.Text = "已切换到「" + account.Username + "」。";
        }

        private void Manage_Click(object sender, RoutedEventArgs e) => ManageRequested?.Invoke();
    }
}
