using System;
using System.Runtime.InteropServices;

namespace XO_Launcher.Cli
{
    /// <summary>
    /// 让「窗口子系统」的启动器进程也能把命令行输出打回调用它的终端。
    ///
    /// 启动器是 GUI 子系统程序，双击启动时不该弹黑框，所以不能改成控制台子系统。
    /// 折中办法：仅当带命令行参数、需要无窗口执行时，用 AttachConsole 附着到父进程
    /// （cmd/PowerShell）的控制台上，直接复用它的 OEM 代码页输出（中文不乱码）。
    /// 没有父控制台（例如从「运行」对话框带参启动）时退而 AllocConsole 建一个。
    ///
    /// 注意：GUI 子系统程序不会让父 shell 阻塞等待，输出可能与下一个提示符交错，
    /// 这是 Windows 的固有限制；需要严格串行时可在调用端用 `start /wait`。
    /// </summary>
    internal static class ConsoleInterop
    {
        private const int AttachParentProcess = -1;
        private static bool _attached;
        private static System.Text.StringBuilder? _sink;

        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();

        public static void Attach()
        {
            if (_attached) return;
            _attached = AttachConsole(AttachParentProcess) || AllocConsole();
        }

        /// <summary>
        /// 把后续 <see cref="WriteLine"/> 重定向到缓冲区（供 AI 工具调用捕获 CliRunner 输出）。
        /// 传 null 关闭重定向。执行是串行的，用普通静态字段即可。
        /// </summary>
        public static void SetSink(System.Text.StringBuilder? sink) => _sink = sink;

        public static void WriteLine(string text = "")
        {
            if (_sink is not null) { _sink.AppendLine(text); return; }
            if (!_attached) return;
            try { Console.WriteLine(text); }
            catch { /* 无控制台可写时静默忽略 */ }
        }
    }
}
