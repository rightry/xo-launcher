using System;

namespace XO_Launcher.Models
{
    /// <summary>启动器保存的游戏账号：微软正版或离线。</summary>
    public sealed class GameAccount
    {
        public const string TypeMicrosoft = "microsoft";
        public const string TypeOffline = "offline";

        // 皮肤来源：mojang（账号服务器，限 Microsoft）/ local（本地 PNG）/ url（远端 URL）/ default（Steve/Alex 占位）
        public const string SkinSourceMojang = "mojang";
        public const string SkinSourceLocal = "local";
        public const string SkinSourceUrl = "url";
        public const string SkinSourceDefault = "default";

        // 模型变体：classic（Steve / 4 像素手臂）/ slim（Alex / 3 像素手臂）
        public const string SkinVariantClassic = "classic";
        public const string SkinVariantSlim = "slim";

        /// <summary>本地账号 Id；微软账号使用角色 UUID（无连字符）。</summary>
        public string Id { get; set; } = "";

        /// <summary><see cref="TypeMicrosoft"/> 或 <see cref="TypeOffline"/>。</summary>
        public string Type { get; set; } = TypeOffline;

        public string Username { get; set; } = "Player";

        /// <summary>无连字符 UUID。</summary>
        public string Uuid { get; set; } = "";

        /// <summary>Minecraft 访问令牌；离线账号为 0。</summary>
        public string AccessToken { get; set; } = "0";

        /// <summary>微软 OAuth 刷新令牌，仅正版账号使用。</summary>
        public string? RefreshToken { get; set; }

        public string Xuid { get; set; } = "0";

        public DateTimeOffset? AccessTokenExpiry { get; set; }

        public string? SkinUrl { get; set; }

        /// <summary>本地皮肤绝对路径（当 SkinSource == local 时使用，文件由启动器写入 SkinsDirectory）。</summary>
        public string? LocalSkinPath { get; set; }

        /// <summary>皮肤来源：<see cref="SkinSourceMojang"/> 等。缺省为 mojang（保持旧账号行为）。</summary>
        public string SkinSource { get; set; } = SkinSourceMojang;

        /// <summary>模型变体：<see cref="SkinVariantClassic"/> 或 <see cref="SkinVariantSlim"/>。</summary>
        public string SkinVariant { get; set; } = SkinVariantClassic;

        public bool IsMicrosoft => string.Equals(Type, TypeMicrosoft, StringComparison.OrdinalIgnoreCase);

        public string TypeLabel => IsMicrosoft ? "微软正版" : "离线模式";

        public string UuidHyphenated => FormatUuid(Uuid);

        /// <summary>是否使用 slim（Alex）模型；false = classic（Steve）。</summary>
        public bool IsSlimModel => string.Equals(SkinVariant, SkinVariantSlim, StringComparison.OrdinalIgnoreCase);

        /// <summary>皮肤来源的中文名。离线账号没有官方皮肤服务器，所以 mojang 一档要单独说明。</summary>
        public string SkinSourceLabel => SkinSource switch
        {
            SkinSourceLocal => "本地 PNG",
            SkinSourceUrl => "远端 URL",
            SkinSourceDefault => "默认 Steve/Alex",
            _ => IsMicrosoft ? "Mojang 官方" : "Mojang 官方（离线账号不可用）"
        };

        public LaunchSession ToLaunchSession(string clientId) => new()
        {
            PlayerName = string.IsNullOrWhiteSpace(Username) ? "Player" : Username,
            Uuid = UuidHyphenated,
            AccessToken = string.IsNullOrWhiteSpace(AccessToken) ? "0" : AccessToken,
            UserType = IsMicrosoft ? "msa" : "legacy",
            Xuid = string.IsNullOrWhiteSpace(Xuid) ? "0" : Xuid,
            ClientId = clientId
        };

        public static string FormatUuid(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "00000000-0000-0000-0000-000000000000";
            var hex = raw.Replace("-", "", StringComparison.Ordinal).Trim();
            if (hex.Length != 32) return raw.Trim();
            return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        }
    }

    /// <summary>启动游戏所需的认证参数。</summary>
    public sealed class LaunchSession
    {
        public string PlayerName { get; init; } = "Player";
        public string Uuid { get; init; } = "00000000-0000-0000-0000-000000000000";
        public string AccessToken { get; init; } = "0";
        public string UserType { get; init; } = "legacy";
        public string Xuid { get; init; } = "0";
        public string ClientId { get; init; } = "";
    }
}
