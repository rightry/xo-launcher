using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Api
{
    public sealed class ModrinthApi
    {
        private const string OfficialRoot = "https://api.modrinth.com";
        private static readonly HttpClient Client = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher UI)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        /// <summary>Modrinth 单次最多返回 100 条。用 offset 分页即可列出该分类下的全部项目。</summary>
        public const int MaxPageSize = 100;

        public async Task<ModrinthSearchResponse> SearchAsync(
            string projectType,
            string? query = null,
            int offset = 0,
            int limit = MaxPageSize,
            string sort = "downloads",
            string? category = null,
            CancellationToken cancellationToken = default)
        {
            limit = Math.Clamp(limit, 1, MaxPageSize);
            offset = Math.Max(0, offset);
            var index = string.Equals(sort, "relevance", StringComparison.OrdinalIgnoreCase) ? "relevance" : "downloads";
            var facetParts = new List<string> { $"[\"project_type:{projectType}\"]" };
            if (!string.IsNullOrWhiteSpace(category))
                facetParts.Add($"[\"categories:{category}\"]");
            var facets = Uri.EscapeDataString("[" + string.Join(",", facetParts) + "]");
            var encodedQuery = Uri.EscapeDataString(query?.Trim() ?? "");
            var path = $"/v2/search?facets={facets}&query={encodedQuery}&limit={limit}&offset={offset}&index={index}";
            return await GetJsonAsync<ModrinthSearchResponse>(path, cancellationToken) ?? new ModrinthSearchResponse();
        }

        /// <summary>获取项目详情（GET /v2/project/{id|slug}）。直连官方 API。</summary>
        public async Task<ModrinthProject?> GetProjectAsync(
            string idOrSlug,
            CancellationToken cancellationToken = default)
        {
            var path = $"/v2/project/{Uri.EscapeDataString(idOrSlug)}";
            return await GetJsonAsync<ModrinthProject>(path, cancellationToken);
        }

        /// <summary>获取项目的全部版本（GET /v2/project/{id|slug}/version）。直连官方 API。</summary>
        public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
            string idOrSlug,
            CancellationToken cancellationToken = default)
        {
            var path = $"/v2/project/{Uri.EscapeDataString(idOrSlug)}/version";
            return await GetJsonAsync<List<ModrinthVersion>>(path, cancellationToken) ?? new List<ModrinthVersion>();
        }

        /// <summary>获取单个版本详情（GET /v2/version/{id}）。用于按依赖里的 version_id 精确取前置。</summary>
        public async Task<ModrinthVersion?> GetVersionAsync(
            string versionId,
            CancellationToken cancellationToken = default)
        {
            var path = $"/v2/version/{Uri.EscapeDataString(versionId)}";
            return await GetJsonAsync<ModrinthVersion>(path, cancellationToken);
        }

        /// <summary>按游戏版本与加载器筛选，返回最新的兼容版本（优先正式版）。</summary>
        public async Task<ModrinthVersion?> GetLatestCompatibleAsync(
            string idOrSlug,
            string gameVersion,
            string loader,
            CancellationToken cancellationToken = default)
        {
            var gameVersions = Uri.EscapeDataString($"[\"{gameVersion}\"]");
            var loaders = Uri.EscapeDataString($"[\"{loader}\"]");
            var path = $"/v2/project/{Uri.EscapeDataString(idOrSlug)}/version?game_versions={gameVersions}&loaders={loaders}";
            var versions = await GetJsonAsync<List<ModrinthVersion>>(path, cancellationToken) ?? new List<ModrinthVersion>();
            return versions
                .Where(v => v.PrimaryFile is not null && !string.IsNullOrWhiteSpace(v.PrimaryFile.Url))
                .OrderBy(v => v.VersionType.ToLowerInvariant() switch
                {
                    "release" => 0,
                    "beta" => 1,
                    _ => 2
                })
                .ThenByDescending(v => DateTimeOffset.TryParse(v.DatePublished, out var date) ? date : DateTimeOffset.MinValue)
                .FirstOrDefault();
        }

        /// <summary>
        /// 按文件 sha1 批量反查版本（POST /v2/version_files）。
        /// 返回 hash → 版本 的映射；查不到的 hash 不会出现在结果里。
        /// 导出整合包时用它把本地 mod jar 精确对应回 Modrinth 上的可下载版本，
        /// 比按模组 id / slug 猜要可靠得多（很多模组的 id 与 slug 并不一致）。
        /// </summary>
        public async Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
            IEnumerable<string> sha1Hashes,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
            var hashes = sha1Hashes
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hashes.Count == 0) return result;

            // Modrinth 单次上限较大，这里保守分批 100 个，避免超长请求体。
            const int batchSize = 100;
            for (var i = 0; i < hashes.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = hashes.Skip(i).Take(batchSize).ToList();
                var payload = JsonSerializer.Serialize(new { hashes = slice, algorithm = "sha1" });
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await Client.PostAsync(OfficialRoot + "/v2/version_files", content, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var map = await JsonSerializer.DeserializeAsync<Dictionary<string, ModrinthVersion>>(
                    stream, JsonOptions, cancellationToken);
                if (map is null) continue;
                foreach (var kv in map)
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        private static async Task<T?> GetJsonAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
        {
            var url = OfficialRoot + pathAndQuery;
            using var response = await Client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
    }
}
