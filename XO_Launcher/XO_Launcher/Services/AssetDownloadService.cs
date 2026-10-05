using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>下载 Mojang 资源索引中的 objects，以及 log4j 配置。缺资源时游戏窗口常会闪一下就退出。</summary>
    public static class AssetDownloadService
    {
        private const int MaxParallel = 16;

        public static async Task EnsureAsync(
            JsonElement versionRoot,
            string gameDir,
            IProgress<string>? progress = null,
            DownloadTask? task = null,
            CancellationToken ct = default)
        {
            var assetsDir = Path.Combine(gameDir, "assets");
            Directory.CreateDirectory(Path.Combine(assetsDir, "indexes"));
            Directory.CreateDirectory(Path.Combine(assetsDir, "objects"));
            Directory.CreateDirectory(Path.Combine(assetsDir, "log_configs"));

            if (versionRoot.TryGetProperty("logging", out var logging)
                && logging.TryGetProperty("client", out var client)
                && client.TryGetProperty("file", out var file)
                && file.TryGetProperty("url", out var logUrl)
                && file.TryGetProperty("id", out var logId))
            {
                var logPath = Path.Combine(assetsDir, "log_configs", logId.GetString() ?? "client.xml");
                await DownloadFileAsync(logUrl.GetString() ?? "", logPath, ct);
            }

            if (!versionRoot.TryGetProperty("assetIndex", out var assetIndex)
                || !assetIndex.TryGetProperty("url", out var indexUrl)
                || !assetIndex.TryGetProperty("id", out var indexId))
                return;

            var indexPath = Path.Combine(assetsDir, "indexes", indexId.GetString() + ".json");
            await DownloadFileAsync(indexUrl.GetString() ?? "", indexPath, ct);
            if (!File.Exists(indexPath)) return;

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, ct));
            if (!doc.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
                return;

            var pending = new List<(string Url, string Path)>();
            foreach (var obj in objects.EnumerateObject())
            {
                if (!obj.Value.TryGetProperty("hash", out var hashEl) || hashEl.GetString() is not { Length: >= 2 } hash)
                    continue;
                var dest = Path.Combine(assetsDir, "objects", hash[..2], hash);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
                pending.Add(($"https://resources.download.minecraft.net/{hash[..2]}/{hash}", dest));
            }

            if (pending.Count == 0) return;
            progress?.Report($"正在补齐游戏资源 0 / {pending.Count}");
            await DownloadAllAsync(pending, task, progress, ct);
        }

        public static string? Log4jArgument(JsonElement versionRoot, string gameDir)
        {
            if (!versionRoot.TryGetProperty("logging", out var logging)
                || !logging.TryGetProperty("client", out var client)
                || !client.TryGetProperty("file", out var file)
                || !file.TryGetProperty("id", out var logId))
                return null;
            var logPath = Path.Combine(gameDir, "assets", "log_configs", logId.GetString() ?? "client.xml");
            return File.Exists(logPath) ? $"-Dlog4j.configurationFile={logPath}" : null;
        }

        private static async Task DownloadAllAsync(
            List<(string Url, string Path)> pending,
            DownloadTask? task,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var total = pending.Count;
            var done = 0;
            using var gate = new SemaphoreSlim(MaxParallel);
            var errors = new List<Exception>();

            async Task One((string Url, string Path) file)
            {
                await gate.WaitAsync(ct);
                try
                {
                    await DownloadFileAsync(file.Url, file.Path, ct);
                    var current = Interlocked.Increment(ref done);
                    if (task is not null)
                    {
                        DownloadService.Instance.UpdateTask(task, t =>
                        {
                            t.Status = "下载中";
                            t.Detail = Path.GetFileName(file.Path);
                            t.Progress = Math.Clamp(20 + current * 70.0 / total, 0, 99);
                            t.ProgressHint = $"游戏资源  {current} / {total}";
                        });
                    }
                    if (current == 1 || current % 80 == 0 || current == total)
                        progress?.Report($"正在补齐游戏资源 {current} / {total}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    lock (errors) errors.Add(ex);
                }
                finally
                {
                    gate.Release();
                }
            }

            try
            {
                await Task.WhenAll(pending.Select(One));
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (errors.Count > 0 && done == 0)
                throw new InvalidOperationException("游戏资源下载失败：" + errors[0].Message);
        }

        private static Task DownloadFileAsync(string url, string destination, CancellationToken ct)
            => string.IsNullOrWhiteSpace(url)
                ? Task.CompletedTask
                : HttpDownloadHelper.DownloadFileAsync(url, destination, ct);
    }
}
