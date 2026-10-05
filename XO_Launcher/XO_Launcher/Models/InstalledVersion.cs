using System.IO;

namespace XO_Launcher.Models
{
    /// <summary>游戏目录 versions 下已安装的版本（原版与加载器共用同一套列表）。</summary>
    public sealed class InstalledVersion
    {
        public string Id { get; set; } = "";
        public string MinecraftVersion { get; set; } = "";
        public string LoaderId { get; set; } = "vanilla";
        public string LoaderVersion { get; set; } = "";
        public string Type { get; set; } = "";
        public string JsonPath { get; set; } = "";
        public string? InheritsFrom { get; set; }
        public string? CustomName { get; set; }
        /// <summary>版本设置里选择的自定义图标文件名（null = 按类型自动）。</summary>
        public string? CustomIcon { get; set; }

        public string IconPath => Services.VersionIconService.GetIconPath(
            CustomIcon, LoaderId, Type, Path.GetDirectoryName(JsonPath));

        public bool IsBedrock => BedrockIds.IsBedrock(LoaderId);

        public string LoaderLabel
        {
            get
            {
                if (LoaderId == "vanilla")
                    return Type switch
                    {
                        "snapshot" => "原版 · 快照",
                        "old_alpha" => "原版 · Alpha",
                        "old_beta" => "原版 · Beta",
                        _ => "原版"
                    };
                if (IsBedrock)
                    return LoaderId switch
                    {
                        "bedrock-preview" => "基岩预览版",
                        "bedrock-server" => string.IsNullOrWhiteSpace(LoaderVersion)
                            ? "基岩服务器"
                            : $"基岩服务器 {LoaderVersion}",
                        _ => "基岩版"
                    };
                var name = FormatHelper.FormatLoaderName(LoaderId);
                return string.IsNullOrWhiteSpace(LoaderVersion) ? name : $"{name} {LoaderVersion}";
            }
        }

        public string DisplayName => !string.IsNullOrWhiteSpace(CustomName)
            ? CustomName
            : LoaderId == "vanilla"
                ? MinecraftVersion
                : IsBedrock
                    ? $"{LoaderLabel}  ·  {MinecraftVersion}"
                    : $"{MinecraftVersion}  ·  {LoaderLabel}";
    }
}
