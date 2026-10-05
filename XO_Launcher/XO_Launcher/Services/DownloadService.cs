using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 全局下载管理器。负责将下载任务加入队列、执行下载并把进度/状态回传到 UI 线程。
    /// 消息中心（MessagesPage）通过 <see cref="Tasks"/> 与 <see cref="TaskAdded"/> 展示进度。
    /// </summary>
    public sealed class DownloadService
    {
        private DispatcherQueue? _dispatcher;
        private readonly ConcurrentDictionary<string, DateTime> _lastProgressUi = new();

        private DownloadService() { }

        public static DownloadService Instance { get; } = new();

        public ObservableCollection<DownloadTask> Tasks { get; } = new();

        /// <summary>有新任务加入队列时触发（UI 线程）。</summary>
        public event Action<DownloadTask>? TaskAdded;

        /// <summary>加入下载队列（必须在 UI 线程调用）。进度由消息页绑定展示。</summary>
        public DownloadTask Enqueue(string title, string fileName, string url, long expectedSize = 0, string? destinationDirectory = null, string? projectType = null)
        {
            _dispatcher ??= DispatcherQueue.GetForCurrentThread();
            var task = new DownloadTask(title, fileName)
            {
                TotalBytes = expectedSize,
                ProjectType = projectType
            };
            Tasks.Add(task);
            TaskAdded?.Invoke(task);
            _ = DownloadAsync(task, url, destinationDirectory);
            return task;
        }

        /// <summary>创建一条由安装流程自行更新进度的任务（必须在 UI 线程调用）。</summary>
        public DownloadTask StartInstall(string title, string fileName, bool switchToMessages = true)
        {
            _dispatcher ??= DispatcherQueue.GetForCurrentThread();
            var task = new DownloadTask(title, fileName)
            {
                Status = "等待下载",
                SuppressMessageSwitch = !switchToMessages
            };
            Tasks.Add(task);
            TaskAdded?.Invoke(task);
            return task;
        }

        public void Cancel(DownloadTask task)
        {
            if (!task.CanCancel) return;
            task.RequestCancel();
            RunOnUi(() =>
            {
                task.Status = "正在取消";
                task.Detail = "正在停止下载…";
                task.ProgressHint = "正在终止";
            });
        }

        public void CancelAll()
        {
            foreach (var task in Tasks.Where(t => t.CanCancel).ToList())
                Cancel(task);
        }

        /// <summary>从消息中心删除一条任务记录（仅允许删除已结束的任务，进行中请先终止）。</summary>
        public void Remove(DownloadTask task)
        {
            if (task is null || task.IsActive) return;
            RunOnUi(() => Tasks.Remove(task));
        }

        public void UpdateTask(DownloadTask task, Action<DownloadTask> update)
        {
            RunOnUi(() =>
            {
                if (task.Token.IsCancellationRequested) return;
                update(task);
            });
        }

        public void MarkCancelled(DownloadTask task)
        {
            RunOnUi(() =>
            {
                task.Status = "已取消";
                task.Detail = "已终止下载";
                task.ProgressHint = "已终止";
            });
        }

        /// <summary>是否已有同名文件的下载任务在进行中。</summary>
        public bool IsDuplicate(string fileName)
            => Tasks.Any(t => t.FileName == fileName && t.IsActive);

        public string GetDownloadDirectory() => ModLibraryService.RootDirectory;

        /// <summary>
        /// 等这批任务全部离开活动态（已完成 / 下载失败 / 已取消）后再返回。
        ///
        /// 只等不催：<paramref name="ct"/> 取消只是结束等待，不会取消下载本身——
        /// 下载仍在后台继续，进度照旧在「消息」页显示。给 AI 工具层用，
        /// 这样模型拿到的是「下完了 / 哪个失败了」，而不是「已加入队列」这种还没发生的话。
        /// </summary>
        public static async Task WaitForAsync(IReadOnlyList<DownloadTask> tasks, CancellationToken ct)
        {
            if (tasks.Count == 0 || tasks.All(t => !t.IsActive)) return;

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new PropertyChangedEventHandler((_, e) =>
            {
                if (e.PropertyName != nameof(DownloadTask.Status)) return;
                if (tasks.All(t => !t.IsActive)) done.TrySetResult();
            });

            foreach (var task in tasks) task.PropertyChanged += handler;
            using var registration = ct.CanBeCanceled ? ct.Register(() => done.TrySetCanceled(ct)) : default;
            try
            {
                // 挂事件与检查之间可能已经全部跑完，这里再确认一次。
                if (tasks.All(t => !t.IsActive)) return;
                await done.Task;
            }
            finally
            {
                foreach (var task in tasks) task.PropertyChanged -= handler;
            }
        }

        private async Task DownloadAsync(DownloadTask task, string url, string? destinationDirectory)
        {
            RunOnUi(() =>
            {
                if (!task.Token.IsCancellationRequested)
                    task.Status = "下载中";
            });

            string? filePath = null;
            try
            {
                var fileName = SanitizeFileName(task.FileName);
                var dir = string.IsNullOrWhiteSpace(destinationDirectory)
                    ? GetDownloadDirectory()
                    : destinationDirectory;
                Directory.CreateDirectory(dir);
                filePath = Path.Combine(dir, fileName);
                if (File.Exists(filePath)) File.Delete(filePath);

                await HttpDownloadHelper.DownloadFileAsync(url, filePath, task.Token, (read, total) =>
                {
                    if (!ShouldReportProgress(task.Id)) return;
                    RunOnUi(() =>
                    {
                        if (task.Token.IsCancellationRequested) return;
                        task.DownloadedBytes = read;
                        if (total > 0)
                        {
                            task.TotalBytes = total;
                            task.Progress = Math.Min(100, read * 100.0 / total);
                        }
                    });
                }, status =>
                {
                    if (!ShouldReportProgress(task.Id, minMs: 250)) return;
                    RunOnUi(() =>
                    {
                        if (task.Token.IsCancellationRequested) return;
                        task.Detail = status;
                    });
                });

                RunOnUi(() =>
                {
                    if (task.Token.IsCancellationRequested) return;
                    task.Progress = 100;
                    task.Status = "已完成";
                    task.Detail = string.IsNullOrWhiteSpace(filePath)
                        ? "已保存至下载库。"
                        : "已保存至下载库，请在版本设置中安装。";
                    NotificationService.DownloadComplete(task.Title);
                });
            }
            catch (Exception ex) when (IsCanceled(ex, task.Token))
            {
                TryDeleteFile(filePath);
                MarkCancelled(task);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("Download", $"下载失败：{task.Title} / {task.FileName}", ex);
                TryDeleteFile(filePath);
                RunOnUi(() =>
                {
                    task.Status = "下载失败";
                    task.Progress = 0;
                    task.Detail = ex.Message;
                    NotificationService.DownloadFailed(task.Title, ex.Message);
                });
            }
        }

        public static bool IsCanceled(Exception ex, CancellationToken token)
            => token.IsCancellationRequested;

        private static void TryDeleteFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
            try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch (Exception) { }
            try { if (File.Exists(path + ".part")) File.Delete(path + ".part"); } catch (Exception) { }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private bool ShouldReportProgress(string taskId, int minMs = 180)
        {
            var now = DateTime.UtcNow;
            if (_lastProgressUi.TryGetValue(taskId, out var last) && now - last < TimeSpan.FromMilliseconds(minMs))
                return false;
            _lastProgressUi[taskId] = now;
            return true;
        }

        private void RunOnUi(Action action)
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null)
            {
                action();
                return;
            }
            if (dispatcher.HasThreadAccess) action();
            else dispatcher.TryEnqueue(() => action());
        }
    }
}
