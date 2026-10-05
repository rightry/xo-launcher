using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XO_Launcher.Api;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 调试页：环境、日志尾部、设置原文、元数据源连通性、缓存清理。
    /// 由 MainWindow 的「按下 20 个不同按键」解锁后才出现在导航里。
    /// </summary>
    public sealed partial class DebugPage : UserControl
    {
        private static readonly HttpClient ProbeClient = CreateProbeClient();

        // AI 首 token 可能慢，给 20 秒；和 8 秒的元数据探测分开，免得把「正常慢」判成失败
        // 120 秒而不是 20 秒：NIM 上的推理模型冷启动实测要几十秒到几分钟才吐第一个 token，
        // 20 秒会误报「AI 不通」。对话流本身用的是无限超时 + 手动取消，不受这里影响。
        private static readonly HttpClient AiProbeClient = new() { Timeout = TimeSpan.FromSeconds(120) };
        private bool _probing;

        public DebugPage()
        {
            InitializeComponent();
            var option = AiModels.Selected;
            AiTargetText.Text = "服务商 " + option.Provider + " · 模型 " + option.Id
                + " · " + AiModels.RequestUrl(option) + "（密钥取自本机设置，此页不显示）";
            Refresh();
            RefreshPages();
            ReloadLog();
            ReloadSettings();
        }

        private static HttpClient CreateProbeClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (diagnostics)");
            return client;
        }

        private void Refresh()
        {
            var settings = LauncherSettings.Instance;
            var lines = new List<string>
            {
                "启动器版本：" + LauncherLogService.LauncherVersion,
                "运行框架：" + RuntimeInformation.FrameworkDescription + " / " + RuntimeInformation.ProcessArchitecture,
                "程序目录：" + AppContext.BaseDirectory,
                "入口程序集：" + (Assembly.GetEntryAssembly()?.GetName().Name ?? "(未知)"),
                "游戏目录：" + settings.GameDirectory,
                "当前选中版本：" + (string.IsNullOrWhiteSpace(settings.SelectedVersion) ? "(未选)" : settings.SelectedVersion),
                "账号数量：" + AccountStore.Instance.Accounts.Count + "（当前：" + (AccountStore.Instance.Selected?.Username ?? "无") + "）",
                "下载镜像策略：" + settings.DownloadMirror,
                "全景背景：" + (settings.PanoramaBackground ? "开" : "关")
                    + "，浓度 " + settings.PanoramaBlurAmount.ToString("0")
                    + "，速度 " + settings.PanoramaScrollSpeed.ToString("0") + " 档",
                "引导完成：" + (settings.WelcomeDone ? "是" : "否")
                    + "，已看更新说明版本：" + (string.IsNullOrWhiteSpace(settings.SeenReleaseNotes) ? "(无)" : settings.SeenReleaseNotes),
                "日志目录：" + LauncherLogService.LogDirectory,
            };
            EnvironmentText.Text = string.Join(Environment.NewLine, lines);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
            RefreshPages();
            ReloadLog();
            ReloadSettings();
        }

        // ==================== 页面导航 ====================

        /// <summary>
        /// 列出各页面的实例化 / 显示状态并给一个直接跳转的入口。
        /// 页面行是代码拼的而不是 XAML 写死：加页面时只改 MainWindow 的清单，这里自动跟着变。
        /// </summary>
        private void RefreshPages()
        {
            PagesHost.Children.Clear();
            if (App.MainWindow is not MainWindow window)
            {
                PagesHost.Children.Add(new TextBlock
                {
                    Text = "拿不到主窗口，无法列出页面。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.Brush("TextSecondaryBrush"),
                });
                return;
            }

            foreach (var (id, name, created, current) in window.DebugPageStates())
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = new TextBlock
                {
                    Text = name,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeService.Brush("TextPrimaryBrush"),
                };
                var state = new TextBlock
                {
                    Text = current ? "正在显示" : created ? "已创建" : "未创建",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeService.Brush(current ? "AccentBrush" : "TextTertiaryBrush"),
                };
                var tag = id;
                var enter = new Button
                {
                    Content = "进入",
                    FontSize = 12,
                    Height = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    IsEnabled = !current,
                    Tag = tag,
                };
                enter.Click += (_, _) => window.DebugNavigate(tag);

                Grid.SetColumn(state, 1);
                Grid.SetColumn(enter, 2);
                row.Children.Add(title);
                row.Children.Add(state);
                row.Children.Add(enter);
                PagesHost.Children.Add(row);
            }
        }

        // ==================== 日志 / 设置原文 ====================

        private void ReloadLog()
        {
            LogPathText.Text = LauncherLogService.LogFile;
            LogTailText.Text = ReadTail(LauncherLogService.LogFile, 120);
        }

        private void ReloadSettings()
        {
            var path = Path.Combine(LauncherLogService.LogDirectory, "..", "settings.json");
            try { path = Path.GetFullPath(path); } catch (Exception) { }
            SettingsPathText.Text = path;
            SettingsText.Text = ReadAll(path, 4000);
        }

        private static string ReadTail(string path, int lines)
        {
            try
            {
                if (!File.Exists(path)) return "(文件不存在)";
                // 只读末尾 64KB：日志可能上百 MB，整文件读进来会把内存和 UI 都拖死
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = (int)Math.Min(fs.Length, 64 * 1024);
                fs.Seek(-length, SeekOrigin.End);
                using var reader = new StreamReader(fs);
                var text = reader.ReadToEnd();
                var all = text.Split('\n');
                var keep = all.Skip(Math.Max(0, all.Length - lines)).ToList();
                return string.Join(Environment.NewLine, keep).TrimEnd();
            }
            catch (Exception ex)
            {
                return "读取失败：" + ex.Message;
            }
        }

        private static string ReadAll(string path, int maxChars)
        {
            try
            {
                if (!File.Exists(path)) return "(文件不存在)";
                var text = File.ReadAllText(path);
                return text.Length > maxChars ? text[..maxChars] + Environment.NewLine + "…（已截断）" : text;
            }
            catch (Exception ex)
            {
                return "读取失败：" + ex.Message;
            }
        }

        private void ReloadLog_Click(object sender, RoutedEventArgs e) => ReloadLog();

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenPath(LauncherLogService.LogDirectory);

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
            => OpenPath(Path.Combine(LauncherLogService.LogDirectory, "..", "settings.json"));

        /// <summary>
        /// 用系统外壳打开本机路径。路径全部来自本程序自己的目录常量，不接受用户输入，
        /// 因此不存在命令注入面；UseShellExecute 也让 explorer / 默认编辑器去处理。
        /// </summary>
        private void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                CacheHintText.Text = "打开失败：" + ex.Message;
            }
        }

        // ==================== 连通性探测 ====================

        private async void Probe_Click(object sender, RoutedEventArgs e)
        {
            if (_probing) return;
            _probing = true;
            ProbeButton.IsEnabled = false;
            ProbeText.Text = "正在测试…";

            var targets = new (string Name, string Url)[]
            {
                ("版本清单 官方", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
                ("版本清单 BMCLAPI", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"),
                ("Fabric Meta 官方", "https://meta.fabricmc.net/v2/versions/game"),
                ("Fabric Meta BMCLAPI", "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/game"),
                ("Quilt Meta 官方", "https://meta.quiltmc.org/v3/versions/game"),
                ("Forge maven 元数据", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"),
                ("NeoForge maven 元数据", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"),
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var lines = await Task.Run(async () =>
            {
                var results = new List<string>();
                foreach (var (name, url) in targets)
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        using var response = await ProbeClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        var bytes = 0L;
                        if (response.Content.Headers.ContentLength is { } length) bytes = length;
                        results.Add($"{name}　{(response.IsSuccessStatusCode ? "HTTP " + (int)response.StatusCode : "HTTP " + (int)response.StatusCode)}　{watch.ElapsedMilliseconds} ms"
                            + (bytes > 0 ? $"　{bytes / 1024} KB" : ""));
                    }
                    catch (OperationCanceledException)
                    {
                        results.Add($"{name}　超时（>8 秒）");
                    }
                    catch (Exception ex)
                    {
                        results.Add($"{name}　失败：{ex.GetType().Name} {ex.Message}");
                    }
                }
                return results;
            });

            ProbeText.Text = string.Join(Environment.NewLine, lines);
            ProbeButton.IsEnabled = true;
            _probing = false;
        }

        // ==================== 缓存 ====================

        private void ClearLoaderCache_Click(object sender, RoutedEventArgs e)
        {
            LoaderMetaApi.ClearCache();
            CacheHintText.Text = "已清空加载器版本列表缓存（含全景亮度画像之外的元数据缓存）。";
        }

        private void OpenPanoramaCache_Click(object sender, RoutedEventArgs e)
            => OpenPath(PanoramaService.CacheDirectory);

        // ==================== 更新 / 引导 / AI ====================

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is not MainWindow window) return;
            CheckUpdateButton.IsEnabled = false;
            UpdateStateText.Text = "正在检查更新…";
            try
            {
                UpdateStateText.Text = await window.DebugCheckUpdateAsync();
            }
            catch (Exception ex)
            {
                UpdateStateText.Text = "检查失败：" + ex.Message;
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private void ShowOnboarding_Click(object sender, RoutedEventArgs e)
            => (App.MainWindow as MainWindow)?.DebugShowWelcomeGate(withOnboarding: true);

        private void ShowReleaseNotes_Click(object sender, RoutedEventArgs e)
            => (App.MainWindow as MainWindow)?.DebugShowWelcomeGate(withOnboarding: false);

        private async void AiProbe_Click(object sender, RoutedEventArgs e)
        {
            AiProbeButton.IsEnabled = false;
            AiProbeText.Text = "正在请求…";
            var watch = Stopwatch.StartNew();
            try
            {
                // 探的是「当前选中的那一档」：地址、Key、模型 id 全部取自 AiModels，
                // 与 AiChatService 用同一套解析 —— 直接 POST 到 BaseURL（…/v1）会 404。
                var option = AiModels.Selected;
                var url = AiModels.RequestUrl(option);
                AiTargetText.Text = "服务商 " + option.Provider + " · 模型 " + option.Id
                    + " · " + url + "（密钥取自本机设置，此页不显示）";

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", option.ApiKey);
                // max_tokens=1：只验「地址通不通、鉴权过不过」，不真让它生成内容，也几乎不耗额度
                request.Content = new StringContent(
                    "{\"model\":\"" + option.Id
                    + "\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":1,\"stream\":false}",
                    Encoding.UTF8, "application/json");

                using var response = await AiProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                var body = await response.Content.ReadAsStringAsync();
                if (body.Length > 600) body = body[..600] + "…";
                AiProbeText.Text = "POST " + url + Environment.NewLine
                    + "HTTP " + (int)response.StatusCode + " · " + watch.ElapsedMilliseconds + " ms"
                    + Environment.NewLine + body;
            }
            catch (Exception ex)
            {
                AiProbeText.Text = "失败（" + watch.ElapsedMilliseconds + " ms）："
                    + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                AiProbeButton.IsEnabled = true;
            }
        }
    }
}
