using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Api
{
    /// <summary>通过 MCIM 镜像访问 CurseForge（无需填写 API 密钥）。</summary>
    public sealed class CurseForgeApi
    {
        private const string McimRoot = "https://mod.mcimirror.top/curseforge";
        public const int PageSize = 50;

        private static readonly HttpClient Client = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher UI)");
            return client;
        }

        public async Task<CurseForgeSearchResponse> SearchAsync(
            string projectType,
            string? query,
            string sort,
            int? categoryId,
            int index,
            CancellationToken cancellationToken)
        {
            var classId = ResourceCatalog.CurseForgeClassId(projectType);
            var sortField = string.Equals(sort, "relevance", StringComparison.OrdinalIgnoreCase) ? 2 : 6;
            var path = $"/v1/mods/search?gameId=432&classId={classId}&index={Math.Max(0, index)}&pageSize={PageSize}"
                       + $"&sortField={sortField}&sortOrder=desc";
            if (!string.IsNullOrWhiteSpace(query))
                path += "&searchFilter=" + Uri.EscapeDataString(query.Trim());
            if (categoryId is > 0)
                path += "&categoryId=" + categoryId.Value;
            return await GetJsonAsync<CurseForgeSearchResponse>(path, cancellationToken) ?? new CurseForgeSearchResponse();
        }

        public async Task<CurseForgeMod?> GetModAsync(int modId, CancellationToken cancellationToken)
        {
            var wrap = await GetJsonAsync<CurseForgeData<CurseForgeMod>>($"/v1/mods/{modId}", cancellationToken);
            return wrap?.Data;
        }

        public async Task<IReadOnlyList<CurseForgeFile>> GetFilesAsync(int modId, CancellationToken cancellationToken)
        {
            var list = new List<CurseForgeFile>();
            for (var index = 0; index < 200 && !cancellationToken.IsCancellationRequested; index += PageSize)
            {
                var path = $"/v1/mods/{modId}/files?index={index}&pageSize={PageSize}";
                var page = await GetJsonAsync<CurseForgeSearchFilesResponse>(path, cancellationToken);
                var batch = page?.Data ?? new List<CurseForgeFile>();
                list.AddRange(batch.Where(f => f.IsAvailable && !string.IsNullOrWhiteSpace(f.FileName)));
                if (batch.Count < PageSize) break;
            }
            return list;
        }

        public static string ResolveDownloadUrl(int modId, CurseForgeFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.DownloadUrl))
                return file.DownloadUrl;
            return $"{McimRoot}/v1/mods/{modId}/files/{file.Id}/download";
        }

        public static ModrinthVersion ToVersion(int modId, CurseForgeFile file)
        {
            var tags = file.GameVersions ?? new List<string>();
            var games = tags.Where(IsMinecraftVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var loaders = tags.Select(ToLoaderId).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (loaders.Count == 0) loaders.Add("其他加载器");
            if (games.Count == 0) games.Add("其他版本");
            var url = ResolveDownloadUrl(modId, file);
            return new ModrinthVersion
            {
                Id = file.Id.ToString(),
                ProjectId = modId.ToString(),
                Name = file.DisplayName,
                VersionNumber = string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName,
                VersionType = file.ReleaseType switch { 2 => "beta", 3 => "alpha", _ => "release" },
                Status = "listed",
                DatePublished = file.FileDate,
                Downloads = file.DownloadCount,
                GameVersions = games,
                Loaders = loaders,
                IsModrinthSource = false,
                Files =
                {
                    new ModrinthVersionFile
                    {
                        Url = url,
                        Filename = file.FileName,
                        Primary = true,
                        Size = file.FileLength
                    }
                }
            };
        }

        private static bool IsMinecraftVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value[0] is >= '0' and <= '9' && value.Contains('.', StringComparison.Ordinal);
        }

        private static string ToLoaderId(string tag)
        {
            var t = tag.Trim().ToLowerInvariant();
            return t switch
            {
                "fabric" => "fabric",
                "forge" => "forge",
                "neoforge" => "neoforge",
                "quilt" => "quilt",
                "rift" => "rift",
                "liteloader" => "liteloader",
                _ => ""
            };
        }

        private static async Task<T?> GetJsonAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        {
            var url = McimRoot + pathAndQuery;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await Client.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} @ {url}");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
    }

    public sealed class CurseForgeData<T>
    {
        public T? Data { get; set; }
    }

    public sealed class CurseForgeSearchResponse
    {
        public List<CurseForgeMod> Data { get; set; } = new();
        public CurseForgePagination? Pagination { get; set; }
    }

    public sealed class CurseForgeSearchFilesResponse
    {
        public List<CurseForgeFile> Data { get; set; } = new();
        public CurseForgePagination? Pagination { get; set; }
    }

    public sealed class CurseForgePagination
    {
        public int Index { get; set; }
        public int PageSize { get; set; }
        public int ResultCount { get; set; }
        public int TotalCount { get; set; }
    }

    public sealed class CurseForgeMod
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Summary { get; set; } = "";
        public long DownloadCount { get; set; }
        public int ClassId { get; set; }
        public DateTimeOffset DateModified { get; set; }
        public List<CurseForgeAuthor> Authors { get; set; } = new();
        public List<CurseForgeCategory> Categories { get; set; } = new();
        public CurseForgeAsset? Logo { get; set; }
        public List<CurseForgeAsset> Screenshots { get; set; } = new();
        public CurseForgeLinks? Links { get; set; }

        /// <summary>
        /// 该项目最近的若干文件。搜索接口不直接给项目的游戏版本，
        /// 但每个文件都带完整的 <see cref="CurseForgeFile.GameVersions"/>，列表里据此标记适配的游戏版本。
        /// </summary>
        public List<CurseForgeFile> LatestFiles { get; set; } = new();

        public string AuthorName => Authors.FirstOrDefault()?.Name ?? "";

        /// <summary>顶部背景图：详情页横幅不限体积，优先取原图，缺失时回落缩略图。</summary>
        public string CoverUrl
        {
            get
            {
                var pick = Screenshots.FirstOrDefault();
                if (pick is null) return "";
                return !string.IsNullOrWhiteSpace(pick.Url) ? pick.Url : pick.ThumbnailUrl ?? "";
            }
        }
    }

    public sealed class CurseForgeAuthor
    {
        public string Name { get; set; } = "";
    }

    public sealed class CurseForgeCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class CurseForgeAsset
    {
        public string? ThumbnailUrl { get; set; }
        public string? Url { get; set; }
    }

    public sealed class CurseForgeLinks
    {
        public string? WebsiteUrl { get; set; }
    }

    public sealed class CurseForgeFile
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileDate { get; set; } = "";
        public long FileLength { get; set; }
        public long DownloadCount { get; set; }
        public string? DownloadUrl { get; set; }
        public int ReleaseType { get; set; }
        public bool IsAvailable { get; set; } = true;
        public List<string> GameVersions { get; set; } = new();
    }
}
