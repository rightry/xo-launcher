using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 启动器诊断日志：写入 %LocalAppData%\XO_Launcher\logs，并支持导出一份可分享的错误报告。
    /// </summary>
    public static class LauncherLogService
    {
        public const string LauncherVersion = "1.0.0";
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private const int TailBytes = 256 * 1024;
        private static readonly object Gate = new();
        private static int _sessionStarted;

        public static string LogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "logs");

        public static string LogFile => Path.Combine(LogDirectory, "xo-launcher.log");

        public static void Info(string source, string message) => Write("INFO", source, message, null);

        public static void Warn(string source, string message) => Write("WARN", source, message, null);

        public static void Error(string source, string message, Exception? exception = null)
            => Write("ERROR", source, message, exception);

        public static string BuildExportText()
        {
            EnsureSession();
            var settings = LauncherSettings.Instance;
            var sb = new StringBuilder();
            sb.AppendLine("===== XO Launcher 诊断日志 =====");
            sb.AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("启动器版本: " + LauncherVersion);
            sb.AppendLine("操作系统: " + Environment.OSVersion);
            sb.AppendLine("系统架构: " + RuntimeInformation.OSArchitecture);
            sb.AppendLine("进程架构: " + RuntimeInformation.ProcessArchitecture);
            sb.AppendLine("64 位进程: " + Environment.Is64BitProcess);
            sb.AppendLine(".NET: " + RuntimeInformation.FrameworkDescription);
            sb.AppendLine("机器名: " + Environment.MachineName);
            sb.AppendLine("用户: " + Environment.UserName);
            sb.AppendLine("游戏目录: " + settings.GameDirectory);
            sb.AppendLine("Java 路径: " + (string.IsNullOrWhiteSpace(settings.JavaPath) ? "(自动匹配 / 未指定)" : settings.JavaPath));
            sb.AppendLine("自动匹配 Java: " + settings.AutoMatchJava);
            sb.AppendLine("内存分配: " + settings.MemoryMB + " MB");
            sb.AppendLine("已选版本: " + settings.SelectedVersion);
            sb.AppendLine("玩家名: " + settings.Username);
            var account = AccountStore.Instance.Selected;
            sb.AppendLine("账号类型: " + account.TypeLabel);
            sb.AppendLine();

            sb.AppendLine("----- 启动器日志 -----");
            AppendFile(sb, LogFile, int.MaxValue);
            sb.AppendLine();

            sb.AppendLine("----- 最近下载任务 -----");
            AppendDownloadTasks(sb);
            sb.AppendLine();

            var instanceDir = GameInstanceLayout.GetDirectory(settings.GameDirectory, settings.SelectedVersion);
            sb.AppendLine("----- 当前游戏实例日志 (" + settings.SelectedVersion + ") -----");
            AppendFile(sb, Path.Combine(instanceDir, "logs", "latest.log"), TailBytes);
            AppendFile(sb, Path.Combine(instanceDir, "logs", "debug.log"), TailBytes);
            AppendFile(sb, Path.Combine(instanceDir, "logs", "xo-launch.log"), TailBytes);
            var crashDir = Path.Combine(instanceDir, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                var latestCrash = Directory.GetFiles(crashDir, "*.txt")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (latestCrash is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("----- 最近崩溃报告 -----");
                    AppendFile(sb, latestCrash, TailBytes);
                }
            }

            return sb.ToString();
        }

        public static void ExportTo(string destination)
        {
            var text = BuildExportText();
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Info("Log", "已导出诊断日志：" + destination);
        }

        private static void EnsureSession()
        {
            if (Interlocked.Exchange(ref _sessionStarted, 1) != 0) return;
            AppendLine("INFO", "App", $"会话开始  v{LauncherVersion}  {RuntimeInformation.FrameworkDescription}  {RuntimeInformation.ProcessArchitecture}", null);
        }

        private static void Write(string level, string source, string message, Exception? exception)
        {
            try
            {
                EnsureSession();
                AppendLine(level, source, message, exception);
            }
            catch (Exception)
            {
                // 日志写入失败不能影响启动器
            }
        }

        private static void AppendLine(string level, string source, string message, Exception? exception)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  [{source}]  {message}";
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();
                    File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                    if (exception is not null)
                        File.AppendAllText(LogFile, exception + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // 日志写入失败不能影响启动器
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(LogFile)) return;
            if (new FileInfo(LogFile).Length < MaxLogBytes) return;
            var previous = Path.Combine(LogDirectory, "xo-launcher.prev.log");
            try
            {
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(LogFile, previous);
            }
            catch (Exception)
            {
                // 轮转失败时继续追加
            }
        }

        private static void AppendFile(StringBuilder sb, string path, int maxBytes)
        {
            sb.AppendLine($"# {path}");
            if (!File.Exists(path))
            {
                sb.AppendLine("(文件不存在)");
                sb.AppendLine();
                return;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = stream.Length;
                var take = Math.Min(length, maxBytes);
                if (length > take)
                {
                    stream.Seek(-take, SeekOrigin.End);
                    sb.AppendLine($"...(仅包含末尾 {take} 字节，原文件 {length} 字节)");
                }

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                sb.AppendLine(reader.ReadToEnd().TrimEnd());
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取失败：" + ex.Message);
            }

            sb.AppendLine();
        }

        private static void AppendDownloadTasks(StringBuilder sb)
        {
            IReadOnlyList<DownloadTask> tasks;
            try { tasks = DownloadService.Instance.Tasks.ToList(); }
            catch (Exception)
            {
                sb.AppendLine("(无法读取下载任务)");
                return;
            }

            if (tasks.Count == 0)
            {
                sb.AppendLine("(本次会话暂无下载任务)");
                return;
            }

            foreach (var task in tasks.Reverse())
            {
                sb.AppendLine($"- {task.Status}  {task.Title}  /  {task.FileName}");
                if (!string.IsNullOrWhiteSpace(task.Detail))
                    sb.AppendLine("  " + task.Detail);
                if (!string.IsNullOrWhiteSpace(task.ProgressHint))
                    sb.AppendLine("  " + task.ProgressHint);
            }
        }
    }
}
