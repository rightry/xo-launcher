using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class MicrosoftLoginOverlay : UserControl
    {
        private CancellationTokenSource? _cts;

        public MicrosoftLoginOverlay()
        {
            InitializeComponent();
        }

        public async Task<GameAccount> RunAsync()
        {
            _cts = new CancellationTokenSource();
            StatusText.Text = "请在浏览器中完成微软登录，完成后会自动返回启动器。不会在软件内输入账号密码。";
            var progress = new Progress<string>(text =>
            {
                DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
            });
            try
            {
                return await MicrosoftAuthService.LoginWithBrowserAsync(progress, _cts.Token);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch (Exception) { }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    }
}
