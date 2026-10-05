using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class MessagesPage : UserControl
    {
        private readonly DispatcherTimer _gameStatusTimer;

        public MessagesPage()
        {
            InitializeComponent();
            TasksList.ItemsSource = DownloadService.Instance.Tasks;
            foreach (var task in DownloadService.Instance.Tasks)
                task.PropertyChanged += OnTaskPropertyChanged;
            UpdateEmptyState();

            _gameStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _gameStatusTimer.Tick += (_, _) => RefreshGameStatus();

            DownloadService.Instance.TaskAdded += OnTaskAdded;
            DownloadService.Instance.Tasks.CollectionChanged += OnTasksChanged;
            Loaded += (_, _) =>
            {
                RefreshSessionCrash();
                RefreshGameStatus();
                _gameStatusTimer.Start();
            };
            Unloaded += (_, _) => _gameStatusTimer.Stop();
        }

        /// <summary>刷新「当前游戏状态」卡片：游戏运行中显示版本与时长，并提供关闭按钮。</summary>
        public void RefreshGameStatus()
        {
            if (GameStatusCard is null) return;
            var process = MainWindow.RunningGame;
            var alive = false;
            try { alive = process is not null && !process.HasExited; }
            catch (Exception) { alive = false; }

            if (!alive)
            {
                GameStatusCard.Visibility = Visibility.Collapsed;
                return;
            }

            GameStatusCard.Visibility = Visibility.Visible;
            var elapsed = DateTime.UtcNow - MainWindow.RunningGameStartedUtc;
            var spanText = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分"
                : $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
            if (MainWindow.RunningGameWindowShown)
            {
                GameStatusTitle.Text = "游戏运行中 · " + MainWindow.RunningGameVersion;
                GameStatusDetail.Text = $"已运行 {spanText}";
            }
            else
            {
                GameStatusTitle.Text = "游戏启动中 · " + MainWindow.RunningGameVersion;
                GameStatusDetail.Text = "正在等待游戏窗口显示…";
            }
        }

        private void CloseGameButton_Click(object sender, RoutedEventArgs e)
        {
            var process = MainWindow.RunningGame;
            if (process is null || process.HasExited) return;

            try
            {
                // 标记为用户手动关闭，退出后跳过崩溃分析
                MainWindow.GameClosedByUser = true;
                if (!process.CloseMainWindow())
                    process.Kill();
                LauncherLogService.Info("Messages", $"用户手动关闭了游戏进程（{MainWindow.RunningGameVersion}）");
                CloseGameButton.IsEnabled = false;
                _ = PollGameClosedAsync(process);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Messages", "关闭游戏进程失败", ex);
            }
        }

        /// <summary>等待游戏进程退出后刷新卡片（超时 10 秒则强制结束）。</summary>
        private async Task PollGameClosedAsync(Process process)
        {
            try
            {
                for (var i = 0; i < 100 && !process.HasExited; i++)
                    await Task.Delay(100);
                if (!process.HasExited)
                    process.Kill();
            }
            catch (Exception)
            {
                // 进程已退出等异常按已关闭处理
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CloseGameButton is not null)
                    CloseGameButton.IsEnabled = true;
                RefreshGameStatus();
            });
        }

        public void RefreshSessionCrash()
        {
            if (CrashCard is null || SessionCrashPanel is null) return;
            var crash = GameCrashService.LastCrash;
            if (crash is null)
            {
                CrashCard.Visibility = Visibility.Collapsed;
                return;
            }

            CrashCard.Visibility = Visibility.Visible;
            SessionCrashPanel.ShowActions = false;
            SessionCrashPanel.SetCrash(crash);
        }

        private void OnTaskAdded(DownloadTask _)
        {
            UpdateEmptyState();
        }

        private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (DownloadTask task in e.NewItems)
                    task.PropertyChanged += OnTaskPropertyChanged;
            }
            UpdateEmptyState();
        }

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DownloadTask.CanCancel) or nameof(DownloadTask.Status) or nameof(DownloadTask.IsActive))
                UpdateEmptyState();
        }

        private void CancelTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: DownloadTask task })
                DownloadService.Instance.Cancel(task);
        }

        private void DeleteTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: DownloadTask task })
                DownloadService.Instance.Remove(task);
        }

        private void CancelAllButton_Click(object sender, RoutedEventArgs e)
            => DownloadService.Instance.CancelAll();

        private async void ExportLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = await LogExportHelper.PickAndExportAsync();
                if (path is null) return;
                await AppDialog.ShowMessageAsync(XamlRoot, "错误日志已导出", path);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Messages", "导出错误日志失败", ex);
                await AppDialog.ShowMessageAsync(XamlRoot, "导出失败", ex.Message);
            }
        }

        private void AnalyzeCrashButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshSessionCrash();
        }

        private void AiCrashButton_Click(object sender, RoutedEventArgs e)
        {
            var crash = GameCrashService.LastCrash;
            if (crash is null) return;
            CrashAnalysisPanel.OpenAiForCrash(crash);
        }

        private async void ExportCrashButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var crash = GameCrashService.LastCrash;
                if (crash is null)
                {
                    await AppDialog.ShowMessageAsync(XamlRoot, "没有崩溃日志",
                        "本次运行尚未检测到崩溃。故障分析仅在本次启动器运行期间保留。");
                    return;
                }
                var path = await LogExportHelper.PickAndExportCrashAsync(crash);
                if (path is null) return;
                await AppDialog.ShowMessageAsync(XamlRoot, "崩溃日志已导出", path);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Messages", "导出崩溃日志失败", ex);
                await AppDialog.ShowMessageAsync(XamlRoot, "导出失败", ex.Message);
            }
        }

        private void UpdateEmptyState()
        {
            var hasTasks = DownloadService.Instance.Tasks.Any();
            EmptyPanel.Visibility = hasTasks ? Visibility.Collapsed : Visibility.Visible;
            TasksList.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;
            CancelAllButton.Visibility = DownloadService.Instance.Tasks.Any(t => t.CanCancel)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
