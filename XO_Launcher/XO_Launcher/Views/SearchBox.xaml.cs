using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    /// <summary>
    /// 自制搜索框控件。外观（圆角外壳、放大镜图标、清除键、搜索键）与交互态
    /// （悬停 / 聚焦描边）全部自绘，内部的 <c>TextBox</c> 仅作透明无框的键盘输入元件。
    ///
    /// 对外暴露 <see cref="Text"/> / <see cref="PlaceholderText"/> 与三个事件，
    /// 调用方无需再关心清除键可见性、聚焦高亮这些细节。
    /// </summary>
    public sealed partial class SearchBox : UserControl
    {
        private bool _focused;
        private bool _hovered;

        public SearchBox()
        {
            InitializeComponent();

            Input.TextChanged += (_, _) =>
            {
                ClearButton.Visibility = string.IsNullOrEmpty(Input.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                TextChanged?.Invoke(this, EventArgs.Empty);
            };
            Input.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    QuerySubmitted?.Invoke(this, EventArgs.Empty);
                }
            };
            Input.GotFocus += (_, _) => { _focused = true; ApplyBorder(); };
            Input.LostFocus += (_, _) => { _focused = false; ApplyBorder(); };

            ClearButton.Click += (_, _) =>
            {
                Input.Text = "";
                Cleared?.Invoke(this, EventArgs.Empty);
                Input.Focus(FocusState.Programmatic);
            };
            QueryButton.Click += (_, _) => QuerySubmitted?.Invoke(this, EventArgs.Empty);

            Shell.PointerEntered += (_, _) => { _hovered = true; ApplyBorder(); };
            Shell.PointerExited += (_, _) => { _hovered = false; ApplyBorder(); };

            ApplyBorder();
        }

        /// <summary>当前输入文本。</summary>
        public string Text
        {
            get => Input.Text;
            set => Input.Text = value ?? "";
        }

        /// <summary>占位提示文本。</summary>
        public string PlaceholderText
        {
            get => Input.PlaceholderText;
            set => Input.PlaceholderText = value ?? "";
        }

        /// <summary>是否显示右侧「搜索」按钮（本地即时筛选的场景可关掉，只留回车触发）。</summary>
        public bool ShowQueryButton
        {
            get => QueryButton.Visibility == Visibility.Visible;
            set => QueryButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>搜索按钮上的文字。</summary>
        public string QueryText
        {
            get => QueryLabel.Text;
            set => QueryLabel.Text = value ?? "";
        }

        /// <summary>文本变化（每次按键）。</summary>
        public event EventHandler? TextChanged;

        /// <summary>提交搜索：回车或点击搜索按钮。</summary>
        public event EventHandler? QuerySubmitted;

        /// <summary>点击清除键清空后触发（此时 <see cref="Text"/> 已为空）。</summary>
        public event EventHandler? Cleared;

        /// <summary>把焦点落到输入框。</summary>
        public void FocusInput() => Input.Focus(FocusState.Programmatic);

        /// <summary>
        /// 边框三态：聚焦 → 强调色加粗；悬停 → 悬停描边；默认 → 常规描边。
        /// 颜色取自统一的输入控件键，与其它输入框同源。
        /// </summary>
        private void ApplyBorder()
        {
            if (_focused)
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
