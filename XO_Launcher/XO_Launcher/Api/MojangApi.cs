using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;
using XO_Launcher.Services;

namespace XO_Launcher.Api
{
    public sealed class MojangApi
    {
        private const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public async Task<MojangVersionManifest> GetVersionManifestAsync(CancellationToken cancellationToken = default)
        {
            // 按下载镜像策略依次尝试官方与 BMCLAPI 镜像
            Exception? last = null;
            foreach (var url in HttpDownloadHelper.ExpandUrls(ManifestUrl))
            {
                try
                {
                    using var response = await Client.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonSerializer.DeserializeAsync<MojangVersionManifest>(stream, JsonOptions, cancellationToken)
                           ?? new MojangVersionManifest();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw last ?? new InvalidOperationException("获取版本清单失败");
        }
    }
}
