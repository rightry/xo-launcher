using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Cli
{
    /// <summary>
    /// 无窗口命令的执行体：在 UI 线程上跑（安装/下载服务依赖 DispatcherQueue 与
    /// <see cref="DownloadTask"/>），把进度打到附着进来的控制台，跑完由 App 调用 Exit()。
    /// </summary>
    internal static class CliRunner
    {
        public static async Task<int> RunHeadlessAsync(CliRequest req)
        {
            switch (req.Command)
            {
                case CliCommand.Help:
                    if (!string.IsNullOrWhiteSpace(req.UnknownCommand))
                        ConsoleInterop.WriteLine($"未知命令：{req.UnknownCommand}");
                    PrintUsage();
                    return string.IsNullOrWhiteSpace(req.UnknownCommand) ? 0 : 2;

                case CliCommand.List:
                    return RunList();

                case CliCommand.Account:
                    return RunAccount(req);

                case CliCommand.Set:
                    return RunSet(req);

                case CliCommand.Import:
                    return await RunImportAsync(req);

                case CliCommand.Install:
                    return await RunInstallAsync(req, isDownload: false);

                case CliCommand.Download:
                    return await RunDownloadAsync(req);

                default:
                    PrintUsage();
                    return 2;
            }
        }

        // ==================== list ====================

        private static int RunList()
        {
            var versions = InstalledVersionService.Scan();
            var selected = LauncherSettings.Instance.SelectedVersion;
            if (versions.Count == 0)
            {
                ConsoleInterop.WriteLine("（无已安装版本）");
                return 0;
            }
            ConsoleInterop.WriteLine($"已安装 {versions.Count} 个版本（* 为当前选中）：");
            foreach (var v in versions)
            {
                var mark = string.Equals(v.Id, selected, StringComparison.Ordinal) ? "*" : " ";
                ConsoleInterop.WriteLine($" {mark} {v.Id}  —  {v.DisplayName}  [{v.LoaderLabel}]");
            }
            return 0;
        }

        // ==================== account ====================

        private static int RunAccount(CliRequest req)
        {
            var name = req.Arg?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                var cur = AccountStore.Instance.Accounts.FirstOrDefault(a => a.Id == LauncherSettings.Instance.SelectedAccountId);
                ConsoleInterop.WriteLine("当前账号：" + (cur is null ? "（无）" : $"{cur.Username}（{cur.TypeLabel}）"));
                ConsoleInterop.WriteLine("用法: account <离线玩家名>");
                return 2;
            }
            var account = AccountStore.Instance.AddOffline(name);
            AccountStore.Instance.Select(account.Id);
            LauncherSettings.Instance.Username = name;
            ConsoleInterop.WriteLine($"已添加并选择离线账号：{name}");
            return 0;
        }

        // ==================== set ====================

        private static int RunSet(CliRequest req)
        {
            var s = LauncherSettings.Instance;
            var key = req.Arg?.Trim().ToLowerInvariant();
            var value = req.Arg2;
            if (string.IsNullOrWhiteSpace(key))
            {
                ConsoleInterop.WriteLine("可设置项: memory gamedir downloaddir java accent theme font mirror username version");
                ConsoleInterop.WriteLine($"当前: memory={s.MemoryMB} gamedir={s.GameDirectory} accent={s.AccentColor} version={s.SelectedVersion}");
                return 2;
            }
            if (value is null)
            {
                ConsoleInterop.WriteLine($"用法: set {key} <值>");
                return 2;
            }

            switch (key)
            {
                case "memory": case "mem":
                    if (!long.TryParse(value, out var mb) || mb <= 0) { ConsoleInterop.WriteLine("内存需为正整数（MB）。"); return 2; }
                    s.MemoryMB = mb; break;
                case "gamedir": case "game": s.GameDirectory = value; break;
                case "downloaddir": case "downloaddirectory": s.DownloadDirectory = value; break;
                case "java": case "javapath": s.JavaPath = value; break;
                case "accent": case "accentcolor": s.AccentColor = value; break;
                case "theme": case "apptheme": s.AppTheme = value; break;
                case "font": case "uifont": s.UiFont = value; break;
                case "mirror": case "downloadmirror": s.DownloadMirror = value; break;
                case "username": case "user": s.Username = value; break;
                case "version": case "selectedversion": s.SelectedVersion = value; break;
                default:
                    ConsoleInterop.WriteLine($"未知设置项：{key}");
                    return 2;
            }
            ConsoleInterop.WriteLine($"已设置 {key} = {value}");
            return 0;
        }

        // ==================== import ====================

        private static async Task<int> RunImportAsync(CliRequest req)
        {
            var link = req.Arg?.Trim();
            if (string.IsNullOrWhiteSpace(link))
            {
                ConsoleInterop.WriteLine("用法: import <xolauncher://分享链接>");
                return 2;
            }
            var manifest = ModpackExportService.FromShareLink(link);
            if (manifest is null)
            {
                ConsoleInterop.WriteLine("链接无效或无法解析。");
                return 2;
            }
            var label = string.IsNullOrWhiteSpace(manifest.Name) ? "整合包" : manifest.Name;
            return await InstallManifestAsync(manifest, $"导入 {label}");
        }

        // ==================== install / download ====================

        private static Task<int> RunInstallAsync(CliRequest req, bool isDownload)
        {
            var versionId = req.Arg?.Trim();
            if (string.IsNullOrWhiteSpace(versionId))
            {
                ConsoleInterop.WriteLine("用法: install <版本ID> [--loader fabric|forge|neoforge|quilt] [--loader-version X] [--name 名称]");
                return Task.FromResult(2);
            }
            var manifest = new XoPackManifest
            {
                GameVersion = versionId,
                Loader = req.GetOption("loader") is { Length: > 0 } l ? l : "vanilla",
                LoaderVersion = req.GetOption("loader-version") ?? "",
                Name = req.GetOption("name") ?? ""
            };
            return InstallManifestAsync(manifest, (isDownload ? "下载资源 " : "安装 ") + versionId);
        }

        private static async Task<int> RunDownloadAsync(CliRequest req)
        {
            var arg = req.Arg?.Trim();
            if (string.IsNullOrWhiteSpace(arg))
            {
                ConsoleInterop.WriteLine("用法: download <版本ID>  或  download <http(s) 直链>");
                return 2;
            }
            if (arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return await DownloadUrlAsync(arg);
            }
            return await RunInstallAsync(req, isDownload: true);
        }

        private static async Task<int> DownloadUrlAsync(string url)
        {
            try
            {
                var dir = DownloadService.Instance.GetDownloadDirectory();
                Directory.CreateDirectory(dir);
                string name;
                try { name = Path.GetFileName(new Uri(url).AbsolutePath); } catch { name = ""; }
                if (string.IsNullOrWhiteSpace(name)) name = "download";
                var path = Path.Combine(dir, name);

                int lastPct = -1;
                await HttpDownloadHelper.DownloadFileAsync(url, path, CancellationToken.None,
                    (read, total) =>
                    {
                        if (total <= 0) return;
                        int pct = (int)(read * 100 / total);
                        if (pct != lastPct && (pct % 10 == 0 || pct == 100))
                        {
                            lastPct = pct;
                            ConsoleInterop.WriteLine($"[{pct,3}%] {read}/{total} 字节");
                        }
                    });
                ConsoleInterop.WriteLine("已下载：" + path);
                return 0;
            }
            catch (Exception ex)
            {
                ConsoleInterop.WriteLine("下载失败：" + ex.Message);
                LauncherLogService.Error("Cli", "download 直链失败", ex);
                return 1;
            }
        }

        private static async Task<int> InstallManifestAsync(XoPackManifest manifest, string label)
        {
            var task = DownloadService.Instance.StartInstall(label, manifest.GameVersion, switchToMessages: false);
            var lastPct = -1;
            var lastStatus = "";
            PropertyChangedEventHandler onProgress = (_, e) =>
            {
                if (e.PropertyName is not (nameof(DownloadTask.Progress) or nameof(DownloadTask.Status)
                    or nameof(DownloadTask.Detail))) return;
                int pct = (int)Math.Round(task.Progress);
                bool statusChanged = !string.Equals(task.Status, lastStatus, StringComparison.Ordinal);
                if (statusChanged || (pct != lastPct && (pct % 5 == 0 || pct == 100)))
                {
                    lastPct = pct;
                    lastStatus = task.Status ?? "";
                    ConsoleInterop.WriteLine($"[{pct,3}%] {label} — {task.Status} {task.Detail}".TrimEnd());
                }
            };
            task.PropertyChanged += onProgress;
            try
            {
                var id = await ModpackInstallService.InstallFromManifestAsync(manifest, task, task.Token);
                ConsoleInterop.WriteLine($"完成：实例 {id}");
                return 0;
            }
            catch (Exception ex) when (!DownloadService.IsCanceled(ex, task.Token))
            {
                ConsoleInterop.WriteLine("失败：" + ex.Message);
                LauncherLogService.Error("Cli", label + " 失败", ex);
                return 1;
            }
            finally
            {
                task.PropertyChanged -= onProgress;
            }
        }

        // ==================== usage ====================

        private static void PrintUsage()
        {
            var exe = "XO_Launcher.exe";
            var lines = new[]
            {
                "XO Launcher 命令行",
                "",
                $"  {exe} launch [版本ID] [--user 名] [--memory MB] [--java 路径]   启动游戏（开 GUI）",
                $"  {exe} open <页面>                                             打开页面（开 GUI）",
                "        页面: home download library online ai skin settings messages",
                $"  {exe} install <版本ID> [--loader fabric|forge|neoforge|quilt] [--loader-version X] [--name 名称]",
                "        安装原版/加载器版本（无窗口，控制台进度）",
                $"  {exe} import <xolauncher://分享链接>                          导入整合包（无窗口）",
                $"  {exe} download <版本ID | http(s) 直链>                        下载版本资源或直链文件（无窗口）",
                $"  {exe} list                                                    列出已安装版本（无窗口）",
                $"  {exe} account <离线玩家名>                                    添加并选择离线账号（无窗口）",
                $"  {exe} set <键> <值>                                           修改设置（无窗口）",
                "        键: memory gamedir downloaddir java accent theme font mirror username version",
                $"  {exe} help                                                    显示本帮助",
                "",
                "不带参数运行则正常打开启动器界面。"
            };
            foreach (var l in lines) ConsoleInterop.WriteLine(l);
        }
    }
}
