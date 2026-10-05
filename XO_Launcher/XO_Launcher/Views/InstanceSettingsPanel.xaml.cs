using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.UI.Text;
using WinRT.Interop;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class InstanceSettingsPanel : UserControl
    {
        private string _versionId = "";
        private string _instanceDir = "";
        private bool _syncing;
        private string _loaderId = "vanilla";
        private string _type = "release";
        private string? _selectedIcon;
        private string _windowMode = "default";

        public event EventHandler? BackRequested;

        /// <summary>版本设置里点击「前往资源库」时触发，由主窗口切到资源库页面。</summary>
        public event EventHandler? LibraryRequested;

        public InstanceSettingsPanel()
        {
            InitializeComponent();
            // 三个内容页共用同一个跳转意图；在这里只挂一次，避免 BindContentPages 反复订阅
            ModsContent.OpenLibraryRequested += (_, _) => LibraryRequested?.Invoke(this, EventArgs.Empty);
            ShadersContent.OpenLibraryRequested += (_, _) => LibraryRequested?.Invoke(this, EventArgs.Empty);
            PacksContent.OpenLibraryRequested += (_, _) => LibraryRequested?.Invoke(this, EventArgs.Empty);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Save();
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 面板内的显式保存。此前只有「返回启动页」会落盘，用户改完设置却直接切页 / 关窗时改动会丢，
        /// 且没有任何「已保存」的反馈 —— 与「版本设置无法应用」的体感高度重合。
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Save();
                RefreshSaveState(manual: true);
            }
            catch (Exception ex)
            {
                SaveStateText.Text = "保存失败：" + ex.Message;
                LauncherLogService.Warn("Instance", "保存版本设置失败：" + ex.Message);
            }
        }

        private void RefreshSaveState(bool manual)
        {
            if (SaveStateText is null) return;
            var resolved = GameInstanceLayout.ResolveLaunch(_versionId);
            SaveStateText.Text = resolved.Independent
                ? (manual ? "已保存 · 本版本使用独立设置" : "本版本使用独立设置")
                : (manual ? "已保存 · 本版本跟随全局设置" : "本版本跟随全局设置");
        }

        public void Load(string versionId, string displayName)
        {
            _versionId = versionId;
            var global = LauncherSettings.Instance;
            _instanceDir = GameInstanceLayout.GetDirectory(global.GameDirectory, versionId);
            Directory.CreateDirectory(_instanceDir);
            if (GameInstanceLayout.UsesIsolatedContent(_instanceDir))
                GameInstanceLayout.Ensure(_instanceDir);
            var meta = GameInstanceLayout.GetOrCreateMeta(_instanceDir);

            _syncing = true;
            DisplayNameBox.Text = string.IsNullOrWhiteSpace(meta.DisplayName) ? displayName : meta.DisplayName;
            FolderIdHint.Text = "文件夹名称：" + versionId;
            IndependentToggle.IsOn = meta.IndependentSettings;
            MemorySlider.Value = meta.MemoryMB is > 0 ? meta.MemoryMB.Value : global.MemoryMB;
            UpdateMemoryLabel((int)MemorySlider.Value);
            AutoMatchToggle.IsOn = meta.AutoMatchJava ?? global.AutoMatchJava;
            JvmArgsBox.Text = meta.ExtraJvmArgs ?? "";
            GameArgsBox.Text = meta.ExtraGameArgs ?? "";

            var installed = InstalledVersionService.Find(versionId);
            _loaderId = installed?.LoaderId ?? meta.Loader ?? "vanilla";
            _type = installed?.Type ?? "release";
            _selectedIcon = VersionIconService.Exists(meta.CustomIcon)
                            || VersionIconService.IsInstanceIcon(meta.CustomIcon, _instanceDir)
                ? meta.CustomIcon
                : VersionIconService.FindInstanceIcon(_instanceDir) is { } pack
                    ? Path.GetFileName(pack)
                    : null;
            _windowMode = meta.WindowMode ?? (meta.WindowWidth is > 0 ? "custom" : "default");
            SelectWindowMode(_windowMode);
            WindowSizePanel.Visibility = _windowMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
            WidthBox.Text = meta.WindowWidth is > 0 ? meta.WindowWidth.Value.ToString() : "854";
            HeightBox.Text = meta.WindowHeight is > 0 ? meta.WindowHeight.Value.ToString() : "480";
            RefreshIconChoices();

            RefreshJavaList(meta.JavaPath ?? global.JavaPath);
            ApplyIndependentState();
            _syncing = false;

            ShowSection("basic");
            BindContentPages();
            HeaderTitle.Text = "版本设置 · " + displayName;
            ApplyBedrockLayout();
            RefreshSaveState(manual: false);
        }

        private void ApplyBedrockLayout()
        {
            var bedrock = BedrockIds.IsBedrock(_loaderId);
            var visibility = bedrock ? Visibility.Collapsed : Visibility.Visible;
            NavModsButton.Visibility = visibility;
            NavShadersButton.Visibility = visibility;
            NavPacksButton.Visibility = visibility;
            NavMemoryButton.Visibility = visibility;
            NavJvmButton.Visibility = visibility;
            // 基岩版只剩「基础」一组，一条只有单个页签的导航栏没有意义，整条收掉
            TabStrip.Visibility = bedrock ? Visibility.Collapsed : Visibility.Visible;
            BedrockHintPanel.Visibility = bedrock ? Visibility.Visible : Visibility.Collapsed;
            OpenBedrockDataButton.Visibility = BedrockIds.IsClient(_loaderId) && !BedrockIds.IsServer(_versionId)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(_versionId)) return;

            // 「使用独立设置」是内存 / Java / JVM 参数 / 窗口大小生效的**唯一开关**
            // （见 GameInstanceLayout.ResolveLaunch：未开启时一律回落全局，写进 meta 也不会被读取）。
            // 因此只要用户在本页动过这些项，就自动开启，避免「改了没反应」的静默丢弃。
            var wantsIndependent = IndependentToggle.IsOn || HasIndependentEdits();
            if (wantsIndependent && !IndependentToggle.IsOn)
            {
                _syncing = true;
                try { IndependentToggle.IsOn = true; }
                finally { _syncing = false; }
                ApplyIndependentState();
            }

            var existing = GameInstanceLayout.ReadMeta(_instanceDir);
            if (wantsIndependent)
                GameInstanceLayout.Ensure(_instanceDir);
            var meta = existing ?? new GameInstanceLayout.InstanceMeta();
            var name = DisplayNameBox.Text?.Trim();
            meta.DisplayName = string.IsNullOrWhiteSpace(name) ? null : name;
            meta.IndependentSettings = wantsIndependent;
            meta.MemoryMB = (long)MemorySlider.Value;
            meta.AutoMatchJava = AutoMatchToggle.IsOn;
            meta.JavaPath = JavaCombo.SelectedItem is JavaInstallation java ? java.Path : meta.JavaPath;
            meta.ExtraJvmArgs = JvmArgsBox.Text?.Trim();
            meta.ExtraGameArgs = GameArgsBox.Text?.Trim();
            meta.CustomIcon = VersionIconService.Exists(_selectedIcon)
                              || VersionIconService.IsInstanceIcon(_selectedIcon, _instanceDir)
                ? _selectedIcon
                : null;
            meta.WindowMode = _windowMode;
            if (_windowMode == "custom")
            {
                meta.WindowWidth = int.TryParse(WidthBox.Text, out var w) && w > 0 ? w : null;
                meta.WindowHeight = int.TryParse(HeightBox.Text, out var h) && h > 0 ? h : null;
            }
            else
            {
                meta.WindowWidth = null;
                meta.WindowHeight = null;
            }
            if (existing is not null || wantsIndependent || !string.IsNullOrWhiteSpace(meta.DisplayName)
                || meta.CustomIcon is not null)
                GameInstanceLayout.WriteMeta(_instanceDir, meta);
        }

        /// <summary>
        /// 本页是否存在「只有开启独立设置才会生效」的改动。
        /// 判据：与全局值或「独立设置的默认值」不一致。
        /// </summary>
        private bool HasIndependentEdits()
        {
            var global = LauncherSettings.Instance;

            if (Math.Abs(MemorySlider.Value - global.MemoryMB) > 0.5) return true;
            if (AutoMatchToggle.IsOn != global.AutoMatchJava) return true;

            if (JavaCombo.SelectedItem is JavaInstallation java
                && !string.Equals(java.Path, global.JavaPath, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(JvmArgsBox.Text)) return true;
            if (!string.IsNullOrWhiteSpace(GameArgsBox.Text)) return true;
            if (!string.Equals(_windowMode, "default", StringComparison.Ordinal)) return true;

            return false;
        }

        private void NavBasic_Click(object sender, RoutedEventArgs e) => ShowSection("basic");
        private void NavMods_Click(object sender, RoutedEventArgs e) => ShowSection("mods");
        private void NavShaders_Click(object sender, RoutedEventArgs e) => ShowSection("shaders");
        private void NavPacks_Click(object sender, RoutedEventArgs e) => ShowSection("packs");
        private void NavMemory_Click(object sender, RoutedEventArgs e) => ShowSection("memory");
        private void NavJvm_Click(object sender, RoutedEventArgs e) => ShowSection("jvm");

        private void BindContentPages()
        {
            var gameDir = LauncherSettings.Instance.GameDirectory;
            ModsContent.Bind(gameDir, _instanceDir, ContentKind.Mod);
            ShadersContent.Bind(gameDir, _instanceDir, ContentKind.Shader);
            PacksContent.Bind(gameDir, _instanceDir, ContentKind.ResourcePack);
        }

        private void ShowSection(string section)
        {
            BasicPage.Visibility = section == "basic" ? Visibility.Visible : Visibility.Collapsed;
            ModsPage.Visibility = section == "mods" ? Visibility.Visible : Visibility.Collapsed;
            ShadersPage.Visibility = section == "shaders" ? Visibility.Visible : Visibility.Collapsed;
            PacksPage.Visibility = section == "packs" ? Visibility.Visible : Visibility.Collapsed;
            MemoryPage.Visibility = section == "memory" ? Visibility.Visible : Visibility.Collapsed;
            JvmPage.Visibility = section == "jvm" ? Visibility.Visible : Visibility.Collapsed;
            HighlightNav(NavBasicButton, section == "basic");
            HighlightNav(NavModsButton, section == "mods");
            HighlightNav(NavShadersButton, section == "shaders");
            HighlightNav(NavPacksButton, section == "packs");
            HighlightNav(NavMemoryButton, section == "memory");
            HighlightNav(NavJvmButton, section == "jvm");
            ModsContent.HidePicker();
            ShadersContent.HidePicker();
            PacksContent.HidePicker();
            if (section == "mods") ModsContent.Refresh();
            if (section == "shaders") ShadersContent.Refresh();
            if (section == "packs") PacksContent.Refresh();
        }

        private static void HighlightNav(Button button, bool active)
        {
            // 顶栏页签的选中态：与下载页分类顶栏 / 设置页同一套（强调色 + 半粗，不填底色）
            button.Foreground = ThemeService.Brush(active ? "AccentBrush" : "GhostButtonForeground");
            button.FontWeight = active ? new FontWeight { Weight = 600 } : new FontWeight { Weight = 400 };
        }

        private void IndependentToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            ApplyIndependentState();
        }

        /// <summary>内存页 / Java 页顶部的「启用独立设置」：就地翻开那个唯一的开关。</summary>
        private void EnableIndependent_Click(object sender, RoutedEventArgs e)
        {
            IndependentToggle.IsOn = true;   // Toggled 里会走 ApplyIndependentState
            Save();
            RefreshSaveState(manual: true);
        }

        private void ApplyIndependentState()
        {
            var on = IndependentToggle.IsOn;
            MemoryHintText.Text = on
                ? "此版本将使用下方内存分配，不再跟随全局设置。"
                : "当前跟随全局内存，本页数值暂不生效。";
            JvmHintText.Text = on
                ? "此版本将使用下方 Java 与启动参数。"
                : "当前跟随全局 Java 与参数，本页数值暂不生效。";
            var enable = on ? Visibility.Collapsed : Visibility.Visible;
            MemoryEnableButton.Visibility = enable;
            JvmEnableButton.Visibility = enable;
            if (SaveStateText is not null)
                SaveStateText.Text = on ? "本版本使用独立设置" : "本版本跟随全局设置";
        }

        private void MemorySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (MemoryValueText is null) return;
            UpdateMemoryLabel((int)e.NewValue);
        }

        private void UpdateMemoryLabel(int mb)
        {
            var gb = mb / 1024.0;
            MemoryValueText.Text = $"{mb} MB（{gb:0.#} GB）";
        }

        private void WindowModeCombo_SelectionChanged(object sender, EventArgs e)
        {
            if (_syncing) return;
            _windowMode = WindowModeCombo.SelectedItem is SelectBoxItem { Tag: string tag } ? tag : "default";
            WindowSizePanel.Visibility = _windowMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SelectWindowMode(string mode)
        {
            foreach (var item in WindowModeCombo.Items)
            {
                if (item is SelectBoxItem { Tag: string tag } && tag == mode)
                {
                    WindowModeCombo.SelectedItem = item;
                    return;
                }
            }
            WindowModeCombo.SelectedIndex = 0;
        }

        /// <summary>构建图标选择行：第一项「自动」按版本类型显示，其后为可自由选择的自定义图标。</summary>
        private void RefreshIconChoices()
        {
            IconChoicesList.Items.Clear();
            AddIconChoice(null, "自动", VersionIconService.GetIconPath(null, _loaderId, _type, _instanceDir));
            var pack = VersionIconService.FindInstanceIcon(_instanceDir);
            if (pack is not null)
                AddIconChoice(Path.GetFileName(pack), "整合包", pack);
            foreach (var name in VersionIconService.GetCustomChoices())
                AddIconChoice(name, Path.GetFileNameWithoutExtension(name), VersionIconService.GetIconPath(name, _loaderId, _type));
        }

        private void AddIconChoice(string? icon, string label, string path)
        {
            var selected = string.Equals(icon, _selectedIcon, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Width = 48,
                Height = 48,
                Padding = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Tag = icon,
                BorderThickness = new Thickness(selected ? 2 : 0),
                // 选中描边跟着主题色走（原来是写死的草绿，换主题色后就对不上了）
                BorderBrush = ThemeService.Brush("AccentBrush"),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            ToolTipService.SetToolTip(button, label);
            var image = RemoteIconLoader.TryCreateFromFile(path, 42);
            button.Content = image is not null
                ? new Image { Source = image, Stretch = Stretch.UniformToFill }
                : new TextBlock { Text = label, FontSize = 9 };
            button.Click += (_, _) =>
            {
                _selectedIcon = icon;
                RefreshIconChoices();
            };
            IconChoicesList.Items.Add(button);
        }

        private void OpenBedrockData_Click(object sender, RoutedEventArgs e)
        {
            var preview = BedrockIds.IsPreviewChannel(_versionId) || _loaderId == "bedrock-preview";
            var path = BedrockEditionService.GetComMojangPath(preview);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages",
                    preview ? BedrockIds.PreviewFamily : BedrockIds.ReleaseFamily,
                    "LocalState", "games", "com.mojang");
            }
            OpenFolder(path);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;
            if (tag == "version")
            {
                OpenFolder(_instanceDir);
                return;
            }
            var contentRoot = GameInstanceLayout.ResolveContentRoot(
                LauncherSettings.Instance.GameDirectory, _instanceDir);
            OpenFolder(GameInstanceLayout.GetSubFolder(contentRoot, tag));
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 打开资源管理器失败时忽略
            }
        }

        private void RefreshJavaList(string? selectedPath)
        {
            var javas = JavaRuntimeService.ScanAll().ToList();
            if (!string.IsNullOrWhiteSpace(selectedPath) && File.Exists(selectedPath)
                && javas.TrueForAll(j => !string.Equals(j.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                javas.Insert(0, new JavaInstallation
                {
                    Path = selectedPath,
                    Major = JavaRuntimeService.QueryMajorVersion(selectedPath),
                    Vendor = "Java",
                    Source = "手动选择"
                });
            }
            JavaCombo.ItemsSource = javas;
            JavaCombo.DisplayMemberPath = nameof(JavaInstallation.Display);
            JavaCombo.SelectedItem = javas.FirstOrDefault(j =>
                string.Equals(j.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        private async void BrowseJava_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, App.MainWindow is not null
                ? WindowNative.GetWindowHandle(App.MainWindow)
                : IntPtr.Zero);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            JavaRuntimeService.InvalidateScanCache();
            RefreshJavaList(file.Path);
        }
    }
}
