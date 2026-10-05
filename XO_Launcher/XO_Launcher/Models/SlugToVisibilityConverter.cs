using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace XO_Launcher.Models
{
    /// <summary>Slug 非空时显示，否则隐藏（用于判断列表项是否为可进入详情的 Modrinth 项目）。</summary>
    public sealed class SlugToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>将 "#RRGGBB" 文本转为画刷，用于列表卡片与致谢图标底色。</summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string hex || string.IsNullOrWhiteSpace(hex))
                return new SolidColorBrush(Color.FromArgb(255, 45, 45, 45));

            if (hex.StartsWith("#", StringComparison.Ordinal)) hex = hex[1..];
            if (hex.Length == 6
                && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
                && byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
                && byte.TryParse(hex[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return new SolidColorBrush(Color.FromArgb(255, r, g, b));
            }

            return new SolidColorBrush(Color.FromArgb(255, 45, 45, 45));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Alpha 标红；Beta 用琥珀色；其余用主题强调色或正文色。</summary>
    public sealed class VersionTypeBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush AlphaBrush = new(Color.FromArgb(255, 229, 72, 77));
        private static readonly SolidColorBrush BetaBrush = new(Color.FromArgb(255, 227, 179, 65));
        private static readonly SolidColorBrush ReleaseBrush = new(Color.FromArgb(255, 124, 189, 75));
        private static readonly SolidColorBrush NormalBrush = new(Color.FromArgb(255, 240, 240, 240));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isAlpha = value switch
            {
                ModrinthVersion version => version.IsAlpha,
                DownloadItem item => item.IsAlpha,
                string type => type.Contains("alpha", StringComparison.OrdinalIgnoreCase),
                true => true,
                _ => false
            };
            if (isAlpha) return AlphaBrush;

            if (string.Equals(parameter as string, "title", StringComparison.Ordinal))
                return NormalBrush;

            var typeName = value switch
            {
                ModrinthVersion v => v.VersionType,
                DownloadItem item => item.VersionType,
                string s => s,
                _ => ""
            };
            if (typeName.Contains("beta", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
                return BetaBrush;

            return ReleaseBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var flag = value is true;
            if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
                flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>选中项使用强调色描边，未选中不画边框。</summary>
    public sealed class SelectedAccentBorderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true
                ? Services.ThemeService.Brush("AccentBrush")
                : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>把本地图片路径转成已校验的 BitmapImage，避免 WinUI 直绑 file:// 闪退。</summary>
    public sealed class SafeFileImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            var size = 36;
            if (parameter is string text && int.TryParse(text, out var parsed) && parsed > 0)
                size = parsed;
            return value is string path ? Services.RemoteIconLoader.TryCreateFromFile(path, size) : null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
