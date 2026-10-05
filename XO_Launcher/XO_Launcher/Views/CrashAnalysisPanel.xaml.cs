using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class CrashAnalysisPanel : UserControl
    {
        private GameCrashInfo? _crash;
        private string? _instanceDir;
        private string? _versionId;

        public bool ShowActions
        {
            get => ActionsBar.Visibility == Visibility.Visible;
            set => ActionsBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        public CrashAnalysisPanel()
        {
            InitializeComponent();
        }

        public void LoadInstance(string instanceDir, string versionId)
        {
            _instanceDir = instanceDir;
            _versionId = versionId;
            SetCrash(GameCrashService.Collect(instanceDir, versionId));
        }

        public void SetCrash(GameCrashInfo crash)
        {
            _crash = crash;
            if (!string.IsNullOrWhiteSpace(crash.InstanceDirectory))
                _instanceDir = crash.InstanceDirectory;
            if (!string.IsNullOrWhiteSpace(crash.VersionId))
                _versionId = crash.VersionId;

            var analysis = CrashAnalyzer.Analyze(crash);
            KindText.Text = string.IsNullOrWhiteSpace(crash.VersionId)
                ? crash.Kind
                : crash.Kind + "  ·  " + crash.VersionId;
            HeadlineText.Text = analysis.Headline;
            ExcerptBox.Text = analysis.Excerpt;
            FindingsHost.Children.Clear();
            foreach (var finding in analysis.Findings)
                FindingsHost.Children.Add(CreateCard(finding));
        }

        public static async Task ShowDialogAsync(XamlRoot root, GameCrashInfo crash)
        {
            var panel = new CrashAnalysisPanel { Width = 640, MinHeight = 380 };
            panel.ShowActions = false;
            panel.SetCrash(crash);
            var result = await AppDialog.ShowAsync(
                root,
                "故障分析",
                "",
                primary: "导出崩溃日志",
                close: "关闭",
                body: panel,
                secondary: "AI 深度分析",
                maxWidth: 720);
            if (result == AppDialogResult.Secondary)
            {
                OpenAiForCrash(crash);
                return;
            }
            if (result != AppDialogResult.Primary)
                return;
            await LogExportHelper.PickAndExportCrashAsync(crash);
        }

        public static async Task ShowLatestDialogAsync(XamlRoot root)
        {
            var crash = GameCrashService.LastCrash ?? GameCrashService.Collect();
            await ShowDialogAsync(root, crash);
        }

        public static void OpenAiForCrash(GameCrashInfo crash)
        {
            var packet = CrashAnalyzer.BuildAssistantPacket(crash);
            if (App.MainWindow is MainWindow window)
            {
                window.OpenAiAssistant(
                    "请根据这份崩溃日志做深度分析，给出最可能的原因和具体解决步骤。",
                    "请根据下面的崩溃材料做深度分析。先给出最可能的原因，再给出在 XO Launcher 里可操作的步骤（版本设置、模组管理、Java、内存等）。如果材料不足，说明还缺哪份日志。\n\n"
                    + packet,
                    autoSend: true);
            }
        }

        private static Border CreateCard(CrashFinding finding)
        {
            var color = finding.Severity switch
            {
                "严重" => Color.FromArgb(255, 229, 72, 77),
                "警告" => Color.FromArgb(255, 230, 162, 60),
                _ => Color.FromArgb(255, 124, 189, 75)
            };
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock
            {
                Text = $"[{finding.Severity}]  {finding.Title}",
                FontSize = 13,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 },
                Foreground = new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = finding.Advice,
                FontSize = 12,
                Foreground = ThemeService.Brush("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return new Border
            {
                Background = ThemeService.Brush("ChipBackgroundBrush"),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Child = stack
            };
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_instanceDir))
                SetCrash(GameCrashService.Collect(_instanceDir, _versionId));
            else if (_crash is not null)
                SetCrash(_crash);
            StatusText.Text = "已重新分析";
        }

        private async void PickFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".txt");
                picker.FileTypeFilter.Add(".log");
                InitializeWithWindow.Initialize(picker, App.MainWindow is not null
                    ? WindowNative.GetWindowHandle(App.MainWindow)
                    : IntPtr.Zero);
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                SetCrash(CrashAnalyzer.FromLogFile(file.Path));
                StatusText.Text = "已分析：" + file.Name;
            }
            catch (Exception ex)
            {
                StatusText.Text = "无法打开文件：" + ex.Message;
            }
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_crash is null) return;
            try
            {
                var path = await LogExportHelper.PickAndExportCrashAsync(_crash);
                StatusText.Text = path is null ? "已取消导出" : "已导出：" + path;
            }
            catch (Exception ex)
            {
                StatusText.Text = "导出失败：" + ex.Message;
            }
        }
    }
}
