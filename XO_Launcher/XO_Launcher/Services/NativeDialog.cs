using System;
using System.Runtime.InteropServices;

namespace XO_Launcher.Services
{
    internal static class NativeDialog
    {
        public static void ShowLaunchFailure(Exception? exception)
        {
            var detail = exception?.Message ?? "未知错误";
            var text =
                "启动器界面未能加载。\n\n" +
                detail + "\n\n" +
                "日志文件：\n" + LauncherLogService.LogFile;
            MessageBoxW(IntPtr.Zero, text, "XO Launcher", 0x00000010);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
