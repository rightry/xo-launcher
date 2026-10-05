using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 多源 swarm 下载（类似 P2P）：同时从官方与镜像拉取不同分片，
    /// 慢源/失败源自动降权，卡住的分片换源重试，直到整文件完成。
    /// </summary>
    public static class HttpDownloadHelper
    {
        public const int FileParallelism = 12;
        private const int BufferSize = 81920;
        private const int MinSwarmSize = 8 * 1024 * 1024;
        private const int DefaultChunkSize = 512 * 1024;
        private const int MaxWorkers = 12;
        private const int MaxPerSource = 4;
        private const int ProbeTimeoutMs = 8000;
        private const int StallTimeoutMs = 180000;
        // 小文件（≤8MB，例如 Quilt installer ~1MB）不需要 3 分钟的停滞容忍：
        // 黑洞连接（TCP/TLS 正常、之后不再发数据）在读上就表现为长期停滞，
        // 3 分钟 × 4 次尝试 = 单文件最坏卡 12 分钟，这正是「Quilt 下载卡住」的量化根因。
        private const int SmallFileStallTimeoutMs = 30000;
        private const long SmallFileSizeThreshold = 8 * 1024 * 1024;

        private static readonly HttpClient Client = CreateClient();

        public static HttpClient Shared => Client;

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 16,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher; +https://github.com/)");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            return client;
        }

        /// <summary>
        /// 按下载镜像策略返回候选源列表（官方 + BMCLAPI / MCIMIRROR 镜像）。
        /// auto=官方在前多源混用；bmclapi=镜像在前；bmclapi_only=仅镜像；official=仅官方。
        /// </summary>
        public static IReadOnlyList<string> ExpandUrls(string url)
        {
            var official = new List<string>();
            var mirrors = new List<string>();
            void AddOfficial(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (!official.Contains(value, StringComparer.OrdinalIgnoreCase))
                    official.Add(value);
            }
            void AddMirror(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (!mirrors.Contains(value, StringComparer.OrdinalIgnoreCase))
                    mirrors.Add(value);
            }
            void AddBmcl(string suffix)
            {
                AddMirror("https://bmclapi2.bangbang93.com" + suffix);
                AddMirror("https://bmclapi.bangbang93.com" + suffix);
            }

            AddOfficial(url);
            try
            {
                var uri = new Uri(url);
                var host = uri.Host;
                var path = uri.PathAndQuery;

                if (host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase)
                         || host.Equals("maven.fabricmc.net", StringComparison.OrdinalIgnoreCase)
                         || host.Equals("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
                         || host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
                         || host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
                         || host.Equals("libraries.neoforged.net", StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = path;
                    if (host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
                        && suffix.StartsWith("/maven", StringComparison.OrdinalIgnoreCase))
                        suffix = suffix["/maven".Length..];
                    // maven.neoforged.net 的 releases/ 与 snapshots/ 是仓库名，
                    // 不属于制品路径，镜像侧没有这两段（带上会 404）。
                    if (host.Equals("maven.neoforged.net", StringComparison.OrdinalIgnoreCase))
                        suffix = StripNeoForgeRepository(suffix);
                    if (!suffix.StartsWith('/')) suffix = "/" + suffix;
                    AddBmcl("/maven" + suffix);
                }
                else if (host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
                {
                    AddBmcl("/assets" + path);
                }
                else if (host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
                {
                    // BMCLAPI 把 fabric-meta / quilt-meta 整套挂在自己的路径前缀下，
                    // 元数据 JSON 走 CDN 比直连官方快一个量级（国内直连经常十几秒）。
                    AddBmcl("/fabric-meta" + path);
                }
                else if (host.Equals("meta.quiltmc.org", StringComparison.OrdinalIgnoreCase))
                {
                    AddBmcl("/quilt-meta" + path);
                }
                else if (host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
                {
                    // MCIM（mod.mcimirror.top）在根路径原样反代 Modrinth CDN：实测同一文件的
                    // ETag、Content-Length、Last-Modified 与首尾各 8KB 都和官方逐字节一致，
                    // Range 请求返回 206 + 正确 Content-Range，可以直接参与分片下载。
                    // BMCLAPI 不镜像 Modrinth（对应路径一律 404），所以这里只有 MCIM 一个镜像。
                    AddMirror("https://mod.mcimirror.top" + path);
                }
                else if (host.Contains("mojang.com", StringComparison.OrdinalIgnoreCase)
                         || host.Contains("minecraft.net", StringComparison.OrdinalIgnoreCase))
                {
                    AddBmcl(path);
                }
            }
            catch (Exception)
            {
                // 非法 URL 只尝试原始地址
            }

            return LauncherSettings.Instance.DownloadMirror switch
            {
                "bmclapi_only" => mirrors.Count > 0 ? mirrors : official,
                "official" => official,
                "bmclapi" => mirrors.Concat(official).ToList(),
                _ => official.Concat(mirrors).ToList()
            };
        }

        /// <summary>
        /// 去掉 <c>maven.neoforged.net</c> 路径开头的仓库名（releases / snapshots）。
        /// 这些是仓库标识而非制品路径，镜像（BMCLAPI）侧的路径不含它们。
        /// </summary>
        private static string StripNeoForgeRepository(string path)
        {
            foreach (var repo in new[] { "/releases/", "/snapshots/" })
            {
                if (path.StartsWith(repo, StringComparison.OrdinalIgnoreCase))
                    return path[repo.Length..];
            }
            return path;
        }

        /// <summary>
        /// 按下载镜像策略依次尝试各源，取第一个成功的响应文本。
        /// 版本 JSON、加载器 profile 这类小文本文件走这里；之前它们是用各自的 HttpClient
        /// 直连官方的，等于绕开了「下载源」设置（国内直连 piston-meta 经常十几秒起步）。
        /// </summary>
        public static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("下载地址为空");

            Exception? last = null;
            foreach (var candidate in ExpandUrls(url))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return await Client.GetStringAsync(candidate, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw last ?? new InvalidOperationException("所有下载源都取不到：" + url);
        }

        public static async Task DownloadFileAsync(
            string url,
            string destination,
            CancellationToken ct,
            Action<long, long>? progress = null,
            Action<string>? status = null)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("下载地址为空");
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var urls = ExpandUrls(url);
            status?.Invoke(urls.Count > 1 ? $"正在探测 {urls.Count} 个下载源…" : "正在连接下载源…");

            // 只有一个候选源（official-only 策略，或 cdn.modrinth.com 这类没有镜像域名的地址）
            // 也照样走 swarm：swarm 内部会在「文件太小 / 不支持 Range」时自动退回单流下载，
            // 而支持 Range 的大文件（整合包常有几十到几百 MB）能从这一个源开多条连接，
            // CDN 单连接被限速时差距是数倍。失败仍会回落到下面的单源下载，不会拖成卡死。
            try
            {
                await SwarmDownloadAsync(urls, destination, ct, progress, status);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                TryDelete(destination);
                TryDelete(PartPath(destination));
            }

            Exception? last = null;
            foreach (var candidate in urls)
            {
                ct.ThrowIfCancellationRequested();
                status?.Invoke("正在回退到单源下载…");
                try
                {
                    await DownloadOnceAsync(candidate, destination, ct, progress);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    TryDelete(destination);
                    TryDelete(PartPath(destination));
                    // 注意：不删 destination + ".tmp"——候选源（官方/镜像）是同一制品的原样代理，
                    // 已下载的字节跨源续传安全（HMCL 同款做法），换源丢进度毫无意义。
                }
            }
            throw last ?? new InvalidOperationException("下载失败：" + url);
        }

        public static async Task DownloadManyAsync(
            IReadOnlyList<(string Url, string Path)> files,
            CancellationToken ct,
            int parallelism = FileParallelism,
            Action<int, int, string>? progress = null)
        {
            var initial = files
                .Where(f => !string.IsNullOrWhiteSpace(f.Url))
                .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Where(f => !File.Exists(f.Path) || new FileInfo(f.Path).Length == 0)
                .ToList();
            if (initial.Count == 0) return;

            var total = initial.Count;
            var done = 0;
            using var gate = new SemaphoreSlim(Math.Clamp(parallelism, 2, 24));
            var errors = new List<Exception>();
            // 瞬时故障（TLS 握手失败、镜像 429 限流、黑洞连接）常只打掉一小撮文件；
            // 对失败文件整轮重试（共 3 轮、轮间退避），比整体抛错让用户从头再来友好得多。
            const int maxRounds = 3;
            var remaining = initial;

            for (var round = 0; round < maxRounds && remaining.Count > 0; round++)
            {
                if (round > 0)
                {
                    progress?.Invoke(done, total, $"正在重试剩余 {remaining.Count} 个文件…");
                    try { await Task.Delay(TimeSpan.FromMilliseconds(500 * round), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                }

                var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (errors) errors.Clear(); // 只保留最后一轮的错误用于报错

                async Task One((string Url, string Path) file)
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        await DownloadFileAsync(file.Url, file.Path, ct);
                        var current = Interlocked.Increment(ref done);
                        var name = Path.GetFileName(file.Path);
                        // 重试成功的文件在进度里标注出来，用户才知道刚才那批失败已被救回
                        progress?.Invoke(current, total, round == 0 ? name : $"重试成功 {name}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        lock (errors) errors.Add(ex);
                        lock (failed) failed.Add(file.Path);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }

                try
                {
                    await Task.WhenAll(remaining.Select(One));
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                remaining = initial.Where(f => failed.Contains(f.Path)).ToList();
            }

            if (errors.Count > 0 && done == 0)
                throw new InvalidOperationException("下载失败：" + errors[0].Message);
            if (errors.Count > 0 && done < total)
                throw new InvalidOperationException($"有 {errors.Count} 个文件下载失败：" + errors[0].Message);
        }

        private static async Task SwarmDownloadAsync(
            IReadOnlyList<string> urls,
            string destination,
            CancellationToken ct,
            Action<long, long>? progress,
            Action<string>? status)
        {
            var probes = await Task.WhenAll(urls.Select(u => ProbeAsync(u, ct)));
            var alive = probes.Where(p => p is not null && p.Size > 0).Cast<SourceProbe>().ToList();
            if (alive.Count == 0)
                throw new InvalidOperationException("没有可用的下载源");

            var size = alive
                .GroupBy(p => p.Size)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First().Key;
            var sources = alive
                .Where(p => p.Size == size)
                .Select(p => new SourceState(p.Url, p.SupportsRange))
                .ToArray();
            var ranged = sources.Where(s => s.SupportsRange).ToArray();

            if (size < MinSwarmSize || ranged.Length == 0)
            {
                await DownloadOnceAsync(sources[0].Url, destination, ct, progress);
                return;
            }

            var chunkSize = size switch
            {
                >= 200L * 1024 * 1024 => 2 * 1024 * 1024,
                >= 50L * 1024 * 1024 => 1024 * 1024,
                _ => DefaultChunkSize
            };
            var pieces = BuildPieces(size, chunkSize);
            // 只有一个源时把并发全押在它身上：Modrinth / CurseForge 的制品域名没有镜像源可切，
            // 而 CDN 通常按连接限速，多连接才拉得满（多源时仍按每源 4 条，避免压垮某个镜像）。
            var perSource = ranged.Length == 1 ? Math.Min(MaxWorkers, 8) : MaxPerSource;
            var workers = Math.Clamp(Math.Min(pieces.Count, Math.Max(perSource, ranged.Length * 3)), 1, MaxWorkers);
            status?.Invoke(ranged.Length == 1
                ? $"单源 {workers} 连接 · 分片 {chunkSize / 1024} KB"
                : $"多源 {ranged.Length} · {workers} 连接");

            var queue = new ConcurrentQueue<Piece>(pieces);
            var inflight = new int[ranged.Length];
            var downloaded = 0L;
            var completed = 0;
            var inFlight = 0;
            var started = Environment.TickCount64;
            var lastStatusAt = 0L;
            var fatal = (Exception?)null;
            var part = PartPath(destination);
            TryDelete(part);

            var ok = false;
            var handle = File.OpenHandle(
                part,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                RandomAccess.SetLength(handle, size);

                void Report(bool force = false)
                {
                    var now = Volatile.Read(ref downloaded);
                    var tick = Environment.TickCount64;
                    if (!force && tick - lastStatusAt < 80 && now < size)
                        return;
                    lastStatusAt = tick;
                    progress?.Invoke(now, size);
                    if (status is null) return;
                    var elapsed = Math.Max(1, tick - started);
                    var speed = now * 1000.0 / elapsed;
                    var active = inflight.Count(v => v > 0);
                    var live = ranged.Count(s => !s.Disabled);
                    status($"多源 {live} · {active} 连接 · {FormatSpeed(speed)}");
                }

                async Task Worker()
                {
                    var buffer = new byte[BufferSize];
                    while (!ct.IsCancellationRequested)
                    {
                        if (!queue.TryDequeue(out var piece))
                        {
                            if (Volatile.Read(ref inFlight) == 0 && queue.IsEmpty)
                                return;
                            await Task.Delay(15, ct);
                            continue;
                        }

                        var index = PickSource(ranged, inflight, perSource);
                        if (index < 0)
                        {
                            if (ranged.All(s => s.Disabled))
                            {
                                queue.Enqueue(piece);
                                return;
                            }
                            queue.Enqueue(piece);
                            await Task.Delay(30, ct);
                            continue;
                        }

                        Interlocked.Increment(ref inFlight);
                        Interlocked.Increment(ref inflight[index]);
                        try
                        {
                            await DownloadPieceAsync(handle, ranged[index], piece, buffer, n =>
                            {
                                Interlocked.Add(ref downloaded, n);
                                Report();
                            }, ct);
                            Interlocked.Increment(ref completed);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            piece.Attempts++;
                            ranged[index].OnFailure(ex);
                            if (piece.Attempts < Math.Max(6, ranged.Length * 3))
                                queue.Enqueue(piece);
                            else
                                fatal ??= ex;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inflight[index]);
                            Interlocked.Decrement(ref inFlight);
                        }
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker()));
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (completed < pieces.Count || RandomAccess.GetLength(handle) != size)
                    throw fatal ?? new InvalidOperationException("多源分片未能全部完成");
                ok = true;
            }
            finally
            {
                handle.Dispose();
                if (!ok) TryDelete(part);
            }

            Replace(part, destination);
            progress?.Invoke(size, size);
            status?.Invoke($"多源 {ranged.Count(s => !s.Disabled)} · 已完成");
        }

        private static List<Piece> BuildPieces(long total, int chunkSize)
        {
            var list = new List<Piece>();
            long offset = 0;
            while (offset < total)
            {
                var length = (int)Math.Min(chunkSize, total - offset);
                list.Add(new Piece { Offset = offset, Length = length });
                offset += length;
            }
            return list;
        }

        private static int PickSource(SourceState[] sources, int[] inflight, int perSource)
        {
            var best = -1;
            var bestScore = double.MinValue;
            for (var i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                if (source.Disabled || inflight[i] >= perSource) continue;
                var score = source.Score / (1 + inflight[i]);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private static async Task DownloadPieceAsync(
            SafeFileHandle handle,
            SourceState source,
            Piece piece,
            byte[] buffer,
            Action<int> onBytes,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.Range = new RangeHeaderValue(piece.Offset, piece.Offset + piece.Length - 1);
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stallCts.CancelAfter(StallTimeoutMs);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                throw new HttpRequestException($"源不可用 ({(int)response.StatusCode})", null, response.StatusCode);
            if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                response.EnsureSuccessStatusCode();

            var returned = response.Content.Headers.ContentLength ?? -1;
            if (response.StatusCode == HttpStatusCode.OK && returned > piece.Length)
                throw new InvalidOperationException("该源不支持分片");

            var started = Environment.TickCount64;
            await using var content = await response.Content.ReadAsStreamAsync(stallCts.Token);
            long written = 0;
            while (written < piece.Length)
            {
                stallCts.CancelAfter(StallTimeoutMs);
                var take = (int)Math.Min(buffer.Length, piece.Length - written);
                var n = await content.ReadAsync(buffer.AsMemory(0, take), stallCts.Token);
                if (n <= 0) break;
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, n), piece.Offset + written, ct);
                written += n;
                onBytes(n);
            }
            if (written != piece.Length)
                throw new IOException($"分片长度不符（{written}/{piece.Length}）");
            source.OnSuccess(piece.Length, Math.Max(1, Environment.TickCount64 - started));
        }

        private static async Task<SourceProbe?> ProbeAsync(string url, CancellationToken ct)
        {
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(ProbeTimeoutMs);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                    return null;

                long size = 0;
                var range = response.StatusCode == HttpStatusCode.PartialContent;
                if (response.Content.Headers.ContentRange?.Length is long len && len > 0)
                    size = len;
                else if (response.Content.Headers.ContentLength is long contentLength && contentLength > 0)
                    size = contentLength;

                if (response.StatusCode == HttpStatusCode.OK && size > 1)
                    range = false;

                return size > 0 ? new SourceProbe(url, size, range) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task DownloadOnceAsync(
            string url,
            string destination,
            CancellationToken ct,
            Action<long, long>? progress)
        {
            Exception? last = null;
            var tmp = destination + ".tmp";
            for (var attempt = 0; attempt < 4; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 0)
                {
                    // 指数退避：400ms → 800ms → 1600ms。黑洞连接往往意味着对端半死，
                    // 不退避立刻重连大概率再撞一次同样的停滞。
                    try { await Task.Delay(400 << attempt, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                }
                // 断点续传：上次尝试写进 tmp 的字节保留，带 Range 从断点继续。
                // 大文件（如基岩版 ~550MB 包）中途断流后若每次都从头开始，4 次尝试几乎不可能成功。
                var resumeFrom = 0L;
                try
                {
                    if (File.Exists(tmp))
                        resumeFrom = new FileInfo(tmp).Length;
                    if (resumeFrom < 0) resumeFrom = 0;

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (resumeFrom > 0)
                        request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
                    using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();

                    var resumed = resumeFrom > 0
                        && response.StatusCode == HttpStatusCode.PartialContent;
                    if (!resumed)
                        resumeFrom = 0; // 服务器不支持 Range（返回 200 全量）→ 从头写

                    // 206 的 Content-Length 是剩余量，ContentRange.Length 才是全文件大小；
                    // 200 则 Content-Length 就是全大小。
                    long total;
                    if (response.Content.Headers.ContentRange?.Length is long fullLen && fullLen > 0)
                        total = fullLen;
                    else
                        total = response.Content.Headers.ContentLength is long len && len > 0
                            ? resumeFrom + len
                            : 0;

                    // 小文件把停滞超时从 3 分钟收紧到 30 秒（见常量注释），
                    // 没有 Content-Length 的响应无法预判大小，保守沿用长超时。
                    var remainingBytes = total - resumeFrom;
                    var stallMs = remainingBytes is > 0 && remainingBytes <= SmallFileSizeThreshold
                        ? SmallFileStallTimeoutMs
                        : StallTimeoutMs;

                    var appendMode = resumed;
                    await using (var fileStream = new FileStream(
                        tmp,
                        appendMode ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        BufferSize,
                        useAsync: true))
                    await using (var content = await response.Content.ReadAsStreamAsync(ct))
                    {
                        var buffer = new byte[BufferSize];
                        long read = 0;
                        int n;
                        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        while (true)
                        {
                            stallCts.CancelAfter(stallMs);
                            n = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token);
                            if (n <= 0) break;
                            await fileStream.WriteAsync(buffer.AsMemory(0, n), ct);
                            read += n;
                            progress?.Invoke(resumeFrom + read, total);
                        }

                        // 服务器温和关闭连接时不会抛「响应提前结束」，只能靠长度校验兜底
                        if (total > 0 && resumeFrom + read < total)
                            throw new IOException($"下载不完整（{resumeFrom + read}/{total} 字节），响应提前结束");
                    }
                    Replace(tmp, destination);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(tmp);
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // 保留 tmp：下一次尝试从断点继续，不因一次断流就丢掉几百 MB 的进度
                }
            }
            throw last ?? new InvalidOperationException("单源下载失败：" + url);
        }

        private static string PartPath(string destination) => destination + ".part";

        private static void Replace(string tmp, string destination)
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tmp, destination);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1024 * 1024)
                return $"{bytesPerSecond / (1024 * 1024):0.#} MB/s";
            if (bytesPerSecond >= 1024)
                return $"{bytesPerSecond / 1024:0.#} KB/s";
            return $"{bytesPerSecond:0} B/s";
        }

        private sealed class Piece
        {
            public long Offset;
            public int Length;
            public int Attempts;
        }

        private sealed record SourceProbe(string Url, long Size, bool SupportsRange);

        private sealed class SourceState
        {
            public SourceState(string url, bool supportsRange)
            {
                Url = url;
                SupportsRange = supportsRange;
            }

            public string Url { get; }
            public bool SupportsRange { get; }
            public bool Disabled { get; private set; }
            public int Failures { get; private set; }
            public long Bytes { get; private set; }
            public long ElapsedMs { get; private set; }

            public double Score
            {
                get
                {
                    if (Disabled) return double.MinValue;
                    if (ElapsedMs <= 0) return 8;
                    return Bytes / (double)ElapsedMs;
                }
            }

            public void OnSuccess(long bytes, long elapsedMs)
            {
                Bytes += bytes;
                ElapsedMs += elapsedMs;
                Failures = 0;
            }

            public void OnFailure(Exception ex)
            {
                Failures++;
                if (Failures >= 3
                    || ex is HttpRequestException http && http.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                    Disabled = true;
            }
        }
    }
}
