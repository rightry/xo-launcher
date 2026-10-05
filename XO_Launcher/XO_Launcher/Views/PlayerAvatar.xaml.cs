using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class PlayerAvatar : UserControl
    {
        public static readonly DependencyProperty AvatarSizeProperty = DependencyProperty.Register(
            nameof(AvatarSize),
            typeof(double),
            typeof(PlayerAvatar),
            new PropertyMetadata(40.0, OnAvatarSizeChanged));

        /// <summary>可绑定的账号：数据模板里没机会调 Show()，所以做成依赖属性，赋值即刷新。</summary>
        public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(
            nameof(Account),
            typeof(GameAccount),
            typeof(PlayerAvatar),
            new PropertyMetadata(null, OnAccountChanged));

        public PlayerAvatar()
        {
            InitializeComponent();
            ApplySize(AvatarSize);
        }

        public double AvatarSize
        {
            get => (double)GetValue(AvatarSizeProperty);
            set => SetValue(AvatarSizeProperty, value);
        }

        public GameAccount? Account
        {
            get => (GameAccount?)GetValue(AccountProperty);
            set => SetValue(AccountProperty, value);
        }

        private static void OnAccountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((PlayerAvatar)d).Show((GameAccount?)e.NewValue);

        private static void OnAvatarSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PlayerAvatar avatar)
                avatar.ApplySize((double)e.NewValue);
        }

        private void ApplySize(double size)
        {
            if (AvatarFrame is null) return;
            AvatarFrame.Width = size;
            AvatarFrame.Height = size;
            FallbackIcon.FontSize = Math.Max(10, size * 0.38);
        }

        public void Show(GameAccount? account)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Uuid))
            {
                ShowFallback();
                return;
            }

            // 头缓存命中（任何来源）直接显示；local / default 也要有头像，不能只认微软账号。
            var cached = SkinAvatarService.CachedPath(account.Uuid);
            if (cached is not null)
            {
                SetImage(cached);
                _ = RefreshAsync(account);
                return;
            }

            ShowFallback();
            _ = RefreshAsync(account);
        }

        private async System.Threading.Tasks.Task RefreshAsync(GameAccount account)
        {
            try
            {
                var path = await SkinAvatarService.GetHeadPathAsync(account);
                if (path is null || !File.Exists(path)) return;
                DispatcherQueue.TryEnqueue(() => SetImage(path));
            }
            catch (Exception)
            {
                // 头像下载失败时保留占位图标
            }
        }

        private void SetImage(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                var px = Math.Max(32, (int)Math.Ceiling(AvatarSize * 2));
                image.DecodePixelWidth = px;
                image.DecodePixelHeight = px;
                image.UriSource = new Uri(Path.GetFullPath(path));
                HeadImage.Source = image;
                HeadImage.Visibility = Visibility.Visible;
                FallbackIcon.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                ShowFallback();
            }
        }

        private void ShowFallback()
        {
            HeadImage.Source = null;
            HeadImage.Visibility = Visibility.Collapsed;
            FallbackIcon.Visibility = Visibility.Visible;
        }
    }
}
