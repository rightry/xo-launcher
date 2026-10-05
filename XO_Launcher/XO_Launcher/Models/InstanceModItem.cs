using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XO_Launcher.Models
{
    public sealed class InstanceModItem
    {
        public string FilePath { get; init; } = "";
        public string Name { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsEnabled { get; init; }
        public bool IsDirectory { get; init; }
        public bool IsLibraryItem { get; init; }
        public string SizeText { get; init; } = "";

        /// <summary>从 jar 元数据读出的模组版本号；读不到时为空串（光影 / 资源包通常为空）。</summary>
        public string Version { get; init; } = "";

        /// <summary>
        /// 从 jar 元数据读出的适配 Minecraft 版本，已归一化（如 <c>1.20.1</c> 或 <c>1.20.1 ~ 1.21</c>）；
        /// 读不到时为空串（光影 / 资源包 / 整合包通常为空）。
        /// </summary>
        public string GameVersions { get; init; } = "";

        /// <summary>
        /// 资源包 / 光影包 zip 里 <c>pack.mcmeta</c> 的格式号（如 <c>15</c>、<c>84 ~ 999</c>）。
        /// 这两类内容不是 jar，读不出模组版本，格式号是唯一能从文件本身拿到的版本线索。
        /// </summary>
        public string PackFormats { get; init; } = "";

        public string ShownName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
        public string StatusText => IsEnabled ? "已启用" : "已禁用";

        /// <summary>带 v 前缀的版本号，缺失时为空串（供 XAML 直接绑定）。</summary>
        public string VersionText => string.IsNullOrWhiteSpace(Version) ? "" : "v" + Version;

        public string DetailText
        {
            get
            {
                var extra = !string.IsNullOrWhiteSpace(DisplayName)
                            && !string.Equals(DisplayName, Name, StringComparison.OrdinalIgnoreCase)
                    ? Name + "  ·  "
                    : "";
                var meta = string.IsNullOrWhiteSpace(Version)
                    ? SizeText
                    : $"v{Version}  ·  {SizeText}";
                return IsLibraryItem ? extra + meta : extra + $"{meta}  ·  {StatusText}";
            }
        }
    }

    public sealed class LibraryPickItem : INotifyPropertyChanged
    {
        public InstanceModItem Item { get; init; } = new();
        public string Name => Item.ShownName;

        /// <summary>版本 + 体积（资源库条目没有启用状态）。</summary>
        public string DetailText => Item.DetailText;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
