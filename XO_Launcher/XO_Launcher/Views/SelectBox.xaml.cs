using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 自制下拉列表项：只承载「显示文字 + 业务 Tag」，外观由 <see cref="SelectBox"/> 统一绘制。
    /// 对应原来写在 ComboBox 里的 <c>&lt;ComboBoxItem Content="…" Tag="…"/&gt;</c> 内联写法。
    /// </summary>
    public sealed class SelectBoxItem
    {
        public string Content { get; set; } = "";
        public object? Tag { get; set; }

        public override string ToString() => Content;
    }

    /// <summary>
    /// 带图标的下拉项。图标是「先占位、在线取到再替换」的异步资源，
    /// 所以这里给的是可绑定属性而不是构造好的元素：控件把 Image 与占位字形都绑到实现方，
    /// 图标到达时由绑定自己刷新，无需重建列表。
    /// </summary>
    public interface ISelectBoxIconOption
    {
        /// <summary>已取到的图标；为空表示还在取或取不到。</summary>
        ImageSource? Icon { get; }

        /// <summary>图标未就绪时的占位字形（图标字体码位）。</summary>
        string IconGlyph { get; }

        /// <summary><see cref="Icon"/> 是否已就绪，供可见性绑定用。</summary>
        bool HasIcon { get; }
    }


    /// <summary>
    /// 自制下拉列表。外壳、下拉面板、列表项、勾选态、悬停 / 聚焦描边全部自绘，
    /// 不再走 WinUI ComboBox 的模板与它那一整套 <c>ComboBox*</c> 主题键
    /// （那套键要逐键覆盖才能主题化，全景底和亮暗切换都得再补一遍）。
    ///
    /// 对外保持与 ComboBox 相同的用法面：<see cref="ItemsSource"/> +
    /// <see cref="DisplayMemberPath"/>、<see cref="ItemTemplate"/>、内联 <see cref="Items"/>、
    /// <see cref="SelectedItem"/> / <see cref="SelectedIndex"/> / <see cref="PlaceholderText"/>
    /// 与 <see cref="SelectionChanged"/>；额外支持 <see cref="ISelectBoxIconOption"/> 带图标项。
    /// </summary>
    public sealed partial class SelectBox : UserControl
    {
        private const string ChevronDownGlyph = "";
        private const string ChevronUpGlyph = "";

        /// <summary>
        /// 行尾勾选用 WinUI 3 内置符号字体的 CheckMark（E73E），不用项目自带的
        /// FluentSystemIcons-Filled：那套字体的 E73E 是 image_shadow，勾在 F294。
        /// </summary>
        private const string CheckmarkGlyph = "";

        private static readonly FontFamily SymbolFont = ResolveSymbolFont();

        private static FontFamily ResolveSymbolFont()
        {
            try
            {
                return ThemeService.Get<FontFamily>("SymbolThemeFontFamily");
            }
            catch (Exception)
            {
                return new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            }
        }

        private static readonly Brush TransparentRow = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        private readonly List<object> _options = new();
        private readonly List<Border> _rows = new();

        // 行是「按需生成」的：从没展开过就没有行，主题变化或数据源变化只置脏标记。
        private bool _rowsBuilt;
        private bool _rowsDirty = true;

        private bool _hovered;
        private bool _focused;
        private int _highlight = -1;
        private int _suppressEvents;

        // 内联 Items 是页面 XAML 构造过程中逐条加进来的，控件构造时看不到；
        // 期间设置的选中值先记着，等第一次真正建好列表再落位。
        private object? _pendingItem;
        private int _pendingIndex = -1;

        public SelectBox()
        {
            InitializeComponent();

            DropPopup.PlacementTarget = Shell;
            DropPopup.VerticalOffset = 4;
            DropPopup.Opened += (_, _) =>
            {
                _focused = true;
                UpdateChevron();
                ApplyState();
            };
            DropPopup.Closed += (_, _) =>
            {
                _highlight = -1;
                UpdateChevron();
                ApplyRows();
                ApplyState();
            };

            Shell.Tapped += (_, _) => Toggle();
            Shell.PointerEntered += (_, _) => { _hovered = true; ApplyState(); };
            Shell.PointerExited += (_, _) => { _hovered = false; ApplyState(); };
            Unloaded += (_, _) =>
            {
                // 页面被切走时下拉不会自己收，留着会浮在别的页面上
                DropPopup.IsOpen = false;
            };

            GotFocus += (_, _) => { _focused = true; ApplyState(); };
            LostFocus += (_, _) => { _focused = false; ApplyState(); };
            PreviewKeyDown += OnPreviewKeyDown;
            IsEnabledChanged += (_, _) => Root.Opacity = IsEnabled ? 1 : 0.45;

            Loaded += (_, _) =>
            {
                Rebuild();
                ApplySize();
                ApplyState();
            };

            // 列表项是代码里造的，拿不到 {ThemeResource} 的自动重解析：主题切换后必须重建行，
            // 否则底色与文字色停在旧主题上。没展开时只置脏标记，展开时 EnsureRows 再补，
            // 免得切一次主题就把两百多行字体项重新建一遍。
            ActualThemeChanged += (_, _) =>
            {
                _rowsDirty = true;
                if (DropPopup.IsOpen) RenderRows();
                UpdateHeader();
            };

            ApplySize();
        }

        // ==================== 依赖属性 ====================

        /// <summary>数据源。为 null 时改用内联的 <see cref="Items"/>。</summary>
        public IEnumerable? ItemsSource
        {
            get => (IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SelectBox),
                new PropertyMetadata(null, OnItemsSourcePropertyChanged));

        /// <summary>取哪个属性当显示文字（等价 ComboBox.DisplayMemberPath）。</summary>
        public string DisplayMemberPath
        {
            get => (string)GetValue(DisplayMemberPathProperty);
            set => SetValue(DisplayMemberPathProperty, value ?? "");
        }
        public static readonly DependencyProperty DisplayMemberPathProperty =
            DependencyProperty.Register(nameof(DisplayMemberPath), typeof(string), typeof(SelectBox),
                new PropertyMetadata("", OnTemplateChanged));

        /// <summary>列表项与选中回显共用的模板（等价 ComboBox.ItemTemplate）。</summary>
        public DataTemplate? ItemTemplate
        {
            get => (DataTemplate?)GetValue(ItemTemplateProperty);
            set => SetValue(ItemTemplateProperty, value);
        }
        public static readonly DependencyProperty ItemTemplateProperty =
            DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(SelectBox),
                new PropertyMetadata(null, OnTemplateChanged));

        /// <summary>紧凑档：32 高、12 号字，塞进工具条或筛选行时用。</summary>
        public bool Compact
        {
            get => (bool)GetValue(CompactProperty);
            set => SetValue(CompactProperty, value);
        }
        public static readonly DependencyProperty CompactProperty =
            DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(SelectBox),
                new PropertyMetadata(false, OnCompactPropertyChanged));

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value ?? "");
        }
        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(SelectBox),
                new PropertyMetadata("", OnPlaceholderTextChanged));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }
        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(SelectBox),
                new PropertyMetadata(-1, OnSelectedIndexPropertyChanged));

        public object? SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }
        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(SelectBox),
                new PropertyMetadata(null, OnSelectedItemPropertyChanged));

        /// <summary>内联项：<c>&lt;views:SelectBox&gt;&lt;views:SelectBoxItem …/&gt;&lt;/views:SelectBox&gt;</c>。</summary>
        public IList<SelectBoxItem> Items { get; } = new List<SelectBoxItem>();

        /// <summary>选中项发生变化（含代码赋值，与 ComboBox 的语义一致）。</summary>
        public event EventHandler? SelectionChanged;

        /// <summary>下拉面板是否展开。</summary>
        public bool IsDropDownOpen => DropPopup.IsOpen;

        /// <summary>当前实际可选项的数量（原 ComboBox.Items.Count 的等价读法）。</summary>
        public bool HasOptions => _options.Count > 0;

        // ==================== 属性回调 ====================

        private static void OnItemsSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SelectBox)d).Rebuild();

        private static void OnTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SelectBox)d).Rebuild();

        private static void OnCompactPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SelectBox)d).ApplySize();

        private static void OnPlaceholderTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((SelectBox)d).UpdateHeader();

        private static void OnSelectedIndexPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (SelectBox)d;
            if (box._suppressEvents > 0) return;
            var index = (int)e.NewValue;
            box._pendingIndex = index;
            if (index >= 0 && index < box._options.Count) box.CommitSelection(index, raiseEvent: true);
            else if (index < 0) box.ClearSelection(raiseEvent: true);
            // index 越界多半是内联 Items 还没读完，等 Rebuild 落位
        }

        private static void OnSelectedItemPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (SelectBox)d;
            if (box._suppressEvents > 0) return;
            var item = e.NewValue;
            box._pendingItem = item;
            if (item is null)
            {
                box.ClearSelection(raiseEvent: true);
                return;
            }
            var index = box._options.IndexOf(item);
            if (index >= 0) box.CommitSelection(index, raiseEvent: true);
            else box.UpdateHeader();
        }

        // ==================== 列表构建 ====================

        private void Rebuild()
        {
            var previous = SelectedItem;
            _options.Clear();
            var source = ItemsSource;
            if (source != null)
            {
                foreach (var entry in source)
                    if (entry is not null) _options.Add(entry);
            }
            else
            {
                foreach (var entry in Items)
                    if (entry is not null) _options.Add(entry);
            }

            _rowsDirty = true;
            // 已经建过行才需要重建（比如换数据源、切主题时正展开着）；没展开过就只留脏标记。
            if (_rowsBuilt) RenderRows();

            // 落位优先级：原选中项还在列表 → 沿用；否则用待落位值；都没有就清空。
            if (previous is not null && _options.Contains(previous))
                CommitSelection(_options.IndexOf(previous), raiseEvent: false);
            else if (_pendingItem is not null && _options.Contains(_pendingItem))
                CommitSelection(_options.IndexOf(_pendingItem), raiseEvent: false);
            else if (_pendingIndex >= 0 && _pendingIndex < _options.Count)
                CommitSelection(_pendingIndex, raiseEvent: false);
            else
                ClearSelection(raiseEvent: true);
        }

        /// <summary>第一次展开（或数据/主题变过再展开）时才把行建出来。</summary>
        private void EnsureRows()
        {
            if (_rowsBuilt && !_rowsDirty) return;
            RenderRows();
            _rowsBuilt = true;
            _rowsDirty = false;
        }

        private void RenderRows()
        {
            OptionHost.Children.Clear();
            _rows.Clear();
            foreach (var item in _options)
            {
                var row = BuildRow(item);
                _rows.Add(row);
                OptionHost.Children.Add(row);
            }
            ApplyRows();
        }

        /// <summary>行里记住的是数据项本身，序号现算：这样主题/数据变化后不用维护序号映射。</summary>
        private Border BuildRow(object item)
        {
            var check = new FontIcon
            {
                FontFamily = SymbolFont,
                Glyph = CheckmarkGlyph,
                FontSize = 13,
                Margin = new Thickness(8, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeService.Brush("AccentBrush"),
                Visibility = Visibility.Collapsed,
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = BuildContent(item);
            Grid.SetColumn(content, 0);
            layout.Children.Add(content);
            Grid.SetColumn(check, 1);
            layout.Children.Add(check);

            var row = new Border
            {
                Child = layout,
                CornerRadius = new CornerRadius(6),
                MinHeight = Compact ? 28 : 34,
                Padding = new Thickness(9, 5, 7, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Background = TransparentRow,
                Tag = item,
            };
            row.Tapped += (_, e) =>
            {
                e.Handled = true;
                PickRow(_options.IndexOf(item));
            };
            row.PointerEntered += (_, _) =>
            {
                _highlight = _options.IndexOf(item);
                ApplyRows();
            };
            row.PointerExited += (_, _) =>
            {
                if (_highlight == _options.IndexOf(item)) _highlight = -1;
                ApplyRows();
            };
            return row;
        }

        /// <summary>
        /// 列表项与回显共用的内容：优先 ItemTemplate，其次 SelectBoxItem.Content，
        /// 再次按 DisplayMemberPath 绑定取值，最后退回 ToString()。
        /// 回显与列表项各建一份元素（同一 UIElement 不能有两个父级）。
        /// </summary>
        private FrameworkElement BuildContent(object item)
        {
            if (ItemTemplate != null)
            {
                return new ContentPresenter
                {
                    Content = item,
                    ContentTemplate = ItemTemplate,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = Root.FontFamily,
                FontSize = Root.FontSize,
                Foreground = Root.Foreground,
            };

            if (item is SelectBoxItem inline)
                text.Text = inline.Content;
            else if (!string.IsNullOrEmpty(DisplayMemberPath))
                text.SetBinding(TextBlock.TextProperty, new Binding
                {
                    Path = new PropertyPath(DisplayMemberPath),
                    Source = item,
                });
            else
                text.Text = item.ToString() ?? "";

            return item is ISelectBoxIconOption iconned ? WithIcon(iconned, text) : text;
        }

        /// <summary>
        /// 图标位 + 文字。占位字形与在线图标叠在同一格里，靠 <see cref="ISelectBoxIconOption.HasIcon"/>
        /// 的绑定切换：图标还没取到（或取不到）时只显示字形，取到后由绑定自己换过来。
        ///
        /// 图标不用 <c>Image</c>：WinUI 的 Image.Source 不是依赖属性，绑不动，
        /// 只能把 ImageBrush 交给 Border.Background（Background 是依赖属性）。
        /// </summary>
        private FrameworkElement WithIcon(ISelectBoxIconOption option, FrameworkElement content)
        {
            var slot = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };

            var glyph = new FontIcon
            {
                FontFamily = ThemeService.IconFontFamily,
                Glyph = option.IconGlyph,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Root.Foreground,
            };
            glyph.SetBinding(UIElement.VisibilityProperty, HasIconBinding(option, invert: true));

            var image = new Border { Width = 20, Height = 20 };
            image.SetBinding(Border.BackgroundProperty, new Binding
            {
                Path = new PropertyPath(nameof(ISelectBoxIconOption.Icon)),
                Source = option,
                Converter = IconBrushConverter.Instance,
            });

            slot.Children.Add(glyph);
            slot.Children.Add(image);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(slot);
            row.Children.Add(content);
            return row;
        }

        private static Binding HasIconBinding(ISelectBoxIconOption option, bool invert) => new()
        {
            Path = new PropertyPath(nameof(ISelectBoxIconOption.HasIcon)),
            Source = option,
            Converter = new BoolToVisibilityConverter(),
            ConverterParameter = invert ? "invert" : "",
        };

        /// <summary>把 <see cref="ImageSource"/> 包成 ImageBrush；没有图标时给出透明底。</summary>
        private sealed class IconBrushConverter : IValueConverter
        {
            public static readonly IconBrushConverter Instance = new();
            private static readonly Brush Empty = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            public object Convert(object value, Type targetType, object parameter, string language)
                => value is ImageSource source
                    ? new ImageBrush { ImageSource = source, Stretch = Stretch.Uniform }
                    : Empty;

            public object ConvertBack(object value, Type targetType, object parameter, string language)
                => throw new NotSupportedException();
        }

        private void UpdateHeader()
        {
            var item = SelectedItem;
            SelectionHost.Content = item is null ? null : BuildContent(item);
            SelectionHost.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
            PlaceholderLabel.Text = PlaceholderText;
            PlaceholderLabel.Visibility = item is null && !string.IsNullOrEmpty(PlaceholderText)
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateChevron();
        }

        private void ApplyRows()
        {
            foreach (var row in _rows) ApplyRowState(row);
        }

        /// <summary>行的三档底色：选中 / 高亮（悬停或键盘）/ 无。</summary>
        private void ApplyRowState(Border row)
        {
            var index = _options.IndexOf(row.Tag!);
            if (index < 0) return;
            var isSelected = index == SelectedIndex;
            row.Background = isSelected
                ? ThemeService.Brush("DropDownItemBackgroundSelected")
                : index == _highlight
                    ? ThemeService.Brush("DropDownItemBackgroundPointerOver")
                    : TransparentRow;
            if (row.Child is Grid layout)
            {
                foreach (var child in layout.Children)
                {
                    if (child is FontIcon icon)
                        icon.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void UpdateChevron()
            => Chevron.Glyph = DropPopup.IsOpen ? ChevronUpGlyph : ChevronDownGlyph;

        // ==================== 交互 ====================

        private void Toggle()
        {
            if (DropPopup.IsOpen) DropPopup.IsOpen = false;
            else Open();
        }

        private void Open()
        {
            if (!IsEnabled || _options.Count == 0) return;
            // 外壳是 Border（不可聚焦），点它不会把焦点带到控件上；不主动要焦点，
            // 展开后方向键 / Enter / Esc 就全落到页面别的元素上。
            Root.Focus(FocusState.Programmatic);
            PanelRoot.MinWidth = Shell.ActualWidth;
            EnsureRows();
            _highlight = SelectedIndex >= 0 ? SelectedIndex : 0;
            ApplyRows();
            DropPopup.IsOpen = true;
            ScrollHighlightIntoView();
        }

        private void PickRow(int index)
        {
            if (index < 0 || index >= _options.Count) return;
            _highlight = index;
            ApplyRows();
            DropPopup.IsOpen = false;
            CommitSelection(index, raiseEvent: true);
        }

        /// <summary>写入选中态（Suppress 挡住两个 DP 互相回写），并按需触发事件。</summary>
        private void CommitSelection(int index, bool raiseEvent)
        {
            var item = _options[index];
            if (Equals(SelectedItem, item) && SelectedIndex == index) return;
            Suppress(() =>
            {
                SelectedItem = item;
                SelectedIndex = index;
            });
            _pendingItem = item;
            _pendingIndex = index;
            UpdateHeader();
            ApplyRows();
            if (raiseEvent) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ClearSelection(bool raiseEvent)
        {
            if (SelectedItem is null && SelectedIndex < 0) return;
            Suppress(() =>
            {
                SelectedItem = null;
                SelectedIndex = -1;
            });
            _pendingItem = null;
            _pendingIndex = -1;
            UpdateHeader();
            ApplyRows();
            if (raiseEvent) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Suppress(Action apply)
        {
            _suppressEvents++;
            try { apply(); }
            finally { _suppressEvents--; }
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!IsEnabled) return;

            if (!DropPopup.IsOpen)
            {
                if (e.Key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.F4 or VirtualKey.Down or VirtualKey.Up)
                {
                    e.Handled = true;
                    _focused = true;
                    Open();
                    if (e.Key == VirtualKey.Up) MoveHighlight(-1);
                }
                return;
            }

            switch (e.Key)
            {
                case VirtualKey.Up:
                    e.Handled = true;
                    MoveHighlight(-1);
                    break;
                case VirtualKey.Down:
                    e.Handled = true;
                    MoveHighlight(1);
                    break;
                case VirtualKey.Home when _options.Count > 0:
                    e.Handled = true;
                    _highlight = 0;
                    ApplyRows();
                    ScrollHighlightIntoView();
                    break;
                case VirtualKey.End when _options.Count > 0:
                    e.Handled = true;
                    _highlight = _options.Count - 1;
                    ApplyRows();
                    ScrollHighlightIntoView();
                    break;
                case VirtualKey.Enter or VirtualKey.Space:
                    e.Handled = true;
                    PickRow(_highlight);
                    break;
                case VirtualKey.Escape:
                    e.Handled = true;
                    DropPopup.IsOpen = false;
                    break;
            }
        }

        private void MoveHighlight(int delta)
        {
            if (_options.Count == 0) return;
            _highlight = _highlight < 0
                ? (delta > 0 ? 0 : _options.Count - 1)
                : Math.Clamp(_highlight + delta, 0, _options.Count - 1);
            ApplyRows();
            ScrollHighlightIntoView();
        }

        private void ScrollHighlightIntoView()
        {
            if (_highlight < 0 || _highlight >= _rows.Count) return;
            var row = _rows[_highlight];
            var offset = row.TransformToVisual(OptionHost).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (offset < 0)
                PanelScroll.ChangeView(null, PanelScroll.VerticalOffset + offset - 4, null, true);
            else if (offset + row.ActualHeight > PanelScroll.ViewportHeight)
                PanelScroll.ChangeView(null,
                    PanelScroll.VerticalOffset + offset + row.ActualHeight - PanelScroll.ViewportHeight + 4, null, true);
        }

        // ==================== 外观 ====================

        private void ApplySize()
        {
            var compact = Compact;
            Root.MinHeight = compact ? 32 : 40;
            Shell.MinHeight = compact ? 32 : 40;
            Shell.Padding = compact ? new Thickness(9, 7, 4, 7) : new Thickness(11, 10, 6, 10);
            if (ReadLocalValue(FontSizeProperty) == DependencyProperty.UnsetValue)
                Root.FontSize = compact ? 12 : 13;
            foreach (var row in _rows) row.MinHeight = compact ? 28 : 34;
        }

        /// <summary>外壳描边三态：展开/聚焦 → 强调色；悬停 → 悬停色；其余 → 常规色。</summary>
        private void ApplyState()
        {
            if (DropPopup.IsOpen || _focused)
            {
                Shell.BorderBrush = ThemeService.Brush("AccentBrush");
                Shell.BorderThickness = new Thickness(1.5);
            }
            else if (_hovered)
            {
                Shell.BorderBrush = ThemeService.Brush("TextControlBorderBrushPointerOver");
                Shell.BorderThickness = new Thickness(1);
            }
            else
            {
                Shell.BorderBrush = ThemeService.Brush("TextControlBorderBrush");
                Shell.BorderThickness = new Thickness(1);
            }
        }
    }
}
