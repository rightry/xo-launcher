using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Api
{
    /// <summary>
    /// 基岩版目录：历史客户端版本库（对齐 BedrockBoot / mc-w10-versiondb）+ Mojang 官方专用服务器。
    /// 客户端 APPX 仍需已购买的微软账号；安装走开发者模式注册，与 BedrockBoot 相同。
    /// </summary>
    public sealed class BedrockApi
    {
        private const string PatchNotesUrl = "https://launchercontent.mojang.com/bedrockPatchNotes.json";
        private const string DownloadLinksUrl = "https://net-secondary.web.minecraft-services.net/api/v1.0/download/links";
        private const string VersionDbUrl =
            "https://raw.githubusercontent.com/ddf8196/mc-w10-versiondb-auto-update/master/versions.json.min";

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XO-Launcher/1.0 (Minecraft launcher)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        public async Task<IReadOnlyList<BedrockCatalogItem>> GetCatalogAsync(CancellationToken cancellationToken = default)
        {
            var notesTask = GetLatestPatchAsync(cancellationToken);
            var versionsTask = GetClientVersionsAsync(cancellationToken);
            var serversTask = GetServerBuildsSafeAsync(cancellationToken);
            await Task.WhenAll(notesTask, versionsTask, serversTask);

            var (retail, preview, retailDate, previewDate) = notesTask.Result;
            var items = new List<BedrockCatalogItem>();
            var versions = versionsTask.Result;
            if (versions.Count > 0)
            {
                items.AddRange(versions);
            }
            else
            {
                items.Add(StorePlaceholder(false, retail, retailDate));
                items.Add(StorePlaceholder(true, preview, previewDate));
            }

            items.AddRange(serversTask.Result);
            return items;
        }

        private static BedrockCatalogItem StorePlaceholder(bool preview, string? version, string? date)
            => new()
            {
                Id = preview ? BedrockIds.Preview : BedrockIds.Release,
                Title = preview ? "Minecraft Preview（Store）" : "Minecraft for Windows（Store）",
                Version = version ?? "",
                Kind = preview ? "preview" : "client",
                Channel = preview ? "preview" : "release",
                Date = date,
                Badge = preview ? "游戏 · Store 预览" : "游戏 · Store",
                IconColor = preview ? "#496A88" : "#3D7A3C",
                Description = preview
                    ? "Microsoft Store 预览通道。未从版本库解析到历史版本时作为兜底入口。"
                    : "通过 Microsoft Store 安装当前正版客户端。未从版本库解析到历史版本时作为兜底入口。"
            };

        private static async Task<IReadOnlyList<BedrockCatalogItem>> GetClientVersionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Client.GetAsync(VersionDbUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Array.Empty<BedrockCatalogItem>();

                var items = new List<BedrockCatalogItem>();
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 3)
                        continue;
                    var version = entry[0].GetString();
                    var updateId = entry[1].GetString();
                    if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(updateId))
                        continue;
                    var type = entry[2].ValueKind == JsonValueKind.Number ? entry[2].GetInt32() : 0;
                    var preview = type != 0;
                    items.Add(new BedrockCatalogItem
                    {
                        Id = BedrockIds.ClientId(version, preview),
                        Title = preview ? version + " 预览" : version,
                        Version = version,
                        Kind = preview ? "preview" : "client",
                        Channel = type == 1 ? "beta" : preview ? "preview" : "release",
                        UpdateId = updateId,
                        Badge = type == 1 ? "游戏 · Beta" : preview ? "游戏 · 预览版" : "游戏 · 正式版",
                        IconColor = preview ? "#496A88" : "#3D7A3C",
                        Description = preview
                            ? $"Windows 基岩预览 {version}。按 BedrockBoot 方式下载 APPX 并注册到本机，可与其它版本并排存放，启动时切换。"
                            : $"Windows 基岩版 {version}。按 BedrockBoot 方式下载 APPX 并注册到本机，可与其它版本并排存放，启动时切换。"
                    });
                }

                return items
                    .OrderBy(i => i.Kind == "preview" ? 1 : 0)
                    .ThenByDescending(i => i.Version, Comparer<string>.Create(FormatHelper.CompareMcVersion))
                    .ToList();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Array.Empty<BedrockCatalogItem>();
            }
        }

        private static async Task<(string? Retail, string? Preview, string? RetailDate, string? PreviewDate)> GetLatestPatchAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Client.GetAsync(PatchNotesUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return (null, null, null, null);

                string? retail = null, preview = null, retailDate = null, previewDate = null;
                foreach (var entry in entries.EnumerateArray())
                {
                    var version = entry.TryGetProperty("version", out var v) ? v.GetString() : null;
                    if (string.IsNullOrWhiteSpace(version)) continue;
                    var type = entry.TryGetProperty("patchNoteType", out var t) ? t.GetString() ?? "" : "";
                    var date = entry.TryGetProperty("date", out var d) ? d.GetString() : null;
                    var isPreview = type.Contains("beta", StringComparison.OrdinalIgnoreCase)
                                    || type.Contains("preview", StringComparison.OrdinalIgnoreCase);
                    if (isPreview)
                    {
                        preview ??= version;
                        previewDate ??= date;
                    }
                    else
                    {
                        retail ??= version;
                        retailDate ??= date;
                    }
                    if (retail is not null && preview is not null) break;
                }
                return (retail, preview, retailDate, previewDate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return (null, null, null, null);
            }
        }

        private static async Task<IReadOnlyList<BedrockCatalogItem>> GetServerBuildsSafeAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await GetServerBuildsAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Array.Empty<BedrockCatalogItem>();
            }
        }

        private static async Task<IReadOnlyList<BedrockCatalogItem>> GetServerBuildsAsync(CancellationToken cancellationToken)
        {
            using var response = await Client.GetAsync(DownloadLinksUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("links", out var links)
                || links.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<BedrockCatalogItem>();
            }

            var items = new List<BedrockCatalogItem>();
            foreach (var link in links.EnumerateArray())
            {
                var type = link.TryGetProperty("downloadType", out var t) ? t.GetString() ?? "" : "";
                var url = link.TryGetProperty("downloadUrl", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var preview = type.Contains("Preview", StringComparison.OrdinalIgnoreCase);
                if (!type.Equals("serverBedrockWindows", StringComparison.OrdinalIgnoreCase)
                    && !type.Equals("serverBedrockPreviewWindows", StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = ExtractServerVersion(url);
                if (string.IsNullOrWhiteSpace(version)) continue;

                items.Add(new BedrockCatalogItem
                {
                    Id = BedrockIds.ServerId(version),
                    Title = preview ? "基岩专用服务器（预览）" : "基岩专用服务器",
                    Version = version,
                    Kind = "server",
                    Channel = preview ? "preview" : "release",
                    DownloadUrl = url,
                    Badge = preview ? "服务器 · 预览版" : "服务器 · 正式版",
                    IconColor = preview ? "#8A6A3A" : "#6B4F3A",
                    Description = $"Mojang 官方 Windows 专用服务器 {version}。下载后可在启动器内直接运行 bedrock_server.exe。"
                });
            }

            return items
                .OrderBy(i => i.Channel == "preview" ? 1 : 0)
                .ThenByDescending(i => i.Version, Comparer<string>.Create(FormatHelper.CompareMcVersion))
                .ToList();
        }

        private static string? ExtractServerVersion(string url)
        {
            var match = Regex.Match(url, @"bedrock-server-([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
