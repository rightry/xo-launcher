using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 皮肤来源编辑器：一个下拉 + 与之配套的本地文件 / URL 输入，作用于当前选中账号。
    /// 宿主有两个（账号浮层与皮肤绘制页），自己订阅 AccountStore.Changed 保持同步。
    /// </summary>
    public sealed partial class SkinSourceEditor : UserControl
    {
        private bool _syncing;

        public SkinSourceEditor()
        {
            InitializeComponent();
            AccountStore.Instance.Changed += OnAccountsChanged;
            // 静态 store 会攥着已经下线的控件，卸载时必须摘钩子
            Unloaded += (_, _) => AccountStore.Instance.Changed -= OnAccountsChanged;
            Refresh();
        }

        private void OnAccountsChanged() => DispatcherQueue.TryEnqueue(Refresh);

        public void Refresh()
        {
            var account = AccountStore.Instance.Selected;
            _syncing = true;
            try
            {
                SkinSourceCombo.IsEnabled = account is not null;
                var target = account?.SkinSource ?? GameAccount.SkinSourceDefault;
                SkinSourceCombo.SelectedItem = SkinSourceCombo.Items
                    .OfType<SelectBoxItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag as string, target, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _syncing = false;
            }

            var source = account?.SkinSource;
            LocalSkinRow.Visibility = source == GameAccount.SkinSourceLocal ? Visibility.Visible : Visibility.Collapsed;
            SkinUrlRow.Visibility = source == GameAccount.SkinSourceUrl ? Visibility.Visible : Visibility.Collapsed;
            LocalSkinPathText.Text = string.IsNullOrEmpty(account?.LocalSkinPath)
                ? "（尚未选择本地皮肤）" : account!.LocalSkinPath;
            SkinUrlBox.Text = account?.SkinUrl ?? "";
        }

        private void SkinSourceCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            if (SkinSourceCombo.SelectedItem is not SelectBoxItem { Tag: string tag }) return;
            var account = AccountStore.Instance.Selected;
            if (account is null) return;

            try
            {
                account.SkinSource = tag;
                // 切到 Mojang 不抹掉 LocalSkinPath：本地文件是用户数据，切回 local 还得重选一次，
                // 而且「上传到 Mojang」后也要留一份本地档。
                AccountStore.Instance.Persist();
                SkinAvatarService.InvalidateRemote(account.Uuid);
                Refresh();
                SkinStatusText.Text = "皮肤来源已切换为：" + account.SkinSourceLabel
                    + (tag == GameAccount.SkinSourceMojang && !account.IsMicrosoft
                        ? "（离线账号没有官方皮肤服务器，请改用「本地 PNG」）" : "");
            }
            catch (Exception ex)
            {
                SkinStatusText.Text = "保存失败：" + ex.Message;
            }
        }

        private async void PickLocalSkin_Click(object sender, RoutedEventArgs e)
        {
            var account = AccountStore.Instance.Selected;
            if (account is null)
            {
                SkinStatusText.Text = "请先添加账号。";
                return;
            }

            var filePicker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            filePicker.FileTypeFilter.Add(".png");
            InitializeWithWindow.Initialize(filePicker,
                App.MainWindow is not null ? WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero);
            var file = await filePicker.PickSingleFileAsync();
            if (file is null) return;

            try
            {
                SkinStatusText.Text = "正在保存本地皮肤…";

                // 先按 64×64 / 64×32 校验，避免把尺寸不对的图存成「已保存但游戏不认」。
                if (await SkinPngCodec.DecodeAsync(file.Path) is null)
                {
                    SkinStatusText.Text = "只支持 64×64 或 64×32 的 PNG 皮肤，这张图尺寸不符。";
                    return;
                }

                var saved = await SkinAvatarService.SetLocalSkinAsync(account.Uuid, file.Path);
                account.LocalSkinPath = saved;
                account.SkinSource = GameAccount.SkinSourceLocal;
                AccountStore.Instance.Persist();
                SkinAvatarService.Invalidate(account.Uuid);
                Refresh();

                if (account.IsMicrosoft)
                {
                    SkinStatusText.Text = "已保存到本地，正在上传到 Mojang…";
                    var uploaded = await MicrosoftAuthService.UploadSkinAsync(
                        account, saved, account.SkinVariant, null, CancellationToken.None);
                    uploaded.SkinSource = GameAccount.SkinSourceMojang;
                    uploaded.LocalSkinPath = saved;
                    AccountStore.Instance.AddOrReplaceMicrosoft(uploaded);
                    SkinAvatarService.InvalidateRemote(uploaded.Uuid);
                    Refresh();
                    SkinStatusText.Text = "已上传到 Mojang，进入游戏即为该皮肤。";
                    return;
                }

                SkinStatusText.Text = "已保存：" + saved + "（离线账号无官方皮肤服务器，此皮肤仅在本启动器内预览）";
            }
            catch (Exception ex)
            {
                SkinStatusText.Text = "保存失败：" + ex.Message;
                LauncherLogService.Warn("Skin", "皮肤来源里选择本地皮肤失败：" + ex.Message);
            }
        }

        private void ApplySkinUrl_Click(object sender, RoutedEventArgs e)
        {
            var account = AccountStore.Instance.Selected;
            if (account is null)
            {
                SkinStatusText.Text = "请先添加账号。";
                return;
            }

            var url = (SkinUrlBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                SkinStatusText.Text = "请填写 URL。";
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                SkinStatusText.Text = "URL 不合法（必须 http/https）。";
                return;
            }

            // 白名单：皮肤 URL 会在启动时拼进游戏参数，放开等于让任意站点喂给客户端贴图。
            var host = uri.Host.ToLowerInvariant();
            var allowed = host.EndsWith("mojang.com", StringComparison.Ordinal)
                          || host.EndsWith("minecraft.net", StringComparison.Ordinal)
                          || host is "crafatar.com" or "littleskin.cn"
                          || host.EndsWith(".crafatar.com", StringComparison.Ordinal)
                          || host.EndsWith(".littleskin.cn", StringComparison.Ordinal);
            if (!allowed)
            {
                SkinStatusText.Text = "URL 域名不在白名单（仅 mojang.com / minecraft.net / crafatar.com / littleskin.cn）。";
                return;
            }

            try
            {
                account.SkinUrl = url;
                account.SkinSource = GameAccount.SkinSourceUrl;
                AccountStore.Instance.Persist();
                SkinAvatarService.Invalidate(account.Uuid);
                Refresh();
                SkinStatusText.Text = "已应用 URL。";
            }
            catch (Exception ex)
            {
                SkinStatusText.Text = "保存失败：" + ex.Message;
            }
        }
    }
}
