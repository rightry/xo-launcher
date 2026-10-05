using Microsoft.UI.Xaml;
using System;
using System.Linq;
using System.Threading.Tasks;
using XO_Launcher.Cli;
using XO_Launcher.Services;

namespace XO_Launcher
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>当前主窗口（供文件/文件夹选择器等场景获取窗口句柄）。</summary>
        public static Window? MainWindow { get; private set; }

        /// <summary>需要开 GUI 再执行的命令行意图（launch / open），由 MainWindow 加载后消费。</summary>
        public static CliRequest? PendingCli { get; private set; }

        public App()
        {
            InitializeComponent();
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            LauncherLogService.Info("App", "启动器进程已启动");
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var raw = Environment.GetCommandLineArgs();
            var req = CliParser.Parse(raw.Length > 1 ? raw.Skip(1).ToArray() : Array.Empty<string>());

            // 无窗口命令：附着父控制台执行，跑完直接退出，不创建任何窗口。
            if (req.IsHeadless)
            {
                _ = RunHeadlessAsync(req);
                return;
            }

            ThemeService.ApplyAccent();
            ThemeService.ApplyFont();
            _window = new MainWindow();
            MainWindow = _window;
            try { ModLibraryService.EnsureLayout(); }
            catch (Exception) { }
            if (req.IsGuiIntent) PendingCli = req;
            _window.Activate();
        }

        private async Task RunHeadlessAsync(CliRequest req)
        {
            ConsoleInterop.Attach();
            int code;
            try
            {
                code = await CliRunner.RunHeadlessAsync(req);
            }
            catch (Exception ex)
            {
                ConsoleInterop.WriteLine("错误：" + ex.Message);
                LauncherLogService.Error("Cli", "命令行执行失败", ex);
                code = 1;
            }
            Environment.ExitCode = code;
            Exit();
        }


        private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LauncherLogService.Error("App", "未处理的界面异常", e.Exception);

            // 关于「挂调试器时卡死」：
            // XAML 生成器在 App.g.i.cs 里挂了一个处理器，**先于**本方法注册：
            //     UnhandledException += (s, a) => { if (Debugger.IsAttached) Debugger.Break(); };
            // 一旦异常逃出首帧渲染前的那段代码，进程会停在断点上 ——
            // 表现为「启动器无声卡死」，既没有崩溃窗口也没有退出码。
            //
            // 注意：BCL 里**没有**解除调试器附加的 API（Debugger.DetachAll 是
            // Visual Studio 的 API，不是 .NET 的，写了会 CS0117）。真正的解法是在
            // 源头消除异常，而不是在这里补救 —— 见 ThemeService.WriteResource
            // （失败只记日志）与 MainWindow.ApplyTheme（整段 try/catch）。
            // 另可静态关闭断点：在 App.g.i.cs 所属工程定义
            // DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION。
            // 这里保持 e.Handled = true 已足以让界面在异常后继续存活。
            e.Handled = true;
        }

        private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            LauncherLogService.Error("App", "未处理的进程异常", e.ExceptionObject as Exception);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LauncherLogService.Error("App", "未观察的后台任务异常", e.Exception);
            e.SetObserved();
        }
    }
}
