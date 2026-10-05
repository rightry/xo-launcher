using System;

namespace XO_Launcher.Models
{
    /// <summary>下载页中的基岩版条目：可并排安装的客户端、Store 当前版，或官方专用服务器。</summary>
    public sealed class BedrockCatalogItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Version { get; set; } = "";
        /// <summary>client / preview / server。</summary>
        public string Kind { get; set; } = "client";
        /// <summary>release / preview / beta。</summary>
        public string Channel { get; set; } = "release";
        public string? DownloadUrl { get; set; }
        /// <summary>Microsoft Store UpdateID，用于 FE3 协议解析 APPX 直链（BedrockBoot / mc-w10-version-launcher）。</summary>
        public string? UpdateId { get; set; }
        public string? Date { get; set; }
        public string Description { get; set; } = "";
        public string Badge { get; set; } = "";
        public string IconColor { get; set; } = "#3D7A3C";
    }

    /// <summary>本机已安装的 Minecraft 基岩版 UWP 包。</summary>
    public sealed class BedrockPackageInfo
    {
        public string FamilyName { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string Version { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public bool Preview { get; set; }

        public string Id => Preview ? BedrockIds.Preview : BedrockIds.Release;
    }

    public static class BedrockIds
    {
        public const string Release = "bedrock";
        public const string Preview = "bedrock-preview";
        public const string ReleaseFamily = "Microsoft.MinecraftUWP_8wekyb3d8bbwe";
        public const string PreviewFamily = "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe";
        public const string ReleaseStoreId = "9NBLGGH2JHXJ";
        public const string PreviewStoreId = "9P5X4QVLC2XR";
        public const string ReleasePackageName = "Microsoft.MinecraftUWP";
        public const string PreviewPackageName = "Microsoft.MinecraftWindowsBeta";
        // 商店版（打包）已安装时，解包注册必须换一个包标识才能共存：
        // Windows 禁止「未打包版本替换打包版本」。发布者不动 →
        // PublisherId 哈希仍是 8wekyb3d8bbwe，只是包系列名前缀不同。
        public const string SideloadReleaseIdentity = "XO.MinecraftUWP";
        public const string SideloadPreviewIdentity = "XO.MinecraftWindowsBeta";

        public static string ClientId(string version, bool preview)
            => preview ? Preview + "-" + version : Release + "-" + version;

        public static string ServerId(string version) => "bedrock-server-" + version;

        public static bool IsServer(string? id)
            => id is not null && id.StartsWith("bedrock-server", StringComparison.OrdinalIgnoreCase);

        public static bool IsPreviewChannel(string? id)
            => string.Equals(id, Preview, StringComparison.OrdinalIgnoreCase)
               || (id is not null && id.StartsWith(Preview + "-", StringComparison.OrdinalIgnoreCase));

        public static bool IsStoreClient(string? id)
            => string.Equals(id, Release, StringComparison.OrdinalIgnoreCase)
               || string.Equals(id, Preview, StringComparison.OrdinalIgnoreCase);

        public static bool IsSideloadClient(string? id)
            => IsClient(id) && !IsStoreClient(id) && !IsServer(id);

        public static bool IsClient(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || IsServer(id)) return false;
            return id.StartsWith("bedrock", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBedrock(string? loaderOrId)
            => IsClient(loaderOrId)
               || IsServer(loaderOrId)
               || string.Equals(loaderOrId, "bedrock-server", StringComparison.OrdinalIgnoreCase);

        public static string PackageNameFor(bool preview)
            => preview ? PreviewPackageName : ReleasePackageName;

        public static string FamilyFor(bool preview)
            => preview ? PreviewFamily : ReleaseFamily;

        /// <summary>与商店版共存安装时改用的独立包标识名。</summary>
        public static string SideloadIdentityFor(bool preview)
            => preview ? SideloadPreviewIdentity : SideloadReleaseIdentity;

        /// <summary>独立标识对应的包系列名（发布者未变，哈希沿用 8wekyb3d8bbwe）。</summary>
        public static string SideloadFamilyFor(bool preview)
            => SideloadIdentityFor(preview) + "_8wekyb3d8bbwe";
    }
}
