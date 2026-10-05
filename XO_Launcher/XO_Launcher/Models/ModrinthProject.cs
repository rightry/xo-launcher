using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace XO_Launcher.Models
{
    /// <summary>Modrinth API v2 搜索结果（GET /v2/search）。</summary>
    public class ModrinthSearchResponse
    {
        public List<ModrinthHit> Hits { get; set; } = new();
        public int Offset { get; set; }
        public int Limit { get; set; }
        [JsonPropertyName("total_hits")]
        public long TotalHits { get; set; }
    }

    /// <summary>Modrinth 搜索结果中的单个项目（Project）。</summary>
    public class ModrinthHit
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = "";

        [JsonPropertyName("project_type")]
        public string ProjectType { get; set; } = "";

        public string Slug { get; set; } = "";
        public string Author { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Categories { get; set; } = new();
        public long Downloads { get; set; }
        public long Follows { get; set; }

        /// <summary>
        /// 该项目支持的游戏版本（该字段只在搜索接口里叫 <c>versions</c>，
        /// 详情接口 <see cref="ModrinthProject.GameVersions"/> 才是 <c>game_versions</c>）。
        /// 注意值是**升序**的（旧 → 新）且可能含 rc/pre 预发布版本，展示前要走 <c>FormatHelper.SummarizeGameVersions</c>。
        /// </summary>
        [JsonPropertyName("versions")]
        public List<string> Versions { get; set; } = new();

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("date_created")]
        public string DateCreated { get; set; } = "";

        [JsonPropertyName("date_modified")]
        public string DateModified { get; set; } = "";

        public int? Color { get; set; }

        /// <summary>项目图库（搜索接口返回图片 URL 列表，实测已是 350px 的 WebP 缩略图）。</summary>
        public List<string>? Gallery { get; set; }
    }

    /// <summary>Modrinth 项目详情（GET /v2/project/{id}）。</summary>
    public class ModrinthProject
    {
        public string Id { get; set; } = "";
        public string Slug { get; set; } = "";

        [JsonPropertyName("project_type")]
        public string ProjectType { get; set; } = "";

        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Body { get; set; } = "";

        [JsonPropertyName("icon_url")]
        public string? IconUrl { get; set; }

        public long Downloads { get; set; }
        public long Followers { get; set; }
        public string Author { get; set; } = "";
        public List<string> Categories { get; set; } = new();

        [JsonPropertyName("game_versions")]
        public List<string> GameVersions { get; set; } = new();

        public List<string> Loaders { get; set; } = new();

        [JsonPropertyName("source_url")]
        public string SourceUrl { get; set; } = "";

        [JsonPropertyName("issues_url")]
        public string IssuesUrl { get; set; } = "";

        public string Published { get; set; } = "";
        public string Updated { get; set; } = "";
        public int? Color { get; set; }

        /// <summary>项目图库（详情接口返回对象列表，含 featured 标记）。</summary>
        public List<ModrinthGalleryImage> Gallery { get; set; } = new();

        /// <summary>
        /// 顶部背景图地址：优先精选图，否则取第一张。
        /// 详情页横幅不限制体积，故直接用原图（raw_url）以保证清晰度；
        /// 原图缺失时回落到 350px 的 url。
        /// </summary>
        public string CoverUrl
        {
            get
            {
                var pick = Gallery.FirstOrDefault(g => g.Featured) ?? Gallery.FirstOrDefault();
                if (pick is null) return "";
                return string.IsNullOrWhiteSpace(pick.RawUrl) ? pick.Url : pick.RawUrl;
            }
        }
    }

    /// <summary>Modrinth 项目图库中的一张图片。</summary>
    public class ModrinthGalleryImage
    {
        /// <summary>350px 的 WebP 缩略图。</summary>
        public string Url { get; set; } = "";

        /// <summary>原图；详情页横幅优先用它。</summary>
        [JsonPropertyName("raw_url")]
        public string RawUrl { get; set; } = "";

        public bool Featured { get; set; }
        public string Title { get; set; } = "";
    }

    /// <summary>Modrinth 项目版本（GET /v2/project/{id}/version）。</summary>
    public class ModrinthVersion
    {
        public string Id { get; set; } = "";

        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = "";

        public string Name { get; set; } = "";

        [JsonPropertyName("version_number")]
        public string VersionNumber { get; set; } = "";

        [JsonPropertyName("version_type")]
        public string VersionType { get; set; } = "";

        public string Status { get; set; } = "";
        public string Changelog { get; set; } = "";

        [JsonPropertyName("date_published")]
        public string DatePublished { get; set; } = "";

        public long Downloads { get; set; }
        public bool Featured { get; set; }

        [JsonPropertyName("game_versions")]
        public List<string> GameVersions { get; set; } = new();

        public List<string> Loaders { get; set; } = new();
        public List<ModrinthVersionFile> Files { get; set; } = new();

        /// <summary>该版本声明的依赖（前置模组）。CurseForge 合成的版本此列表为空。</summary>
        public List<ModrinthDependency> Dependencies { get; set; } = new();

        /// <summary>是否来自 Modrinth（CurseForge 合成版本置 false，用于隐藏「一键安装含前置」）。</summary>
        [JsonIgnore]
        public bool IsModrinthSource { get; set; } = true;

        /// <summary>主要下载文件（标记 primary，否则取第一个）。</summary>
        public ModrinthVersionFile? PrimaryFile =>
            Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();

        /// <summary>必需前置（dependency_type == required）数量。</summary>
        public int RequiredDependencyCount => Dependencies.Count(d => d.IsRequired);

        public bool HasRequiredDependencies => RequiredDependencyCount > 0;

        public string RequiredDependencyBadge => "前置 " + RequiredDependencyCount;

        public string LoaderText => string.Join(" · ", Loaders);
        public string FormattedDate =>
            System.DateTimeOffset.TryParse(DatePublished, out var d) ? d.ToString("yyyy-MM-dd") : "";
        public string FormattedSize => PrimaryFile is null ? "未知大小" : FormatHelper.FormatBytes(PrimaryFile.Size);
        public string FormattedDownloads => FormatHelper.FormatCompactNumber(Downloads);

        public bool IsAlpha =>
            string.Equals(VersionType, "alpha", System.StringComparison.OrdinalIgnoreCase)
            || VersionNumber.Contains("alpha", System.StringComparison.OrdinalIgnoreCase)
            || Name.Contains("alpha", System.StringComparison.OrdinalIgnoreCase);

        public string VersionTypeLabel
        {
            get
            {
                if (IsAlpha) return "Alpha";
                return VersionType.Trim().ToLowerInvariant() switch
                {
                    "beta" => "Beta",
                    "release" => "Release",
                    "" => "",
                    _ => char.ToUpperInvariant(VersionType[0]) + VersionType[1..]
                };
            }
        }
    }

    /// <summary>Modrinth 版本中的下载文件。</summary>
    public class ModrinthVersionFile
    {
        public Dictionary<string, string> Hashes { get; set; } = new();
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
        [JsonPropertyName("filename")]
        public string Filename { get; set; } = "";
        public bool Primary { get; set; }
        public long Size { get; set; }
    }

    /// <summary>Modrinth 版本的单条依赖（前置模组）。</summary>
    public class ModrinthDependency
    {
        [JsonPropertyName("version_id")]
        public string? VersionId { get; set; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        /// <summary>required / optional / incompatible / embedded。</summary>
        [JsonPropertyName("dependency_type")]
        public string DependencyType { get; set; } = "";

        /// <summary>仅必需依赖需要一并下载；embedded 已内置、optional 可选、incompatible 冲突都跳过。</summary>
        public bool IsRequired =>
            string.Equals(DependencyType, "required", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>详情页内某一加载器下的文件列表。</summary>
    public class LoaderVersionGroup
    {
        public string LoaderId { get; set; } = "";
        public string LoaderName { get; set; } = "";
        public List<ModrinthVersion> Versions { get; set; } = new();
        public bool IsExpanded { get; set; }
        public string VersionCountText => $"{Versions.Count} 个文件";
    }

    /// <summary>详情页按 Minecraft 版本分组，其下再按模组加载器分栏。</summary>
    public class McVersionGroup
    {
        public string MinecraftVersion { get; set; } = "";
        public List<LoaderVersionGroup> Loaders { get; set; } = new();
        public bool IsExpanded { get; set; }
        public string LoaderSummary => string.Join(" / ", Loaders.Select(l => l.LoaderName));
        public string VersionCountText => $"{Loaders.Sum(l => l.Versions.Count)} 个文件 · {Loaders.Count} 种加载器";
    }

    /// <summary>设置页开源致谢卡片模型。</summary>
    public class CreditItem
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconText { get; set; } = "";
        public string IconColor { get; set; } = "#2D2D2D";
        /// <summary>项目真实图标（本地 Assets/credits 下的 PNG/ICO/SVG），为空时回退到 IconText 字母占位。</summary>
        public Microsoft.UI.Xaml.Media.ImageSource? IconSource { get; set; }
        /// <summary>图标底色；已有真实图标时返回 null（不绘制背景色）。</summary>
        public Microsoft.UI.Xaml.Media.Brush? IconBadgeBrush
        {
            get
            {
                if (IconSource is not null) return null;
                var hex = IconColor?.TrimStart('#') ?? "";
                if (hex.Length == 6
                    && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
                    && byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                    && byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
                }
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 45, 45));
            }
        }
        public string Url { get; set; } = "";
        public string RepoText { get; set; } = "";
        public System.Uri? GitHubUri =>
            System.Uri.TryCreate(Url, System.UriKind.Absolute, out var uri) ? uri : null;
    }
}
