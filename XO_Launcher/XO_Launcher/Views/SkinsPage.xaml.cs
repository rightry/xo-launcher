using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Input;
using Windows.Storage.Pickers;
// Microsoft.UI.Xaml.Shapes 里也有一个 Path（矢量图形），与 System.IO.Path 冲突（CS0104），这里显式取后者。
using Path = System.IO.Path;
using Windows.UI;
using WinRT.Interop;
using XO_Launcher.Models;
using XO_Launcher.Services;
// Shift 修飾鍵走 InputKeyboardSource（見 IsKeyDown），不用 PointerPoint.KeyModifiers，
// 否則又要整包 using Windows.System 而撞 DispatcherQueueTimer 的 CS0104。
using VirtualKey = Windows.System.VirtualKey;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 独立皮肤页：左侧像素画布，右侧账号 / 来源 / 预览。
    /// 重做后的分层：
    /// <see cref="SkinDocument"/>（像素 + 历史 + 脏标记，唯一真相）→
    /// <see cref="SkinBitmapRenderer"/>（像素 → WriteableBitmap，唯一的预乘 / 放大 / 合成知识）→
    /// 本页只剩输入（指针 / 快捷键）与控件同步，不再手写任何位图循环。
    /// 画布只保存「当前编辑中的像素」，必须点「保存为当前账号的皮肤」才会落盘。
    /// </summary>
    public sealed partial class SkinsPage : UserControl
    {
        private enum SkinTool { Pencil, Eraser, Line, Rect, RectFill, Bucket, Darken, Lighten, Picker }

        private readonly SkinDocument _doc = new(100);

        private bool _ready;
        private bool _syncing;
        private bool _viewerBoot;

        private SkinTool _tool = SkinTool.Pencil;
        private SkinRegion _layer = SkinRegion.Base;
        private Color _color = Color.FromArgb(0xFF, 0x7C, 0xBD, 0x4B);
        private int _brushSize = 1;
        private int _zoom = DefaultZoom;
        private bool _showGrid = true;
        private bool _mirror = true;
        private bool _ghostOther = true;

        /// <summary>缩放档位阶梯，必须与 ZoomCombo 的 Tag 一一对应。</summary>
        private static readonly int[] ZoomLadder = { 2, 4, 6, 8, 12, 16 };
        private const int DefaultZoom = 8;

        // 主键盘的 +− 与 [] 是 OEM 键位，Windows.System.VirtualKey 没为它们命名，按虚拟键码取。
        // 小键盘的 +− 是 VirtualKey.Add / Subtract，两条都认。
        private const VirtualKey KeyPlus = (VirtualKey)0xBB;   // = / +
        private const VirtualKey KeyMinus = (VirtualKey)0xBD;  // - / _
        private const VirtualKey KeyLBracket = (VirtualKey)0xDB;
        private const VirtualKey KeyRBracket = (VirtualKey)0xDD;

        /// <summary>当前编辑的身体部位；null = 整张贴图（自由模式）。默认进「头」。</summary>
        private SkinPart? _part = SkinPart.Head;

        private DispatcherQueueTimer? _previewTimer;
        private bool _pushingPreview;

        private bool _painting;
        private bool _shaping;
        private bool _strokeErase;
        private bool _strokeShift;
        private int _lastX, _lastY;
        private int _anchorX, _anchorY;

        /// <summary>调色板：固定的语义色板（不受主题影响），20 色排成 10×2。</summary>
        private static readonly Color[] Palette =
        {
            Color.FromArgb(0xFF, 0x00, 0x00, 0x00), Color.FromArgb(0xFF, 0x3F, 0x3F, 0x3F),
            Color.FromArgb(0xFF, 0x7F, 0x7F, 0x7F), Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF),
            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0x56, 0x2A, 0x1B),
            Color.FromArgb(0xFF, 0x8B, 0x5A, 0x2B), Color.FromArgb(0xFF, 0xC6, 0x8E, 0x5A),
            Color.FromArgb(0xFF, 0xF1, 0xC4, 0x9C), Color.FromArgb(0xFF, 0xD8, 0x9B, 0x6B),
            Color.FromArgb(0xFF, 0x9C, 0x5B, 0x38), Color.FromArgb(0xFF, 0x8B, 0xC3, 0x4A),
            Color.FromArgb(0xFF, 0x3F, 0x7A, 0x2C), Color.FromArgb(0xFF, 0x2E, 0x62, 0xA8),
            Color.FromArgb(0xFF, 0x1B, 0x3D, 0x6B), Color.FromArgb(0xFF, 0xE0, 0x3C, 0x3C),
            Color.FromArgb(0xFF, 0x8B, 0x1F, 0x1F), Color.FromArgb(0xFF, 0xF2, 0xC4, 0x2D),
            Color.FromArgb(0xFF, 0xE2, 0x7A, 0x2E), Color.FromArgb(0xFF, 0x8E, 0x44, 0xAD),
        };

        public SkinsPage()
        {
            InitializeComponent();
            _doc.Changed += () => DispatcherQueue.TryEnqueue(() =>
            {
                UndoButton.IsEnabled = _doc.CanUndo;
                RedoButton.IsEnabled = _doc.CanRedo;
            });
            Loaded += OnPageLoaded;
            AccountStore.Instance.Changed += OnAccountsChanged;
        }

        /// <summary>由 MainWindow.ShowPage("skin") 调用。首次进入时装配画布，之后只刷新账号栏。</summary>
        /// <summary>休眠时压低 3D 预览（WebView2）的内存目标；唤醒后恢复。</summary>
        public void SetMemoryTarget(bool low) => SkinViewer.SetMemoryTarget(low);

        public void EnsureReady()
        {
            if (_ready)
            {
                RefreshAccountChrome();
                return;
            }

            _ready = true;
            CanvasSurface.IsTabStop = true;
            // ScrollViewer 自己会把手柄滚轮事件标记为已处理（那是它的滚动语义），
            // 所以 Ctrl+滚轮必须用 AddHandler(handledEventsToo) 才收得到。
            CanvasScroll.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(Canvas_PointerWheelChanged),
                true);
            BuildPalette();
            SyncControls();
            ApplyZoom();
            RenderAll();
            RefreshAccountChrome();
            _ = LoadCanvasFromAccountAsync();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_viewerBoot) return;
            _viewerBoot = true;
            if (_ready) PushPreview();
            else _ = RefreshViewerAsync(AccountStore.Instance.Selected);
        }

        private void OnAccountsChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshAccountChrome();
                if (!_doc.IsDirty)
                {
                    _ = LoadCanvasFromAccountAsync();
                    _ = RefreshViewerAsync(AccountStore.Instance.Selected);
                }
            });
        }

        // ==================================================================
        // 画布装载 / 保存
        // ==================================================================

        private async Task LoadCanvasFromAccountAsync()
        {
            var account = AccountStore.Instance.Selected;
            byte[]? pixels = null;

            if (account is not null)
            {
                try
                {
                    var path = await SkinAvatarService.GetFullSkinPathAsync(account);
                    if (!string.IsNullOrEmpty(path))
                        pixels = await SkinPngCodec.DecodeAsync(path);
                }
                catch (Exception ex)
                {
                    LauncherLogService.Warn("Skin", "读取当前皮肤失败：" + ex.Message);
                }
            }

            if (pixels is null)
            {
                var slim = account?.IsSlimModel ?? false;
                var fallback = Path.Combine(AppContext.BaseDirectory, "Assets", "skin-default",
                    slim ? "alex.png" : "steve.png");
                if (File.Exists(fallback))
                {
                    try { pixels = await SkinPngCodec.DecodeAsync(fallback); }
                    catch (Exception) { }
                }
                pixels ??= SkinTemplateFactory.Create(slim);
            }

            _doc.LoadPixels(pixels);
            _doc.IsSlim = account?.IsSlimModel ?? false;

            RenderAll();
            PushPreview();
            EditorStatusText.Text = account is null
                ? "当前没有选中账号。可以直接在上面画，或者载入一个起步模板。"
                : "已载入「" + account.Username + "」当前的皮肤。";
        }

        private async void SaveToAccount_Click(object sender, RoutedEventArgs e)
        {
            var account = AccountStore.Instance.Selected;
            if (account is null)
            {
                SaveStatusText.Text = "请先在设置页添加并选择一个账号。";
                return;
            }
            if (string.IsNullOrWhiteSpace(account.Uuid))
            {
                SaveStatusText.Text = "该账号没有 UUID，无法保存本地皮肤。";
                return;
            }

            SaveToAccountButton.IsEnabled = false;
            SaveStatusText.Text = "正在保存…";
            var temp = Path.Combine(Path.GetTempPath(), "xo-skin-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var variant = _doc.IsSlim ? GameAccount.SkinVariantSlim : GameAccount.SkinVariantClassic;
                await SkinPngCodec.EncodeAsync(temp, _doc.Pixels);
                var saved = await SkinAvatarService.SetLocalSkinAsync(account.Uuid, temp);

                account.LocalSkinPath = saved;
                account.SkinSource = GameAccount.SkinSourceLocal;
                account.SkinVariant = variant;
                AccountStore.Instance.Persist();

                _doc.MarkSaved();
                RefreshAccountChrome();
                await RefreshViewerAsync(account);

                // 微软正版账号：本地 PNG 不会被游戏读取，必须真正上传到 Mojang 才会在游戏内生效。
                if (account.IsMicrosoft)
                {
                    SaveStatusText.Text = "已保存到本地，正在上传到 Mojang…";
                    var uploaded = await MicrosoftAuthService.UploadSkinAsync(
                        account, temp, variant, null, CancellationToken.None);
                    // 上传成功后 Mojang 侧成为真实来源，切回 mojang 以便游戏内直接使用。
                    uploaded.SkinSource = GameAccount.SkinSourceMojang;
                    uploaded.SkinVariant = variant;
                    uploaded.LocalSkinPath = saved;
                    AccountStore.Instance.AddOrReplaceMicrosoft(uploaded);
                    // 清掉远端缓存，但预览直接用刚保存的本地文件：
                    // Mojang 的 CDN 有缓存延迟，立刻按 URL 拉可能还是旧皮肤。
                    SkinAvatarService.InvalidateRemote(uploaded.Uuid);
                    _doc.LoadPixels(await SkinPngCodec.DecodeAsync(saved) ?? _doc.Pixels);
                    RefreshAccountChrome();
                    RenderAll();
                    PushPreview();
                    await SkinViewer.ShowSkinAsync(saved, _doc.IsSlim);
                    SaveStatusText.Text = "已保存并上传到 Mojang，进入游戏即为该皮肤。";
                    return;
                }

                SaveStatusText.Text = "已保存到该账号：" + saved
                    + "（离线账号无官方皮肤服务器，此皮肤仅在本启动器内预览）";
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = "保存失败：" + ex.Message;
                LauncherLogService.Warn("Skin", "保存自制皮肤失败：" + ex.Message);
            }
            finally
            {
                SaveToAccountButton.IsEnabled = true;
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            }
        }

        private async void ImportPng_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add(".png");
                InitializeWithWindow.Initialize(picker, WindowHandle());
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;

                var pixels = await SkinPngCodec.DecodeAsync(file.Path);
                if (pixels is null)
                {
                    EditorStatusText.Text = "只支持 64×64 或 64×32 的 PNG 皮肤，这张图尺寸不符。";
                    return;
                }
                _doc.BeginStroke();
                _doc.Canvas.LoadPixels(pixels);
                _doc.MarkDirty();
                RenderAll();
                PushPreview();
                EditorStatusText.Text = "已导入 " + file.Name + "。";
            }
            catch (Exception ex)
            {
                EditorStatusText.Text = "导入失败：" + ex.Message;
                LauncherLogService.Warn("Skin", "导入皮肤失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 导出游戏实际使用的 64×64 皮肤文件。
        /// 整段都包在 try 里：弹文件框、取窗口句柄、写文件都可能抛异常，
        /// 而 async void 里抛出的异常没有调用方接得住 —— 那就是应用级失败。
        /// </summary>
        private async void ExportPng_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
                picker.SuggestedFileName = "skin-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                InitializeWithWindow.Initialize(picker, WindowHandle());
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;

                var bytes = await SkinPngCodec.EncodeToBytesAsync(_doc.Pixels);
                // 必须写 StorageFile 本身，不能按 file.Path 写：选择器可能仍持有句柄，
                // 覆盖已存在的同名文件时按路径写会撞「文件正由另一进程使用」。
                await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
                EditorStatusText.Text = "已导出皮肤文件：" + file.Path + "（右侧 3D 预览就是它在游戏里的样子）";
            }
            catch (Exception ex)
            {
                EditorStatusText.Text = "导出失败：" + ex.Message;
                LauncherLogService.Warn("Skin", "导出皮肤文件失败：" + ex.Message);
            }
        }

        /// <summary>导出一张「人形立绘」：把皮肤合成成正面站姿，用来直观确认自己画的是什么。</summary>
        private async void ExportSheet_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                const int scale = 8;
                var (w, h) = SkinPreviewRenderer.FrontSheetSize(scale);

                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
                picker.SuggestedFileName = "skin-preview-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                InitializeWithWindow.Initialize(picker, WindowHandle());
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;

                var sheet = SkinPreviewRenderer.RenderFrontSheet(_doc.Pixels, _doc.IsSlim, scale);
                var bytes = await SkinPngCodec.EncodeToBytesAsync(sheet, w, h);
                await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
                EditorStatusText.Text = $"已导出人形预览图（{w}×{h}）：{file.Path}";
            }
            catch (Exception ex)
            {
                EditorStatusText.Text = "导出预览图失败：" + ex.Message;
                LauncherLogService.Warn("Skin", "导出预览图失败：" + ex.Message);
            }
        }

        private void ClearLayer_Click(object sender, RoutedEventArgs e)
        {
            _doc.ClearLayer(_layer);
            RenderAll();
            PushPreview();
            EditorStatusText.Text = _layer == SkinRegion.Overlay ? "已清空覆盖层。" : "已清空基础层。";
        }

        /// <summary>
        /// 起步模板：一键得到一张完整、UV 正确的人形皮肤；「空白画布」则清成全透明。
        /// 走代码生成（<see cref="SkinTemplateFactory"/>）而不是读 Assets 里的 PNG。
        /// 这里刻意绕开 <see cref="SkinDocument.LoadPixels"/>（它会 Reset 历史），
        /// 改为 BeginStroke + 直接 Load，保持「载入模板」本身可撤销。
        /// </summary>
        private void Template_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;

            try
            {
                _doc.BeginStroke();

                if (string.Equals(tag, "blank", StringComparison.Ordinal))
                {
                    _doc.Canvas.LoadPixels(new byte[SkinCanvas.ByteCount]);
                    _doc.MarkDirty();
                    RenderAll();
                    PushPreview();
                    EditorStatusText.Text = "画布已清空为全透明，可以从零开始画。";
                    return;
                }

                bool slim = string.Equals(tag, "alex", StringComparison.Ordinal);
                _doc.Canvas.LoadPixels(SkinTemplateFactory.Create(slim));
                _doc.IsSlim = slim;
                _doc.MarkDirty();

                // 载入 Alex 模板就是按纤细模型在画，账号的模型标记要跟着走 ——
                // 否则右栏 chip 显示「经典」而画布是纤细，两边对不上。
                var owner = AccountStore.Instance.Selected;
                if (owner is not null)
                {
                    owner.SkinVariant = slim ? GameAccount.SkinVariantSlim : GameAccount.SkinVariantClassic;
                    AccountStore.Instance.Persist();
                    RefreshSkinControls(owner);
                }

                RenderAll();
                PushPreview();
                EditorStatusText.Text = "已载入 " + (slim ? "Alex" : "Steve") + " 模板，直接在它上面改就行。";
            }
            catch (Exception ex)
            {
                EditorStatusText.Text = "载入模板失败：" + ex.Message;
                LauncherLogService.Warn("Skin", "载入模板失败：" + ex.Message);
            }
        }

        private static IntPtr WindowHandle() =>
            App.MainWindow is not null ? WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;

        // ==================================================================
        // 工具 / 颜色 / 图层
        // ==================================================================

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;
            SetTool(tag);
        }

        private void SetTool(string tag)
        {
            _tool = tag switch
            {
                "eraser" => SkinTool.Eraser,
                "line" => SkinTool.Line,
                "rect" => SkinTool.Rect,
                "rectfill" => SkinTool.RectFill,
                "bucket" => SkinTool.Bucket,
                "darken" => SkinTool.Darken,
                "lighten" => SkinTool.Lighten,
                "picker" => SkinTool.Picker,
                _ => SkinTool.Pencil
            };
            UpdateToolButtons();
            EditorStatusText.Text = _tool switch
            {
                SkinTool.Eraser => "橡皮：擦掉当前层像素（也可随时按住右键擦）。",
                SkinTool.Line => "直线：按住拖出，松开落笔；按住 Shift 强制水平 / 垂直。",
                SkinTool.Rect => "矩形：按住拖出空心矩形，松开落笔。",
                SkinTool.RectFill => "实心矩形：按住拖出填满的矩形，松开落笔。",
                SkinTool.Bucket => "油漆桶：只填充落点所在的单个面，不会溢出到相邻部位。",
                SkinTool.Darken => "加深：沿线压暗已有像素（不改透明度），适合画阴影。",
                SkinTool.Lighten => "变亮：沿线提亮已有像素（不改透明度），适合画高光。",
                SkinTool.Picker => "取色器：点一下画布，把该点颜色取为当前色。",
                _ => "铅笔：按住拖动连续落笔（B）。",
            };
        }

        private void UpdateToolButtons()
        {
            PaintToggle(ToolPencilButton, _tool == SkinTool.Pencil);
            PaintToggle(ToolEraserButton, _tool == SkinTool.Eraser);
            PaintToggle(ToolLineButton, _tool == SkinTool.Line);
            PaintToggle(ToolRectButton, _tool == SkinTool.Rect);
            PaintToggle(ToolRectFillButton, _tool == SkinTool.RectFill);
            PaintToggle(ToolBucketButton, _tool == SkinTool.Bucket);
            PaintToggle(ToolDarkenButton, _tool == SkinTool.Darken);
            PaintToggle(ToolLightenButton, _tool == SkinTool.Lighten);
            PaintToggle(ToolPickerButton, _tool == SkinTool.Picker);
        }

        private static void PaintToggle(Button button, bool active)
        {
            button.Background = ThemeService.Brush(active ? "AccentBrush" : "NeutralButtonBrush");
            button.Foreground = ThemeService.Brush(active ? "AccentOnBrush" : "NeutralButtonForegroundBrush");
        }

        /// <summary>切换编辑部位。Tag："head"/"body"/"rightarm"/"leftarm"/"rightleg"/"leftleg"/"whole"。</summary>
        private void Part_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag }) return;

            _part = tag switch
            {
                "whole" => (SkinPart?)null,
                "body" => SkinPart.Body,
                "rightarm" => SkinPart.RightArm,
                "leftarm" => SkinPart.LeftArm,
                "rightleg" => SkinPart.RightLeg,
                "leftleg" => SkinPart.LeftLeg,
                _ => SkinPart.Head,
            };

            UpdatePartButtons();
            ApplyZoom();
            EditorStatusText.Text = _part is SkinPart p
                ? $"现在只显示「{SkinPartLayout.DisplayName(p)}」，摊成纸模的样子。画完会自动拼回整张皮肤。"
                : "现在是整张贴图模式（原样的 64×64 UV 布局，适合抠细节或对齐）。";
        }

        private void UpdatePartButtons()
        {
            PaintToggle(PartHeadButton, _part == SkinPart.Head);
            PaintToggle(PartBodyButton, _part == SkinPart.Body);
            PaintToggle(PartRightArmButton, _part == SkinPart.RightArm);
            PaintToggle(PartLeftArmButton, _part == SkinPart.LeftArm);
            PaintToggle(PartRightLegButton, _part == SkinPart.RightLeg);
            PaintToggle(PartLeftLegButton, _part == SkinPart.LeftLeg);
            PaintToggle(PartWholeButton, _part is null);
            UpdateScopeText();
        }

        private void UpdateScopeText()
        {
            if (EditorScopeText is null) return;

            var partName = _part is SkinPart p ? SkinPartLayout.DisplayName(p) : "整张贴图";
            var layerName = _layer == SkinRegion.Overlay ? "覆盖层" : "基础层";
            EditorScopeText.Text = "正在编辑：" + partName + " · " + layerName
                + (_part is null ? "" : "（画布已把两层合成显示，你画在选中的这一层上）")
                + (_doc.IsDirty ? " · 有未保存改动" : "");

            GhostOtherToggle.IsEnabled = _part is null;
        }

        private void BuildPalette()
        {
            PaletteHost.Children.Clear();
            PaletteHost.RowDefinitions.Clear();
            PaletteHost.ColumnDefinitions.Clear();

            // 色板搬进 216px 的左工具栏后放不下原来的 10 列（10×26=260），改 6 列；
            // 行数按色数算出来，别写死 2 —— 加色时不会漏行。
            const int cols = 6;
            var rows = (Palette.Length + cols - 1) / cols;
            for (int c = 0; c < cols; c++)
                PaletteHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < rows; r++)
                PaletteHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < Palette.Length; i++)
            {
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ThemeService.Brush("DividerBrush"),
                    Background = new SolidColorBrush(Palette[i]),
                    Margin = new Thickness(0, 0, 4, 4),
                    Tag = Palette[i]
                };
                swatch.Tapped += Swatch_Tapped;
                Grid.SetColumn(swatch, i % cols);
                Grid.SetRow(swatch, i / cols);
                PaletteHost.Children.Add(swatch);
            }
        }

        private void Swatch_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Color color }) SetColor(color);
        }

        private async void CustomColor_Click(object sender, RoutedEventArgs e)
        {
            var body = new ColorPickBody(_color);
            var result = await AppDialog.ShowAsync(
                XamlRoot,
                "自定义颜色",
                "",
                primary: "确定",
                close: "取消",
                body: body);
            if (result == AppDialogResult.Primary)
                SetColor(body.Color);
        }

        private void SetColor(Color color)
        {
            _color = color;
            ColorSwatch.Background = new SolidColorBrush(color);
        }

        private void Layer_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _layer = OverlayLayerRadio.IsChecked == true ? SkinRegion.Overlay : SkinRegion.Base;
            UpdateScopeText();
            RenderAll();
            EditorStatusText.Text = _layer == SkinRegion.Overlay
                ? "正在编辑覆盖层（帽子 / 夹克 / 袖子 / 裤腿）。"
                : "正在编辑基础层（身体本体）。";
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (!_doc.Undo()) return;
            RenderAll();
            PushPreview();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (!_doc.Redo()) return;
            RenderAll();
            PushPreview();
        }

        private bool _railFolded;

        /// <summary>
        /// 右栏竖向折叠：把整栏内容沿竖向折起来，只留「账号与预览」标题行，
        /// 栏宽保持 372 不动 —— 之前顺带收成 172 那是横向变窄，不是折叠。
        /// 只切 Visibility，SkinViewer（WebView2）一直活着，展开后预览还在。
        /// </summary>
        private void Rail_Toggle_Click(object sender, RoutedEventArgs e)
        {
            _railFolded = !_railFolded;
            RailBody.Visibility = _railFolded ? Visibility.Collapsed : Visibility.Visible;
            // chevron 朝上 = 展开、朝下 = 已折叠（Fluent 码点 F2B6 / F2A3）
            RailFoldChevron.Glyph = _railFolded ? "\uF2A3" : "\uF2B6";
        }

        /// <summary>
        /// 右侧两张卡（保存到账号 / 预览）标题行的折叠。
            // chevron 朝上 = 展开、朝下 = 已折叠（Fluent 码点 F2B6 / F2A3）
        /// </summary>
        private void Section_Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string key }) return;
            var (body, chevron) = key switch
            {
                "save" => (SaveSectionBody, SaveSectionChevron),
                _ => (PreviewSectionBody, PreviewSectionChevron),
            };
            bool collapse = body.Visibility == Visibility.Visible;
            body.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            chevron.Glyph = collapse ? "" : "";
        }

        /// <summary>
        /// 页面级快捷键。挂在根 Grid 的 PreviewKeyDown（tunneling）上，
        /// 这样焦点在画布、工具条还是图层单选框上都能用；原来的 Canvas_KeyDown
        /// 只在画布拿到焦点时生效，点一下工具按钮之后快捷键就断了。
        /// </summary>
        private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // 正在往输入框（皮肤 URL 等）打字时不抢键
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;

            bool ctrl = IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.LeftControl) || IsKeyDown(VirtualKey.RightControl);

            bool zoomIn = e.Key == VirtualKey.Add || e.Key == KeyPlus;

            if (ctrl)
            {
                switch (e.Key)
                {
                    case VirtualKey.Z: Undo_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case VirtualKey.Y: Redo_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case VirtualKey.S: SaveToAccount_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case VirtualKey.Number0: ResetZoom(); e.Handled = true; return;
                    case VirtualKey.Add:
                    case VirtualKey.Subtract:
                    case KeyPlus:
                    case KeyMinus:
                        StepZoom(zoomIn ? 1 : -1);
                        e.Handled = true;
                        return;
                }
                return;
            }

            switch (e.Key)
            {
                case VirtualKey.B: SetTool("pencil"); e.Handled = true; return;
                case VirtualKey.E: SetTool("eraser"); e.Handled = true; return;
                case VirtualKey.G: SetTool("bucket"); e.Handled = true; return;
                case VirtualKey.I: SetTool("picker"); e.Handled = true; return;
                case VirtualKey.L: SetTool("line"); e.Handled = true; return;
                case VirtualKey.U: SetTool("rect"); e.Handled = true; return;
                case VirtualKey.Number1: SetLayer(SkinRegion.Base); e.Handled = true; return;
                case VirtualKey.Number2: SetLayer(SkinRegion.Overlay); e.Handled = true; return;
                case KeyLBracket: ChangeBrush(-1); e.Handled = true; return;
                case KeyRBracket: ChangeBrush(1); e.Handled = true; return;
                case VirtualKey.Add:
                case VirtualKey.Subtract:
                case KeyPlus:
                case KeyMinus:
                    StepZoom(zoomIn ? 1 : -1);
                    e.Handled = true;
                    return;
            }
        }

        private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // 不按 Ctrl 时把滚轮留给 ScrollViewer 平移画布
            if (!IsKeyDown(VirtualKey.Control) && !IsKeyDown(VirtualKey.LeftControl) && !IsKeyDown(VirtualKey.RightControl))
                return;
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            if (delta == 0) return;
            StepZoom(delta > 0 ? 1 : -1);
            e.Handled = true;
        }

        private void SetLayer(SkinRegion region)
        {
            if (region == SkinRegion.Base) BaseLayerRadio.IsChecked = true;
            else OverlayLayerRadio.IsChecked = true;
            Layer_Changed(this, new RoutedEventArgs());
        }

        private void ChangeBrush(int delta)
        {
            _brushSize = Math.Clamp(_brushSize + delta, 1, 4);
            SyncBrushCombo();
        }

        /// <summary>按缩放档位上下跳一格；Ctrl+滚轮、Ctrl +/− 与 +/- 共用。</summary>
        private void StepZoom(int direction)
        {
            int idx = 0;
            for (int i = 1; i < ZoomLadder.Length; i++)
            {
                if (Math.Abs(ZoomLadder[i] - _zoom) <= Math.Abs(ZoomLadder[idx] - _zoom)) idx = i;
            }
            SetZoomIndex(idx + direction);
        }

        private void ResetZoom() => SetZoomIndex(Array.IndexOf(ZoomLadder, DefaultZoom));

        private void SetZoomIndex(int index)
        {
            index = Math.Clamp(index, 0, ZoomLadder.Length - 1);
            _zoom = ZoomLadder[index];
            _syncing = true;
            try { SelectComboItemByTag(ZoomCombo, _zoom.ToString()); }
            finally { _syncing = false; }
            ApplyZoom();
            RenderGrid();
            EditorStatusText.Text = $"画布缩放 {_zoom}×。";
        }

        private void SyncBrushCombo()
        {
            _syncing = true;
            try { SelectComboItemByTag(BrushSizeCombo, _brushSize.ToString()); }
            finally { _syncing = false; }
            ApplyZoom();
        }

        private static bool IsKeyDown(VirtualKey key)
        {
            try
            {
                var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
                return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            }
            catch (Exception) { return false; }
        }

        // ==================================================================
        // 缩放 / 网格 / 开关
        // ==================================================================

        private void BrushSize_Changed(object sender, EventArgs e)
        {
            if (_syncing) return;
            if (BrushSizeCombo.SelectedItem is SelectBoxItem { Tag: string tag } && int.TryParse(tag, out var size))
                _brushSize = Math.Clamp(size, 1, 4);
            ApplyZoom();
        }

        private void Zoom_Changed(object sender, EventArgs e)
        {
            if (_syncing) return;
            if (ZoomCombo.SelectedItem is SelectBoxItem { Tag: string tag } && int.TryParse(tag, out var zoom))
                _zoom = Math.Clamp(zoom, 2, 16);
            ApplyZoom();
            RenderGrid();
        }

        private void GridToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _showGrid = GridToggle.IsOn;
            RenderGrid();
        }

        private void MirrorToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _mirror = MirrorToggle.IsOn;
        }

        private void GhostOtherToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _ghostOther = GhostOtherToggle.IsOn;
            RenderLayers();
        }

        private void SyncControls()
        {
            _syncing = true;
            try
            {
                SelectComboItemByTag(BrushSizeCombo, _brushSize.ToString());
                SelectComboItemByTag(ZoomCombo, _zoom.ToString());
                GridToggle.IsOn = _showGrid;
                MirrorToggle.IsOn = _mirror;
                GhostOtherToggle.IsOn = _ghostOther;
                BaseLayerRadio.IsChecked = _layer == SkinRegion.Base;
                OverlayLayerRadio.IsChecked = _layer == SkinRegion.Overlay;
                ColorSwatch.Background = new SolidColorBrush(_color);
            }
            finally
            {
                _syncing = false;
            }
            UpdateToolButtons();
            UpdatePartButtons();
        }

        private static void SelectComboItemByTag(SelectBox combo, string tag)
        {
            foreach (var item in combo.Items.OfType<SelectBoxItem>())
            {
                if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        // ==================================================================
        // 指针落笔
        // ==================================================================

        private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            try { CanvasSurface.Focus(FocusState.Pointer); } catch (Exception) { }
            var point = e.GetCurrentPoint(CanvasSurface);
            bool left = point.Properties.IsLeftButtonPressed;
            bool right = point.Properties.IsRightButtonPressed;
            if (!left && !right) return;

            var target = ToPixel(point.Position);
            UpdateCursor(target);
            if (target is null) return;
            int px = target.Value.X, py = target.Value.Y;

            if (_tool == SkinTool.Picker && !right)
            {
                var picked = _doc.Canvas.Pick(px, py);
                if (picked.A > 0)
                {
                    SetColor(Color.FromArgb(picked.A, picked.R, picked.G, picked.B));
                    EditorStatusText.Text = "已取色：R" + picked.R + " G" + picked.G + " B" + picked.B;
                }
                else
                {
                    EditorStatusText.Text = "该点是透明像素，没有颜色可取。";
                }
                return;
            }

            if (_tool == SkinTool.Bucket && !right)
            {
                _doc.BeginStroke();
                _doc.FloodFillFace(px, py, _color.B, _color.G, _color.R, 0xFF, _layer, _mirror);
                RenderAll();
                PushPreview();
                e.Handled = true;
                return;
            }
            // 油漆桶 + 右键 = 擦除该面
            if (_tool == SkinTool.Bucket && right)
            {
                _doc.BeginStroke();
                _doc.FloodFillFace(px, py, 0, 0, 0, 0, _layer, _mirror);
                RenderAll();
                PushPreview();
                e.Handled = true;
                return;
            }

            CanvasSurface.CapturePointer(e.Pointer);
            _painting = true;
            _strokeErase = right || _tool == SkinTool.Eraser;
            _strokeShift = IsKeyDown(VirtualKey.Shift);
            _lastX = px; _lastY = py;
            _anchorX = px; _anchorY = py;

            _doc.BeginStroke();

            if (_tool is SkinTool.Line or SkinTool.Rect or SkinTool.RectFill)
            {
                _shaping = true;
                UpdateShapeOverlay(px, py, px, py, _strokeShift);
            }
            else
            {
                ApplyBrushSegment(px, py, px, py);
                RenderAll();
            }
            e.Handled = true;
        }

        private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(CanvasSurface);
            var target = ToPixel(point.Position);
            UpdateCursor(target);

            if (target is null)
            {
                if (_painting && _shaping) HideShapeOverlay();
                return;
            }
            if (!_painting) return;
            if (_tool is SkinTool.Bucket or SkinTool.Picker) return;

            int px = target.Value.X, py = target.Value.Y;
            bool shift = IsKeyDown(VirtualKey.Shift);
            _strokeShift = shift;

            if (_shaping)
            {
                UpdateShapeOverlay(_anchorX, _anchorY, px, py, shift);
                e.Handled = true;
                return;
            }

            if (px != _lastX || py != _lastY)
            {
                ApplyBrushSegment(_lastX, _lastY, px, py);
                _lastX = px;
                _lastY = py;
                RenderAll();
            }
            e.Handled = true;
        }

        private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_shaping && _painting)
            {
                var point = e.GetCurrentPoint(CanvasSurface);
                var target = ToPixel(point.Position);
                if (target is not null)
                {
                    var (x1, y1) = ConstrainShape(_anchorX, _anchorY, target.Value.X, target.Value.Y, _strokeShift);
                    ApplyShape(_anchorX, _anchorY, x1, y1);
                    RenderAll();
                }
                HideShapeOverlay();
                _shaping = false;
                _painting = false;
                try { CanvasSurface.ReleasePointerCapture(e.Pointer); } catch (Exception) { }
                PushPreview();
                e.Handled = true;
                return;
            }
            if (!_painting) return;
            _painting = false;
            try { CanvasSurface.ReleasePointerCapture(e.Pointer); } catch (Exception) { }
            PushPreview();
            e.Handled = true;
        }

        private void Canvas_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            CursorBox.Visibility = Visibility.Collapsed;
        }

        private void ApplyBrushSegment(int x0, int y0, int x1, int y1)
        {
            byte b = 0, g = 0, r = 0, a = 0;
            if (!_strokeErase) { b = _color.B; g = _color.G; r = _color.R; a = 0xFF; }

            if (_tool == SkinTool.Darken && !_strokeErase)
            {
                _doc.ShadeSegment(x0, y0, x1, y1, 0.82, _layer, _mirror, _brushSize);
                return;
            }
            if (_tool == SkinTool.Lighten && !_strokeErase)
            {
                _doc.ShadeSegment(x0, y0, x1, y1, 1.22, _layer, _mirror, _brushSize);
                return;
            }
            _doc.StrokeSegment(x0, y0, x1, y1, b, g, r, a, _layer, _mirror, _brushSize);
        }

        private void ApplyShape(int x0, int y0, int x1, int y1)
        {
            byte b = 0, g = 0, r = 0, a = 0;
            if (!_strokeErase) { b = _color.B; g = _color.G; r = _color.R; a = 0xFF; }

            if (_tool == SkinTool.Line)
            {
                _doc.StrokeSegment(x0, y0, x1, y1, b, g, r, a, _layer, _mirror, _brushSize);
            }
            else if (_tool == SkinTool.Rect)
            {
                _doc.StrokeRect(x0, y0, x1, y1, b, g, r, a, _layer, _mirror, _brushSize);
            }
            else if (_tool == SkinTool.RectFill)
            {
                _doc.FillRect(x0, y0, x1, y1, b, g, r, a, _layer, _mirror);
            }
        }

        private static (int X, int Y) ConstrainShape(int x0, int y0, int x1, int y1, bool shift)
        {
            // 直线 + Shift：按主导轴强制水平 / 垂直；矩形不约束（保持拖拽直觉）。
            if (!shift) return (x1, y1);
            if (Math.Abs(x1 - x0) >= Math.Abs(y1 - y0)) return (x1, y0);
            return (x0, y1);
        }

        /// <summary>把形状预览画到 ShapeOverlay 上（展开图坐标系，乘 zoom 换算）。</summary>
        private void UpdateShapeOverlay(int x0, int y0, int x1, int y1, bool shift)
        {
            var (cx1, cy1) = ConstrainShape(x0, y0, x1, y1, _tool == SkinTool.Line && shift);
            var n0 = TextureToSurface(x0, y0);
            var n1 = TextureToSurface(cx1, cy1);
            if (n0 is null || n1 is null) { HideShapeOverlay(); return; }

            double sx0 = n0.Value.X * _zoom, sy0 = n0.Value.Y * _zoom;
            double sx1 = n1.Value.X * _zoom, sy1 = n1.Value.Y * _zoom;

            if (_tool == SkinTool.Line)
            {
                ShapeLine.X1 = sx0 + _zoom / 2.0; ShapeLine.Y1 = sy0 + _zoom / 2.0;
                ShapeLine.X2 = sx1 + _zoom / 2.0; ShapeLine.Y2 = sy1 + _zoom / 2.0;
                ShapeLine.Visibility = Visibility.Visible;
                ShapeRect.Visibility = Visibility.Collapsed;
            }
            else
            {
                double left = Math.Min(sx0, sx1), top = Math.Min(sy0, sy1);
                double w = Math.Abs(sx1 - sx0) + _zoom, h = Math.Abs(sy1 - sy0) + _zoom;
                ShapeRect.Width = w; ShapeRect.Height = h;
                Canvas.SetLeft(ShapeRect, left);
                Canvas.SetTop(ShapeRect, top);
                ShapeRect.Visibility = Visibility.Visible;
                ShapeLine.Visibility = Visibility.Collapsed;
                if (_tool == SkinTool.RectFill) ShapeRect.Fill = ThemeService.Brush("AccentBrush");
                else ShapeRect.Fill = null;
                ShapeRect.Opacity = _tool == SkinTool.RectFill ? 0.35 : 1.0;
            }
        }

        private void HideShapeOverlay()
        {
            ShapeRect.Visibility = Visibility.Collapsed;
            ShapeLine.Visibility = Visibility.Collapsed;
        }

        /// <summary>贴图像素 → 画布表面格子（展开图模式需反查回展开图，整图模式直通）。</summary>
        private (int X, int Y)? TextureToSurface(int x, int y)
        {
            if (_part is not SkinPart part) return (x, y);
            return SkinPartLayout.ToNet(part, x, y);
        }

        /// <summary>
        /// 控件坐标 → 64×64 贴图像素坐标；返回 null 表示这个点不该落笔。
        /// 展开图模式下先把「展开图坐标」翻译回贴图坐标，落在两个空白角上就返回 null。
        /// </summary>
        private (int X, int Y)? ToPixel(Windows.Foundation.Point position)
        {
            int nx = (int)Math.Floor(position.X / _zoom);
            int ny = (int)Math.Floor(position.Y / _zoom);

            if (_part is not SkinPart part)
            {
                if (nx < 0 || nx >= SkinCanvas.Size || ny < 0 || ny >= SkinCanvas.Size) return null;
                return (nx, ny);
            }

            return SkinPartLayout.ToTexture(part, nx, ny, _layer);
        }

        // ==================================================================
        // 渲染（全部委托 SkinBitmapRenderer，本页只负责尺寸与赋值）
        // ==================================================================

        private (int Width, int Height) SurfaceSize() =>
            _part is SkinPart part ? SkinPartLayout.NetSize(part) : (SkinCanvas.Size, SkinCanvas.Size);

        private void ApplyZoom()
        {
            var (w, h) = SurfaceSize();
            double pw = w * _zoom;
            double ph = h * _zoom;

            CanvasSurface.Width = pw;
            CanvasSurface.Height = ph;
            GridOverlay.Width = pw;
            GridOverlay.Height = ph;
            ShapeOverlay.Width = pw;
            ShapeOverlay.Height = ph;
            CursorBox.Width = _brushSize * _zoom;
            CursorBox.Height = _brushSize * _zoom;
            RenderLayers();
            RenderGrid();
        }

        private void UpdateCursor((int X, int Y)? target)
        {
            if (target is null)
            {
                CursorBox.Visibility = Visibility.Collapsed;
                return;
            }

            int cx, cy;
            if (_part is SkinPart part)
            {
                var net = SkinPartLayout.ToNet(part, target.Value.X, target.Value.Y);
                if (net is null)
                {
                    CursorBox.Visibility = Visibility.Collapsed;
                    return;
                }
                cx = net.Value.X;
                cy = net.Value.Y;
            }
            else
            {
                cx = target.Value.X;
                cy = target.Value.Y;
            }

            CursorBox.Margin = new Thickness(cx * _zoom, cy * _zoom, 0, 0);
            CursorBox.Width = _brushSize * _zoom;
            CursorBox.Height = _brushSize * _zoom;
            CursorBox.Visibility = Visibility.Visible;
        }

        // ==================================================================
        // 「边画边看」实时 3D 预览
        // ==================================================================

        private void PushPreview()
        {
            if (!_ready || !_viewerBoot) return;

            if (_previewTimer is null)
            {
                _previewTimer = DispatcherQueue.CreateTimer();
                _previewTimer.Interval = TimeSpan.FromMilliseconds(150);
                _previewTimer.IsRepeating = false;
                _previewTimer.Tick += (_, _) => _ = PushPreviewNowAsync();
            }

            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async Task PushPreviewNowAsync()
        {
            if (_pushingPreview) return;
            _pushingPreview = true;
            try
            {
                await SkinViewer.ShowSkinPixelsAsync(_doc.Pixels, _doc.IsSlim);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Skin", "实时 3D 预览刷新失败：" + ex.Message);
            }
            finally
            {
                _pushingPreview = false;
            }
        }

        private void RenderAll()
        {
            RenderLayers();
            RenderPreviewHead();
            UndoButton.IsEnabled = _doc.CanUndo;
            RedoButton.IsEnabled = _doc.CanRedo;
            UpdateScopeText();
        }

        private void RenderLayers()
        {
            if (CanvasImage is null) return;
            if (_part is SkinPart part)
            {
                CanvasImage.Source = SkinBitmapRenderer.RenderNet(_doc.Pixels, part, _zoom);
                GhostLayerImage.Source = null;
                GhostLayerImage.Opacity = 0;
                return;
            }

            var other = _layer == SkinRegion.Base ? SkinRegion.Overlay : SkinRegion.Base;
            CanvasImage.Source = SkinBitmapRenderer.RenderLayer(_doc.Pixels, _layer, _zoom);
            GhostLayerImage.Source = SkinBitmapRenderer.RenderLayer(_doc.Pixels, other, _zoom);
            GhostLayerImage.Opacity = _ghostOther ? 0.38 : 0.0;
        }

        private void RenderPreviewHead()
        {
            if (PreviewHeadImage is null) return;
            PreviewHeadImage.Source = SkinBitmapRenderer.RenderHead(_doc.Pixels);
        }

        private void RenderGrid()
        {
            if (GridOverlay is null) return;
            GridOverlay.Children.Clear();
            if (!_showGrid) return;

            var brush = ThemeService.Brush("DividerBrush");
            var lines = _part is SkinPart part
                ? SkinBitmapRenderer.NetGridLines(part, _zoom)
                : SkinBitmapRenderer.WholeGridLines(_zoom);
            foreach (var (x1, y1, x2, y2) in lines)
            {
                GridOverlay.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = brush, StrokeThickness = 1, Opacity = 0.5
                });
            }
        }

        // ==================================================================
        // 模型（经典 / 纤细）与皮肤来源。来源那块是 SkinSourceEditor 控件，
        // 和账号浮层共用一份逻辑；这里只管画布侧的刷新。
        // ==================================================================

        private void RefreshAccountChrome()
        {
            var account = AccountStore.Instance.Selected;
            AccountNameText.Text = account?.Username ?? "（未选择账号）";
            AccountTypeText.Text = account?.TypeLabel ?? "";
            AccountSkinSourceText.Text = account is null ? "" : "当前来源：" + account.SkinSourceLabel;
            RefreshSkinControls(account);
        }

        private void RefreshSkinControls(GameAccount? account)
        {
            ModelRow.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
            PaintToggle(ModelClassicButton, account is null || !account.IsSlimModel);
            PaintToggle(ModelSlimButton, account is not null && account.IsSlimModel);
            SkinStatusText.Text = account is null
                ? "还没有账号，点窗口右上角头像添加一个。"
                : "已保存到该账号。换账号后这里的设置互不影响。";
        }

        private void Model_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            if (sender is not Button { Tag: string tag }) return;
            var account = AccountStore.Instance.Selected;
            if (account is null) return;

            try
            {
                _syncing = true;
                account.SkinVariant = tag;
                AccountStore.Instance.Persist();
                SkinAvatarService.Invalidate(account.Uuid);
                // 画布的 slim 影响身体部位模板的横向偏移，切完必须重画并推给 3D 预览
                _doc.IsSlim = string.Equals(tag, GameAccount.SkinVariantSlim, StringComparison.OrdinalIgnoreCase);
                RenderAll();
                PushPreview();
                RefreshSkinControls(account);
            }
            catch (Exception ex)
            {
                SkinStatusText.Text = "保存失败：" + ex.Message;
            }
            finally
            {
                _syncing = false;
            }
        }

        private async Task RefreshViewerAsync(GameAccount? account)
        {
            try
            {
                if (account is null)
                {
                    await SkinViewer.ShowSkinAsync(null, false);
                    return;
                }

                var path = await SkinAvatarService.GetFullSkinPathAsync(account);
                if (string.IsNullOrEmpty(path))
                {
                    path = Path.Combine(AppContext.BaseDirectory, "Assets", "skin-default",
                        account.IsSlimModel ? "alex.png" : "steve.png");
                }
                await SkinViewer.ShowSkinAsync(path, account.IsSlimModel);
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Skin", "皮肤预览刷新失败：" + ex.Message);
            }
        }
    }
}
