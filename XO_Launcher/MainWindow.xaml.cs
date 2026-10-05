using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.UI;

namespace XO_Launcher
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppWindow _appWindow;

        public MainWindow()
        {
            InitializeComponent();

            Title = "XO Launcher";
            _appWindow = AppWindow.GetFromWindowId(
                Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
            _appWindow.Title = "XO Launcher";
            _appWindow.Resize(new SizeInt32(1280, 800));

            // 深色主题下使用自绘标题栏
            _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            _appWindow.TitleBar.ButtonForegroundColor = Color.FromArgb(255, 230, 237, 243);
            _appWindow.TitleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 139, 148, 158);

            SetTitleBar(TitleBar);

            MinButton.Click += (_, _) => _appWindow.Minimize();
            MaxButton.Click += (_, _) => ToggleMaximize();
            CloseButton.Click += (_, _) => Close();
        }

        private void ToggleMaximize()
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Maximized)
                    presenter.Restore();
                else
                    presenter.Maximize();
            }
        }
    }
}
