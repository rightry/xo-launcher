using System;
using System.Security;
using Microsoft.Windows.AppNotifications;

namespace XO_Launcher.Services
{
    /// <summary>
    /// Windows 系统通知（Toast）。首次使用时向 AppNotificationManager 惰性注册；
    /// 用户点击通知后触发 <see cref="PageRequested"/>，由 MainWindow 跳转到对应页面。
    /// 是否弹出由 LauncherSettings 里的通知开关控制。
    /// </summary>
    public static class NotificationService
    {
        private static bool _registered;
        private static bool _registerFailed;

        /// <summary>用户点击了系统通知，参数为要跳转的页面键（UI 线程触发）。</summary>
        public static event Action<string>? PageRequested;

        /// <summary>启动时调用：提前注册通知激活，使点击旧通知也能唤起本实例。</summary>
        public static void Initialize() => EnsureRegistered();

        public static void DownloadComplete(string title) => Post(
            LauncherSettings.Instance.NotifyDownloadComplete,
            "下载完成", title, "messages");

        public static void DownloadFailed(string title, string error) => Post(
            LauncherSettings.Instance.NotifyDownloadComplete,
            "下载失败", $"{title}：{error}", "messages");

        public static void GameCrash(string kind, string versionId) => Post(
            LauncherSettings.Instance.NotifyGameCrash,
            "游戏崩溃", $"{versionId} · {kind}，故障分析已生成", "messages");

        public static void AiConfirm(string describe) => Post(
            LauncherSettings.Instance.NotifyAiConfirm,
            "AI 请求确认", describe, "ai");

        /// <summary>应用退出时注销通知管理器。</summary>
        public static void Shutdown()
        {
            if (!_registered) return;
            try { AppNotificationManager.Default.Unregister(); }
            catch (Exception) { }
        }

        private static void Post(bool categoryEnabled, string title, string message, string? page)
        {
            if (!LauncherSettings.Instance.NotificationsEnabled || !categoryEnabled) return;
            if (!EnsureRegistered()) return;
            try
            {
                var args = string.IsNullOrWhiteSpace(page) ? "xo=notify" : $"xo=notify&page={page}";
                var xml = "<toast activationType=\"foreground\" arguments=\"" + SecurityElement.Escape(args) + "\">"
                    + "<visual><binding template=\"ToastGeneric\">"
                    + "<text>" + SecurityElement.Escape(title) + "</text>"
                    + "<text>" + SecurityElement.Escape(message) + "</text>"
                    + "</binding></visual></toast>";
                AppNotificationManager.Default.Show(new AppNotification(xml));
                LauncherLogService.Info("Notify", $"已发送系统通知:{title} - {message}");
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Notify", "发送系统通知失败", ex);
            }
        }

        private static bool EnsureRegistered()
        {
            if (_registered) return true;
            if (_registerFailed) return false;
            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += OnNotificationInvoked;
                var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "XO_Launcher.ico");
                if (System.IO.File.Exists(icon))
                    manager.Register("XO Launcher", new Uri(icon));
                else
                    manager.Register();
                _registered = true;
                return true;
            }
            catch (Exception ex)
            {
                _registerFailed = true;
                LauncherLogService.Error("Notify", "注册系统通知失败", ex);
                return false;
            }
        }

        private static void OnNotificationInvoked(
            AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            string? page = null;
            try
            {
                if (args.Arguments.TryGetValue("page", out var value)
                    && !string.IsNullOrWhiteSpace(value))
                    page = value;
            }
            catch (Exception)
            {
                // 参数解析失败时忽略跳转
            }
            if (page is null) return;
            UiDispatch.Run(() => PageRequested?.Invoke(page));
        }
    }
}
