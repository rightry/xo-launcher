using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 自制输入框控件。外观（圆角外壳、底色、悬停 / 聚焦描边）全部自绘，
    /// 内部的 <c>TextBox</c> 仅作透明无框的键盘输入元件 —— 不再依赖 WinUI TextBox
    /// 默认模板里那些无法主题化的资源键。
    ///
    /// 对外暴露的属性 / 事件与 <c>TextBox</c> 常用面保持一致（<see cref="Text"/>、
    /// <see cref="PlaceholderText"/>、<see cref="IsReadOnly"/>、<see cref="TextChanged"/> 等），
    /// 且 <c>KeyDown</c> / <c>LostFocus</c> / <c>GotFocus</c> 由内部 TextBox 冒泡上来，
    /// 调用方几乎无需改动。
    /// </summary>
    public sealed partial class TextInput : UserControl
    {
        private bool _focused;
        private bool _hovered;
        private bool _syncing;

        public TextInput()
        {
            InitializeComponent();

            Input.TextChanged += OnInputTextChanged;
            Input.GotFocus += (_, _) => { _focused = true; ApplyState(); };
            Input.LostFocus += (_, _) => { _focused = false; ApplyState(); };

            Shell.PointerEntered += (_, _) => { _hovered = true; ApplyState(); };
            Shell.PointerExited += (_, _) => { _hovered = false; ApplyState(); };

            Loaded += (_, _) => ApplyState();
            ApplyState();
        }

        // ==================== 依赖属性 ====================

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value ?? "");
        }
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(TextInput),
                new PropertyMetadata("", OnTextPropertyChanged));

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value ?? "");
        }
        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(TextInput),
                new PropertyMetadata(""));

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }
        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(TextInput),
                new PropertyMetadata(false, OnVisualPropertyChanged));

        public bool AcceptsReturn
        {
            get => (bool)GetValue(AcceptsReturnProperty);
            set => SetValue(AcceptsReturnProperty, value);
        }
        public static readonly DependencyProperty AcceptsReturnProperty =
            DependencyProperty.Register(nameof(AcceptsReturn), typeof(bool), typeof(TextInput),
                new PropertyMetadata(false, OnAcceptsReturnChanged));

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)GetValue(TextWrappingProperty);
            set => SetValue(TextWrappingProperty, value);
        }
        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(TextInput),
                new PropertyMetadata(TextWrapping.NoWrap));

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }
        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(TextInput),
                new PropertyMetadata(TextAlignment.Left));

        /// <summary>无外壳模式：透明、无边框、不做悬停/聚焦描边。用于已经嵌在自绘容器内部的场景（如聊天输入条）。</summary>
        public bool Chromeless
        {
            get => (bool)GetValue(ChromelessProperty);
            set => SetValue(ChromelessProperty, value);
        }
        public static readonly DependencyProperty ChromelessProperty =
            DependencyProperty.Register(nameof(Chromeless), typeof(bool), typeof(TextInput),
                new PropertyMetadata(false, OnVisualPropertyChanged));

        /// <summary>凹陷模式：使用「内凹」底色与描边（只读展示 / 房间码这类沉下去的输入区）。</summary>
        public bool Recessed
        {
            get => (bool)GetValue(RecessedProperty);
            set => SetValue(RecessedProperty, value);
        }
        public static readonly DependencyProperty RecessedProperty =
            DependencyProperty.Register(nameof(Recessed), typeof(bool), typeof(TextInput),
                new PropertyMetadata(false, OnVisualPropertyChanged));

        /// <summary>文本变化。与 <c>TextBox</c> 一致：用户输入与程序化赋值都会触发。</summary>
        public event TextChangedEventHandler? TextChanged;

        /// <summary>把焦点落到输入框。</summary>
        public void FocusInput() => Input.Focus(FocusState.Programmatic);

        // ==================== 内部实现 ====================

        private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (TextInput)d;
            var value = (string)(e.NewValue ?? "");
            if (self.Input is not null && self.Input.Text != value)
            {
                self._syncing = true;
                self.Input.Text = value;
                self._syncing = false;
            }
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e)
        {
            // 用户输入时把文本同步回 Text 依赖属性；程序化赋值(_syncing)时 Text 已是最新，无需回写。
            if (!_syncing) Text = Input.Text ?? "";
            TextChanged?.Invoke(this, e);
        }

        private static void OnAcceptsReturnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (TextInput)d;
            if (self.Input is not null)
                self.Input.VerticalContentAlignment = self.AcceptsReturn
                    ? VerticalAlignment.Top
                    : VerticalAlignment.Center;
            self.ApplyState();
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((TextInput)d).ApplyState();

        /// <summary>
        /// 依据 只读 / 悬停 / 聚焦 / 内嵌 / 凹陷 状态重绘外壳底色、描边与圆角。
        /// 颜色统一取自输入控件主题键，与其它控件同源。
        /// </summary>
        private void ApplyState()
        {
            if (Shell is null) return;

            if (Chromeless)
            {
                Shell.Background = null;
                Shell.BorderBrush = null;
                Shell.BorderThickness = new Thickness(0);
                return;
            }

            Shell.BorderThickness = new Thickness(1);
            Shell.Background = Recessed
                ? ThemeService.Brush("RecessedBackgroundBrush")
                : ThemeService.Brush("TextControlBackground");

            if (!IsReadOnly && _focused)
                Shell.BorderBrush = ThemeService.Brush("AccentBrush");
            else if (!IsReadOnly && _hovered)
                Shell.BorderBrush = ThemeService.Brush("TextControlBorderBrushPointerOver");
            else
                Shell.BorderBrush = Recessed
                    ? ThemeService.Brush("RecessedBorderBrush")
                    : ThemeService.Brush("TextControlBorderBrush");
        }
    }
}
