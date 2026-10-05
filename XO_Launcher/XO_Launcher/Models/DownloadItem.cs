using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;

namespace XO_Launcher.Models
{
    /// <summary>
    /// 下载页列表项的统一展示模型：原版版本、模组加载器、Modrinth 项目均转换为该模型显示。
    /// </summary>
    public class DownloadItem : INotifyPropertyChanged
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Badge { get; set; } = "";
        public string Meta { get; set; } = "";

        /// <summary>图标占位符文字（远程图标加载失败时的兜底）。</summary>
        public string IconText { get; set; } = "";

        /// <summary>图标占位符底色（Modrinth 项目直接使用其主题色）。</summary>
        public string IconColor { get; set; } = "#2D2D2D";

        public bool HasIcon => _icon is not null;

        /// <summary>远程图标地址。必须经 RemoteIconLoader 下载校验后再交给 BitmapImage（含 WebP 解码回退）。</summary>
        public string IconUrl { get; set; } = "";

        private ImageSource? _icon;

        /// <summary>远程图标（Modrinth CDN）或本地版本图标。</summary>
        public ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
            }
        }

        /// <summary>悬停背景图地址（Modrinth 图库 / CurseForge 截图）；为空表示该条目没有背景图。</summary>
        public string CoverUrl { get; set; } = "";

        private ImageBrush? _cover;

        /// <summary>
        /// 悬停背景图（列表卡片鼠标悬停时显示，按需懒加载）。
        /// 用 ImageBrush 而不是 ImageSource：绑定到 Border.Background 才能顺着
        /// CornerRadius 裁掉四角（Border 只裁自己的背景笔刷，不裁子元素几何）。
        /// </summary>
        public ImageBrush? Cover
        {
            get => _cover;
            set
            {
                if (ReferenceEquals(_cover, value)) return;
                _cover = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverOpacity)));
            }
        }

        public bool HasCover => _cover is not null;

        private bool _isHovered;

        /// <summary>卡片是否处于鼠标悬停态（由 DownloadPage 的指针事件写入）。</summary>
        public bool IsHovered
        {
            get => _isHovered;
            set
            {
                if (_isHovered == value) return;
                _isHovered = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHovered)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverOpacity)));
            }
        }

        /// <summary>背景层透明度：只有「悬停中且图已就绪」才淡入，直接绑定到背景层的 Opacity。</summary>
        public double CoverOpacity => _isHovered && HasCover ? 1 : 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>点击下载/打开时跳转的链接。</summary>
        public string DownloadUrl { get; set; } = "";

        /// <summary>Modrinth 项目标识（slug 或 id）。为空表示非 Modrinth 项目（原版/加载器）。</summary>
        public string Slug { get; set; } = "";

        /// <summary>Modrinth 项目 ID；详情请求优先用它，避免 slug 为空。</summary>
        public string ProjectId { get; set; } = "";

        /// <summary>Modrinth 项目类型（mod / shader / resourcepack / modpack）或 vanilla。</summary>
        public string ProjectType { get; set; } = "";

        /// <summary>列表项种类：modrinth / curseforge / vanilla / loader / bedrock。</summary>
        public string ItemKind { get; set; } = "";

        /// <summary>CurseForge 模组数字 ID。</summary>
        public int CurseForgeId { get; set; }

        /// <summary>基岩版条目类型：client / preview / server。</summary>
        public string BedrockKind { get; set; } = "";

        /// <summary>Microsoft Store UpdateID，用于解析 APPX 直链。</summary>
        public string UpdateId { get; set; } = "";

        public bool KindIsServer =>
            string.Equals(BedrockKind, "server", StringComparison.OrdinalIgnoreCase);

        /// <summary>Mojang 版本类型：release / snapshot / old_beta / old_alpha。</summary>
        public string VersionType { get; set; } = "";

        /// <summary>原版版本的类型图标（正式版 = 草方块，快照/预览版 = 命令方块）。</summary>
        public string IconPath => Services.VersionIconService.GetIconPath(
            null,
            ItemKind == "bedrock"
                ? BedrockKind switch
                {
                    "preview" => "bedrock-preview",
                    "server" => "bedrock-server",
                    _ => "bedrock"
                }
                : "vanilla",
            string.IsNullOrWhiteSpace(VersionType) ? "release" : VersionType);

        public bool HasDetail { get; set; }
        public bool IsAlpha { get; set; }

        /// <summary>该原版版本已发布的模组加载器，如 “Fabric  ·  Forge”。</summary>
        public string LoaderSupport { get; set; } = "";

        /// <summary>
        /// 适配（支持）的 Minecraft 游戏版本摘要，如 “1.20.1 ~ 1.21.4”。
        /// 由远端元数据自动标记，取不到时为空（不显示）。
        /// </summary>
        public string GameVersions { get; set; } = "";

        /// <summary>
        /// 卡片中部的「支持版本」文本：原版版本显示可用的模组加载器，模组类条目显示适配的游戏版本。
        /// 两者互斥（同一时刻只有一个有值），因此共用同一个展示位。
        /// </summary>
        public string SupportText => !string.IsNullOrWhiteSpace(LoaderSupport) ? LoaderSupport : GameVersions;

        public bool HasSupportText => !string.IsNullOrWhiteSpace(SupportText);
    }
}
