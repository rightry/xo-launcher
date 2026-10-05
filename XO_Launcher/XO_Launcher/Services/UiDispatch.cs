using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 把界面更新固定派发到创建窗口时的 UI 线程。
    /// WinUI 在后台线程改控件时不会抛托管异常，而是直接 0xC000027B。
    /// </summary>
    public static class UiDispatch
    {
        public static DispatcherQueue? Queue { get; private set; }

        public static void Bind(DispatcherQueue queue) => Queue = queue;

        public static void Run(Action action)
        {
            var queue = Queue ?? DispatcherQueue.GetForCurrentThread();
            if (queue is null || queue.HasThreadAccess)
            {
                action();
                return;
            }

            queue.TryEnqueue(() => action());
        }

        public static Task RunAsync(Action action)
        {
            var queue = Queue ?? DispatcherQueue.GetForCurrentThread();
            if (queue is null || queue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource();
            if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("无法切换到界面线程。"));
            }

            return tcs.Task;
        }
    }
}
