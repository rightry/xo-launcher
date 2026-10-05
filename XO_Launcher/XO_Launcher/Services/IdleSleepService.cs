using System;
using Microsoft.UI.Dispatching;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 空闲检测：连续 5 分钟没有任何鼠标 / 键盘 / 窗口激活动作就进入休眠，
    /// 由调用方（MainWindow）负责真正做省内存的事；一旦有动作立刻唤醒。
    ///
    /// 只做「什么时候」，不做「做什么」——省内存的动作依赖具体页面，放这里会把它变成大杂烩。
    /// </summary>
    public sealed class IdleSleepService
    {
        private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

        /// <summary>进入休眠时触发（在 UI 线程）。</summary>
        public event Action? Slept;

        /// <summary>休眠后被用户动作唤醒时触发（在 UI 线程）。</summary>
        public event Action? Woken;

        /// <summary>休眠前问一句：正在启动游戏 / 有下载在跑时不该休眠。</summary>
        public Func<bool>? ShouldSkipSleep;

        private readonly DispatcherQueueTimer _timer;
        private DateTime _lastActiveUtc = DateTime.UtcNow;
        private bool _asleep;

        public IdleSleepService(DispatcherQueue queue)
        {
            _timer = queue.CreateTimer();
            _timer.Interval = TickInterval;
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Check();
            _timer.Start();
        }

        /// <summary>任何用户动作都调它：刷新活跃时间，休眠中则立刻唤醒。</summary>
        public void NoteActivity()
        {
            _lastActiveUtc = DateTime.UtcNow;
            if (!_asleep) return;
            _asleep = false;
            Woken?.Invoke();
        }

        private void Check()
        {
            if (_asleep) return;
            if (DateTime.UtcNow - _lastActiveUtc < IdleThreshold) return;
            if (ShouldSkipSleep?.Invoke() == true)
            {
                // 有事在跑：把计时起点推后，等下一轮再看，避免每 30 秒重复问一遍
                _lastActiveUtc = DateTime.UtcNow;
                return;
            }
            _asleep = true;
            Slept?.Invoke();
        }
    }
}
