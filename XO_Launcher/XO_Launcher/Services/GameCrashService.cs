using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 检测 Minecraft / JVM 崩溃，并导出崩溃报告、latest.log 与 hs_err 日志。
    /// </summary>
    public static class GameCrashService
    {
        private const int TailBytes = 384 * 1024;
        private static readonly TimeSpan WriteGrace = TimeSpan.FromSeconds(2);

        public static GameCrashInfo? LastCrash { get; private set; }

        public static HashSet<string> Snapshot(string instanceDir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumerateCrashFiles(instanceDir, includeHsErr: true))
                set.Add(path);
            return set;
        }

        public static GameCrashInfo? Detect(
            string versionId,
            string instanceDir,
            HashSet<string> before,
            int exitCode,
            DateTime launchedAtUtc)
        {
            Directory.CreateDirectory(Path.Combine(instanceDir, "crash-reports"));
            Directory.CreateDirectory(Path.Combine(instanceDir, "logs"));

            var crashReport = NewestAfter(EnumerateCrashReports(instanceDir), before, launchedAtUtc);
            var hsErr = NewestAfter(EnumerateHsErr(instanceDir), before, launchedAtUtc);
            var latestLog = Path.Combine(instanceDir, "logs", "latest.log");
            var launchLog = Path.Combine(instanceDir, "logs", "xo-launch.log");
            var logLooksCrashed = FileLooksCrashed(latestLog, launchedAtUtc);

            string? kind = null;
            if (crashReport is not null) kind = "Minecraft 崩溃";
            else if (hsErr is not null) kind = "JVM 崩溃";
            else if (logLooksCrashed) kind = "Minecraft 崩溃";
            else if (exitCode != 0) kind = DateTime.UtcNow - launchedAtUtc < TimeSpan.FromSeconds(4)
                ? "启动失败"
                : "异常退出";

            if (kind is null) return null;

            var info = new GameCrashInfo
            {
                VersionId = versionId,
                InstanceDirectory = instanceDir,
                ExitCode = exitCode,
                Kind = kind,
                CrashReportPath = crashReport,
                HsErrPath = hsErr,
                LatestLogPath = File.Exists(latestLog) ? latestLog : null,
                LaunchLogPath = File.Exists(launchLog) ? launchLog : null,
                Summary = BuildSummary(kind, exitCode, crashReport, hsErr, latestLog)
            };
            LastCrash = info;
            Archive(info);
            LauncherLogService.Error("Crash", $"{kind}  版本={versionId}  退出码={exitCode}  {info.Summary.Replace(Environment.NewLine, " / ")}");
            return info;
        }

        public static GameCrashInfo Collect(string? instanceDir = null, string? versionId = null)
        {
            var settings = LauncherSettings.Instance;
            versionId ??= settings.SelectedVersion;
            instanceDir ??= GameInstanceLayout.GetDirectory(settings.GameDirectory, versionId);
            var report = EnumerateCrashReports(instanceDir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            var hsErr = EnumerateHsErr(instanceDir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            var latestLog = Path.Combine(instanceDir, "logs", "latest.log");
            var launchLog = Path.Combine(instanceDir, "logs", "xo-launch.log");
            string kind;
            if (report is not null) kind = "Minecraft 崩溃";
            else if (hsErr is not null) kind = "JVM 崩溃";
            else if (FileLooksCrashed(latestLog, DateTime.MinValue)) kind = "Minecraft 崩溃";
            else if (File.Exists(latestLog) || File.Exists(launchLog)) kind = "日志分析";
            else kind = "暂无崩溃记录";

            return new GameCrashInfo
            {
                VersionId = string.IsNullOrWhiteSpace(versionId)
                    ? Path.GetFileName(instanceDir.TrimEnd(Path.DirectorySeparatorChar))
                    : versionId,
                InstanceDirectory = instanceDir,
                Kind = kind,
                CrashReportPath = report,
                HsErrPath = hsErr,
                LatestLogPath = File.Exists(latestLog) ? latestLog : null,
                LaunchLogPath = File.Exists(launchLog) ? launchLog : null,
                Summary = BuildSummary(kind, 0, report, hsErr, latestLog)
            };
        }

        public static GameCrashInfo? FindLatest(string? instanceDir = null)
        {
            if (LastCrash is not null) return LastCrash;
            var settings = LauncherSettings.Instance;
            instanceDir ??= GameInstanceLayout.GetDirectory(settings.GameDirectory, settings.SelectedVersion);
            var report = EnumerateCrashReports(instanceDir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            var hsErr = EnumerateHsErr(instanceDir).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            var latestLog = Path.Combine(instanceDir, "logs", "latest.log");
            if (report is null && hsErr is null && !FileLooksCrashed(latestLog, DateTime.MinValue))
                return null;

            return new GameCrashInfo
            {
                VersionId = Path.GetFileName(instanceDir.TrimEnd(Path.DirectorySeparatorChar)),
                InstanceDirectory = instanceDir,
                Kind = report is not null ? "Minecraft 崩溃" : hsErr is not null ? "JVM 崩溃" : "Minecraft 崩溃",
                CrashReportPath = report,
                HsErrPath = hsErr,
                LatestLogPath = File.Exists(latestLog) ? latestLog : null,
                LaunchLogPath = File.Exists(Path.Combine(instanceDir, "logs", "xo-launch.log"))
                    ? Path.Combine(instanceDir, "logs", "xo-launch.log")
                    : null,
                Summary = BuildSummary(
                    report is not null ? "Minecraft 崩溃" : "JVM 崩溃",
                    0, report, hsErr, latestLog)
            };
        }

        public static void ExportTo(string destination, GameCrashInfo crash)
        {
            var text = BuildExportText(crash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LauncherLogService.Info("Crash", "已导出崩溃日志：" + destination);
        }

        public static string BuildExportText(GameCrashInfo crash)
        {
            var settings = LauncherSettings.Instance;
            var sb = new StringBuilder();
            sb.AppendLine("===== XO Launcher 崩溃日志 =====");
            sb.AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("启动器版本: " + LauncherLogService.LauncherVersion);
            sb.AppendLine("崩溃类型: " + crash.Kind);
            sb.AppendLine("退出码: " + crash.ExitCode);
            sb.AppendLine("游戏版本: " + crash.VersionId);
            sb.AppendLine("实例目录: " + crash.InstanceDirectory);
            sb.AppendLine("操作系统: " + Environment.OSVersion);
            sb.AppendLine("系统架构: " + RuntimeInformation.OSArchitecture);
            sb.AppendLine("Java 路径: " + (string.IsNullOrWhiteSpace(settings.JavaPath) ? "(自动匹配 / 未指定)" : settings.JavaPath));
            sb.AppendLine("内存分配: " + settings.MemoryMB + " MB");
            sb.AppendLine();
            sb.Append(CrashAnalyzer.FormatForExport(CrashAnalyzer.Analyze(crash)));
            sb.AppendLine("----- 摘要 -----");
            sb.AppendLine(crash.Summary);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(crash.CrashReportPath))
            {
                sb.AppendLine("----- Minecraft 崩溃报告 -----");
                AppendFile(sb, crash.CrashReportPath, int.MaxValue);
            }
            if (!string.IsNullOrWhiteSpace(crash.HsErrPath))
            {
                sb.AppendLine("----- JVM hs_err 日志 -----");
                AppendFile(sb, crash.HsErrPath, TailBytes);
            }
            if (!string.IsNullOrWhiteSpace(crash.LatestLogPath))
            {
                sb.AppendLine("----- latest.log -----");
                AppendFile(sb, crash.LatestLogPath, TailBytes);
            }
            var debugLog = Path.Combine(crash.InstanceDirectory, "logs", "debug.log");
            if (File.Exists(debugLog))
            {
                sb.AppendLine("----- debug.log -----");
                AppendFile(sb, debugLog, TailBytes);
            }
            if (!string.IsNullOrWhiteSpace(crash.LaunchLogPath))
            {
                sb.AppendLine("----- xo-launch.log -----");
                AppendFile(sb, crash.LaunchLogPath, TailBytes);
            }

            sb.AppendLine("----- 启动器日志 -----");
            AppendFile(sb, LauncherLogService.LogFile, 128 * 1024);
            return sb.ToString();
        }

        private static void Archive(GameCrashInfo crash)
        {
            try
            {
                var dir = Path.Combine(LauncherLogService.LogDirectory, "crashes");
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                CopyIfExists(crash.CrashReportPath, Path.Combine(dir, stamp + "-crash-report.txt"));
                CopyIfExists(crash.HsErrPath, Path.Combine(dir, stamp + "-hs_err.log"));
                if (!string.IsNullOrWhiteSpace(crash.LatestLogPath) && File.Exists(crash.LatestLogPath))
                {
                    var dest = Path.Combine(dir, stamp + "-latest.log");
                    File.Copy(crash.LatestLogPath, dest, overwrite: true);
                }
            }
            catch (Exception)
            {
                // 归档失败不影响崩溃提示
            }
        }

        private static void CopyIfExists(string? source, string dest)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
            File.Copy(source, dest, overwrite: true);
        }

        private static IEnumerable<string> EnumerateCrashFiles(string instanceDir, bool includeHsErr)
        {
            foreach (var path in EnumerateCrashReports(instanceDir))
                yield return path;
            if (includeHsErr)
            {
                foreach (var path in EnumerateHsErr(instanceDir))
                    yield return path;
            }
        }

        private static IEnumerable<string> EnumerateCrashReports(string instanceDir)
        {
            var dir = Path.Combine(instanceDir, "crash-reports");
            if (!Directory.Exists(dir)) yield break;
            IEnumerable<string> files;
            try { files = Directory.GetFiles(dir, "*.txt"); }
            catch (Exception) { yield break; }
            foreach (var file in files) yield return file;
        }

        private static IEnumerable<string> EnumerateHsErr(string instanceDir)
        {
            var roots = new[]
            {
                instanceDir,
                Path.GetDirectoryName(instanceDir.TrimEnd(Path.DirectorySeparatorChar)) ?? instanceDir,
                Environment.CurrentDirectory
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                string[] files;
                try { files = Directory.GetFiles(root, "hs_err_pid*.log", SearchOption.TopDirectoryOnly); }
                catch (Exception) { continue; }
                foreach (var file in files)
                {
                    if (seen.Add(file)) yield return file;
                }
            }
        }

        private static string? NewestAfter(IEnumerable<string> files, HashSet<string> before, DateTime launchedAtUtc)
        {
            var threshold = launchedAtUtc - WriteGrace;
            return files
                .Where(path =>
                {
                    try
                    {
                        if (!before.Contains(path)) return true;
                        return File.GetLastWriteTimeUtc(path) >= threshold;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static bool FileLooksCrashed(string latestLog, DateTime launchedAtUtc)
        {
            if (!File.Exists(latestLog)) return false;
            try
            {
                if (launchedAtUtc != DateTime.MinValue && File.GetLastWriteTimeUtc(latestLog) < launchedAtUtc - WriteGrace)
                    return false;
                using var stream = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var take = (int)Math.Min(stream.Length, TailBytes);
                if (stream.Length > take) stream.Seek(-take, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                return text.Contains("---- Minecraft Crash Report ----", StringComparison.Ordinal)
                       || text.Contains("Encountered an unexpected exception", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("Fatal errors were detected", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string BuildSummary(string kind, int exitCode, string? crashReport, string? hsErr, string latestLog)
        {
            var lines = new List<string> { kind + (exitCode != 0 ? $"（退出码 {exitCode}）" : "") };
            var fromReport = ReadCrashHead(crashReport);
            if (!string.IsNullOrWhiteSpace(fromReport))
                lines.Add(fromReport);
            else if (!string.IsNullOrWhiteSpace(hsErr))
                lines.Add("检测到 Java 致命错误日志：" + Path.GetFileName(hsErr));
            else if (FileLooksCrashed(latestLog, DateTime.MinValue))
                lines.Add("latest.log 中包含崩溃记录。");
            else if (kind == "启动失败")
                lines.Add("游戏进程很快退出。常见原因是内存分配过大，或 Java 版本与游戏不匹配。");
            return string.Join(Environment.NewLine, lines);
        }

        private static string? ReadCrashHead(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var useful = new List<string>();
                foreach (var raw in File.ReadLines(path).Take(80))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("----", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("Time:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Exception", StringComparison.Ordinal)
                        || line.StartsWith("java.", StringComparison.Ordinal)
                        || line.StartsWith("at ", StringComparison.Ordinal))
                    {
                        useful.Add(line);
                        if (useful.Count >= 8) break;
                    }
                }
                return useful.Count == 0 ? Path.GetFileName(path) : string.Join(Environment.NewLine, useful);
            }
            catch (Exception)
            {
                return Path.GetFileName(path);
            }
        }

        private static void AppendFile(StringBuilder sb, string path, int maxBytes)
        {
            sb.AppendLine("# " + path);
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
    }
}
