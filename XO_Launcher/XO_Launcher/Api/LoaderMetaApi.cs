using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using XO_Launcher.Models;

namespace XO_Launcher.Api
{
    /// <summary>
    /// 模组加载器元数据。接口与 HMCL / Prism / XMCL 相同：
    /// Fabric Meta、Quilt Meta、Forge Maven、NeoForge Maven。
    /// </summary>
    public sealed class LoaderMetaApi
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        // (loader, mc) 维度的版本列表缓存：用 Lazy<Task<...>> 包装，保证同一对 (loader, mc)
        // 在并发调用下只发出一次网络请求，后到的调用复用同一个 Task（参考 HMCL VersionList
        // 的 RefreshTask 缓存与 PrismLauncher 的 MetaVersionList TaskCache）。
        // 缓存里保存「全量列表」含 beta，UI 自己按 Stable 字段过滤——避免「切换显示全部」
        // 开关时再次发起网络请求。ClearCache 用于「点击重试」之类的强制刷新场景。
        private static readonly ConcurrentDictionary<(string loader, string mc), Lazy<Task<IReadOnlyList<LoaderBuild>>>> BuildCache = new();

        public async Task<IReadOnlyList<LoaderBuild>> GetBuildsAsync(string loader, string minecraftVersion, CancellationToken ct = default)
        {
            var key = (loader ?? "", minecraftVersion ?? "");
            var lazy = BuildCache.GetOrAdd(key,
                k => new Lazy<Task<IReadOnlyList<LoaderBuild>>>(
                    // 后台拉取不吃调用方的 token：缓存存的是 Task 对象本身，一旦把首个调用者的
                    // token 传进去，他一取消（切版本 / 离开详情页）这个已取消的 Task 就永久留在
                    // 缓存里，之后每次进同一版本都直接拿到「加载失败」，除非点重试清缓存。
                    () => FetchBuildsAsync(k.loader, k.mc, CancellationToken.None)));
            try
            {
                // 调用方的 token 只管「这一次还等不等」，不打断后台把结果拉完填热缓存
                return await lazy.Value.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 失败结果不进缓存：Lazy 会把异常 Task 一直存着，不移除就永远拉不出列表
                BuildCache.TryRemove(key, out _);
                throw;
            }
        }

        private async Task<IReadOnlyList<LoaderBuild>> FetchBuildsAsync(string loader, string mc, CancellationToken ct)
        {
            // 单条请求有超时，不等于重试链不会拖满几分钟：给整次拉取一个总预算，
            // 超时就报可读错误，让 UI 出「点击重试」，而不是让「正在查询…」一直转圈。
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                return loader switch
                {
                    "fabric" => await GetFabricAsync(mc, budget.Token),
                    "quilt" => await GetQuiltAsync(mc, budget.Token),
                    "forge" => await GetForgeAsync(mc, budget.Token),
                    "neoforge" => await GetNeoForgeAsync(mc, budget.Token),
                    _ => Array.Empty<LoaderBuild>()
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("版本列表拉取超时（45 秒），请检查网络或换下载镜像后重试。");
            }
        }

        /// <summary>清除指定 (loader, mc) 的缓存；loader 或 mc 为 null 时全清。</summary>
        public static void ClearCache(string? loader = null, string? mc = null)
        {
            if (loader is null || mc is null)
            {
                BuildCache.Clear();
                return;
            }
            BuildCache.TryRemove((loader, mc), out _);
        }

        private static LoaderSupportIndex? _supportCache;
        private static DateTime _supportCacheAt;

        /// <summary>一次拉齐各加载器覆盖的游戏版本，供原版列表展示可用加载器。</summary>
        public async Task<LoaderSupportIndex> GetSupportIndexAsync(CancellationToken ct = default)
        {
            if (_supportCache is not null && DateTime.UtcNow - _supportCacheAt < TimeSpan.FromHours(6))
                return _supportCache;

            var index = new LoaderSupportIndex();
            var fabric = FillGameVersionsAsync("https://meta.fabricmc.net/v2/versions/game", index.Fabric, ct);
            var quilt = FillGameVersionsAsync("https://meta.quiltmc.org/v3/versions/game", index.Quilt, ct);
            var forge = FillForgeVersionsAsync(index.Forge, ct);
            var neo = FillNeoForgeVersionsAsync(index.NeoForge, ct);
            await Task.WhenAll(fabric, quilt, forge, neo);
            if (index.Fabric.Count + index.Quilt.Count + index.Forge.Count + index.NeoForge.Count > 0)
            {
                _supportCache = index;
                _supportCacheAt = DateTime.UtcNow;
            }
            return index;
        }

        private async Task FillGameVersionsAsync(string url, HashSet<string> target, CancellationToken ct)
        {
            try
            {
                // 走 RaceAsync：它按下载镜像策略在官方与镜像（fabric-meta / quilt-meta）之间竞速，
                // 单源直连 meta.fabricmc.net 在国内经常十几秒起步，会拖住整张原版列表。
                // 20 秒上限由 FetchJsonListOnceAsync 内部给，超时算这一个加载器失败。
                var rows = await RaceAsync(url, FetchJsonListOnceAsync<GameVersionRow>, ct);
                foreach (var row in rows)
                {
                    if (!string.IsNullOrWhiteSpace(row.Version))
                        target.Add(row.Version);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 单个加载器清单失败时其余仍可用
            }
        }

        private async Task FillForgeVersionsAsync(HashSet<string> target, CancellationToken ct)
        {
            var versions = await ReadMavenVersionsOrEmptyAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
            foreach (var value in versions)
            {
                var dash = value.IndexOf('-');
                if (dash > 0) target.Add(value[..dash]);
            }
        }

        private async Task FillNeoForgeVersionsAsync(HashSet<string> target, CancellationToken ct)
        {
            var modern = await ReadMavenVersionsOrEmptyAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct);
            foreach (var value in modern)
            {
                if (value.Contains("beta", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = value.Split('.');
                if (parts.Length < 2) continue;
                var minor = parts[0];
                var patch = parts[1];
                target.Add(patch == "0" ? $"1.{minor}" : $"1.{minor}.{patch}");
            }

            var legacy = await ReadMavenVersionsOrEmptyAsync(
                "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml", ct);
            foreach (var value in legacy)
            {
                var dash = value.IndexOf('-');
                if (dash > 0) target.Add(value[..dash]);
            }
        }

        public static string FabricProfileUrl(string mc, string loader)
            => $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json";

        public static string QuiltProfileUrl(string mc, string loader)
            => $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mc)}/{Uri.EscapeDataString(loader)}/profile/json";

        private async Task<IReadOnlyList<LoaderBuild>> GetFabricAsync(string mc, CancellationToken ct)
        {
            var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mc)}";
            var rows = await GetJsonListWithRetryAsync<FabricLoaderRow>(url, "Fabric", ct);
            return rows
                .Where(r => r.Loader is not null && !string.IsNullOrWhiteSpace(r.Loader.Version))
                .Select(r => new LoaderBuild
                {
                    Loader = "fabric",
                    Version = r.Loader!.Version,
                    MavenVersion = r.Loader.Version,
                    Stable = r.Loader.Stable,
                    Display = TaggedDisplay(r.Loader.Version, r.Loader.Stable)
                })
                .ToList();
        }

        private async Task<IReadOnlyList<LoaderBuild>> GetQuiltAsync(string mc, CancellationToken ct)
        {
            var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mc)}";
            var rows = await GetJsonListWithRetryAsync<QuiltLoaderRow>(url, "Quilt", ct);
            // Quilt Meta 的返回顺序是乱序的（既非升序也非降序），不像 Forge / NeoForge 的
            // maven-metadata 那样按构建号递增。若不排序，UI 默认选中 SelectedIndex=0 时
            // 可能落到 0.24.0 这种一年前的旧版本（真实最新稳定版已是 0.30.x）。
            // Quilt 接口没有 stable 字段，只能靠版本号语义降序 + 名称里的 beta/pre 来区分。
            return rows
                .Where(r => r.Loader is not null && !string.IsNullOrWhiteSpace(r.Loader.Version))
                .Select(r => new LoaderBuild
                {
                    Loader = "quilt",
                    Version = r.Loader!.Version,
                    MavenVersion = r.Loader.Version,
                    Stable = IsStableLoaderVersion(r.Loader.Version),
                    Display = TaggedDisplay(r.Loader.Version, IsStableLoaderVersion(r.Loader.Version))
                })
                .OrderByDescending(b => b.Version, LoaderVersionComparer.Instance)
                .ToList();
        }

        private async Task<IReadOnlyList<LoaderBuild>> GetForgeAsync(string mc, CancellationToken ct)
        {
            const string meta = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
            var (versions, fetchFailed) = await ReadMavenVersionsAsync(meta, ct);
            if (fetchFailed)
                throw new InvalidOperationException("Forge 版本清单拉取失败，请检查网络后重试。");
            var prefix = mc + "-";
            return versions
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .Reverse()
                .Take(40)
                .Select(v =>
                {
                    var build = v[prefix.Length..];
                    return new LoaderBuild
                    {
                        Loader = "forge",
                        Version = build,
                        MavenVersion = v,
                        Stable = IsStableLoaderVersion(build),
                        Display = TaggedDisplay(build, IsStableLoaderVersion(build)),
                        InstallerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{v}/forge-{v}-installer.jar"
                    };
                })
                .ToList();
        }

        private async Task<IReadOnlyList<LoaderBuild>> GetNeoForgeAsync(string mc, CancellationToken ct)
        {
            var mirror = XO_Launcher.Services.LauncherSettings.Instance.DownloadMirror;
            if (mirror != "official")
            {
                // HMCL 同款做法：BMCLAPI 提供按游戏版本过滤的 NeoForge 列表 JSON 端点
                // （GET /neoforge/list/{mc}）。官方 maven.neoforged.net 从国内直连经常
                // TLS 握手失败（实测约 40% 概率 curl 000），这个端点国内可达性好得多，
                // 且返回的每条都带 installerPath，免去 maven-metadata 的前缀匹配与猜测。
                try
                {
                    var builds = await GetNeoForgeFromBmclAsync(mc, ct);
                    if (builds.Count > 0) return builds;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // 端点失败（例如该版本未被收录、镜像故障）时回落到 maven-metadata 路径
                }
            }
            return await GetNeoForgeFromMavenAsync(mc, ct);
        }

        /// <summary>
        /// 通过 BMCLAPI 的 <c>/neoforge/list/{mc}</c> 端点获取某个游戏版本的 NeoForge 构建列表。
        /// 响应是 <c>{ rawVersion, version, mcversion, installerPath }</c> 的数组（升序、含 beta）。
        /// installerPath 是 BMCLAPI 镜像路径 <c>/maven/&lt;制品路径&gt;</c>，
        /// 去掉 <c>/maven</c> 前缀就是官方 releases 仓库的制品路径——返回官方地址，
        /// 下载侧 ExpandUrls 会自动补上镜像候选，由 swarm 挑快的源。
        /// </summary>
        private async Task<IReadOnlyList<LoaderBuild>> GetNeoForgeFromBmclAsync(string mc, CancellationToken ct)
        {
            var url = $"https://bmclapi2.bangbang93.com/neoforge/list/{Uri.EscapeDataString(mc)}";
            List<BmclNeoForgeRow> rows = new();
            Exception? last = null;
            for (var round = 0; round < 2; round++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await using var stream = await Client.GetStreamAsync(url, timeout.Token);
                    rows = await JsonSerializer.DeserializeAsync<List<BmclNeoForgeRow>>(stream, JsonOptions, timeout.Token) ?? new();
                    break;
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
            if (rows.Count == 0)
                throw new InvalidOperationException("NeoForge 版本列表拉取失败：" + (last?.Message ?? "响应为空"));

            var result = new List<LoaderBuild>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var v = row.Version?.Trim();
                if (string.IsNullOrEmpty(v)) continue;
                // 测试版不再整条丢掉：MC 26.x 这类新版本的 NeoForge 目前只有 beta 构建
                // （如 26.3.0.0-beta），丢掉就等于「该版本没有 NeoForge」。
                // 是否显示由页面的「显示全部加载器版本」开关决定，标签写清是测试版。

                var display = NormalizeNeoForgeVersion(v, mc);
                if (string.IsNullOrEmpty(display) || !seen.Add(display)) continue;

                var mavenVersion = ExtractMavenVersionFromInstallerPath(row.InstallerPath);
                if (string.IsNullOrEmpty(mavenVersion))
                {
                    // 旧式条目（1.20.1 早期）没有 installerPath，rawVersion 形如 "1.20.1-47.1.5"
                    var raw = row.RawVersion?.Trim();
                    if (!string.IsNullOrEmpty(raw) && raw.StartsWith(mc + "-", StringComparison.Ordinal))
                        mavenVersion = raw;
                    else
                        continue;
                }

                var installerUrl = mavenVersion.StartsWith(mc + "-", StringComparison.Ordinal)
                    ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{mavenVersion}/forge-{mavenVersion}-installer.jar"
                    : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{mavenVersion}/neoforge-{mavenVersion}-installer.jar";

                result.Add(new LoaderBuild
                {
                    Loader = "neoforge",
                    Version = display,
                    MavenVersion = mavenVersion,
                    Stable = IsStableLoaderVersion(display),
                    Display = TaggedDisplay(display, IsStableLoaderVersion(display)),
                    InstallerUrl = installerUrl
                });
            }

            result.Sort((a, b) => LoaderVersionComparer.Instance.Compare(b.Version, a.Version));
            if (result.Count > 40) result.RemoveRange(40, result.Count - 40);
            return result;
        }

        /// <summary>HMCL 同款归一化："1.20.1-47.1.105" / "1.20.1-forge-47.1.105" → "47.1.105"。</summary>
        private static string NormalizeNeoForgeVersion(string version, string mc)
        {
            var forgePrefix = mc + "-forge-";
            if (version.StartsWith(forgePrefix, StringComparison.Ordinal))
                return version[forgePrefix.Length..];
            var plainPrefix = mc + "-";
            if (version.StartsWith(plainPrefix, StringComparison.Ordinal))
                return version[plainPrefix.Length..];
            return version;
        }

        /// <summary>
        /// 从 installerPath（形如 <c>/maven/net/neoforged/neoforge/21.4.158/neoforge-21.4.158-installer.jar</c>）
        /// 里取 maven 制品版本（目录倒数第二段）。
        /// </summary>
        private static string? ExtractMavenVersionFromInstallerPath(string? installerPath)
        {
            var p = installerPath?.Trim();
            if (string.IsNullOrEmpty(p)) return null;
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[^2] : null;
        }

        private async Task<IReadOnlyList<LoaderBuild>> GetNeoForgeFromMavenAsync(string mc, CancellationToken ct)
        {
            var (modern, modernFailed) = await ReadMavenVersionsAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct);
            if (modernFailed)
                throw new InvalidOperationException("NeoForge 版本清单拉取失败，请检查网络后重试。");

            var prefix = NeoForgePrefix(mc);
            var builds = modern
                .Where(v => prefix.Length > 0 && v.StartsWith(prefix, StringComparison.Ordinal))
                .Reverse()
                .Take(40)
                .Select(v => new LoaderBuild
                {
                    Loader = "neoforge",
                    Version = v,
                    MavenVersion = v,
                    Stable = IsStableLoaderVersion(v),
                    Display = TaggedDisplay(v, IsStableLoaderVersion(v)),
                    InstallerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{v}/neoforge-{v}-installer.jar"
                })
                .ToList();

            if (builds.Count > 0) return builds;

            // 1.20.1 早期 NeoForge 使用 net.neoforged:forge
            var (legacy, legacyFailed) = await ReadMavenVersionsAsync(
                "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml", ct);
            if (legacyFailed)
                throw new InvalidOperationException("NeoForge 旧版清单拉取失败，请检查网络后重试。");

            var legacyPrefix = mc + "-";
            return legacy
                .Where(v => v.StartsWith(legacyPrefix, StringComparison.Ordinal))
                .Reverse()
                .Take(40)
                .Select(v => new LoaderBuild
                {
                    Loader = "neoforge",
                    Version = v[legacyPrefix.Length..],
                    MavenVersion = v,
                    Stable = IsStableLoaderVersion(v),
                    Display = TaggedDisplay(v[legacyPrefix.Length..], IsStableLoaderVersion(v)),
                    InstallerUrl = $"https://maven.neoforged.net/releases/net/neoforged/forge/{v}/forge-{v}-installer.jar"
                })
                .ToList();
        }

        private static string NeoForgePrefix(string mcVersion)
        {
            var core = mcVersion.Split('-')[0];
            var parts = core.Split('.');
            if (parts.Length < 2) return "";
            // 旧命名（1.20.1）里 NeoForge 去掉开头的 "1."，制品版本形如 20.1.x；
            // 新命名（26.3 这类）不再以 1. 开头，制品版本直接跟着完整主版本，如 26.3.0.0。
            if (parts[0] == "1")
            {
                var patch = parts.Length > 2 ? parts[2] : "0";
                return $"{parts[1]}.{patch}.";
            }
            return core + ".";
        }

        /// <summary>列表项文案：版本号后面统一跟上「正式版 / 测试版」，四类加载器一套规则。</summary>
        private static string TaggedDisplay(string version, bool stable)
            => $"{version}  ·  {(stable ? "正式版" : "测试版")}";

        /// <summary>
        /// Quilt Meta 不提供 stable 字段，只能按版本号命名判断：
        /// 带 beta / alpha / rc / pre / snapshot / dev 的一律视为测试版。
        /// </summary>
        private static bool IsStableLoaderVersion(string version)
        {
            var v = version.ToLowerInvariant();
            return !v.Contains("beta") && !v.Contains("alpha") && !v.Contains("rc")
                   && !v.Contains("pre") && !v.Contains("snapshot") && !v.Contains("dev");
        }

        /// <summary>
        /// 加载器版本号语义比较：按 数字段 &gt; 文本段 逐段比较，数字段按数值比大小。
        /// 用于把 Quilt 乱序的列表排成 0.30.1 &gt; 0.30.0 &gt; 0.29.2 … &gt; 0.20.0-beta.9。
        /// </summary>
        private sealed class LoaderVersionComparer : IComparer<string>
        {
            public static readonly LoaderVersionComparer Instance = new();

            public int Compare(string? x, string? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (string.IsNullOrEmpty(x)) return string.IsNullOrEmpty(y) ? 0 : -1;
                if (string.IsNullOrEmpty(y)) return 1;

                var a = x.Split('.', '-', '+');
                var b = y.Split('.', '-', '+');
                var len = Math.Max(a.Length, b.Length);
                for (var i = 0; i < len; i++)
                {
                    var sa = i < a.Length ? a[i] : "";
                    var sb = i < b.Length ? b[i] : "";
                    var na = long.TryParse(sa, out var va);
                    var nb = long.TryParse(sb, out var vb);
                    int cmp;
                    if (na && nb) cmp = va.CompareTo(vb);
                    else if (na) cmp = 1;        // 纯数字段比文本段新（0.30 > 0.30-beta）
                    else if (nb) cmp = -1;
                    else cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                }
                return 0;
            }
        }

        private static async Task<(List<string> Versions, bool FetchFailed)> ReadMavenVersionsAsync(string url, CancellationToken ct)
        {
            try
            {
                return (await RaceAsync(url, FetchMavenOnceAsync, ct), false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return (new List<string>(), true);
            }
        }

        /// <summary>
        /// 多源竞速拉取：元数据体积很小，同时向候选源发请求、取最先成功的一个，其余取消。
        /// 比「官方失败 → 等满超时 → 换镜像」快得多，也不挑地区（谁近谁先回）。
        /// 候选顺序沿用 ExpandUrls 的镜像策略（auto / bmclapi / bmclapi_only / official）。
        /// </summary>
        private static async Task<T> RaceAsync<T>(string url, Func<string, CancellationToken, Task<T>> fetch, CancellationToken ct)
        {
            var candidates = XO_Launcher.Services.HttpDownloadHelper.ExpandUrls(url);
            if (candidates.Count == 0)
                throw new InvalidOperationException("没有可用的请求地址：" + url);

            var failures = new List<Exception>();
            for (var round = 0; round < 2; round++)
            {
                if (round > 0)
                {
                    // 退避：给 TLS 抖动和 429 限流一点恢复时间
                    try { await Task.Delay(TimeSpan.FromMilliseconds(400 * round), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                }

                using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pending = candidates.Take(3).Select(u => FetchSafe(u)).ToList();

                async Task<(bool Ok, T Value, Exception? Error)> FetchSafe(string u)
                {
                    try { return (true, await fetch(u, race.Token), null); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { return (false, default!, ex); }
                }

                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending);
                    pending.Remove(done);
                    var result = await done;
                    if (result.Ok)
                    {
                        race.Cancel();   // 赢家已定，其余竞速者没必要继续占连接
                        return result.Value;
                    }
                    if (result.Error is not null) failures.Add(result.Error);
                }
            }
            throw new AggregateException("版本清单拉取失败，请检查网络后重试。", failures);
        }

        private static async Task<List<string>> FetchMavenOnceAsync(string url, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var xml = await Client.GetStringAsync(url, timeout.Token);
            var doc = XDocument.Parse(xml);
            return doc.Descendants("version").Select(x => x.Value)
                      .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        }

        /// <summary>只关心版本列表、不关心拉取是否失败时用这个（例如给原版列表打加载器标签）。</summary>
        private static async Task<List<string>> ReadMavenVersionsOrEmptyAsync(string url, CancellationToken ct)
            => (await ReadMavenVersionsAsync(url, ct)).Versions;

        /// <summary>
        /// Fabric / Quilt Meta 的 JSON 列表拉取（多源竞速，见 RaceAsync）。
        /// 404 表示「该游戏版本不受此加载器支持」，返回空列表而非错误；
        /// 其余网络异常抛可读异常，让 UI 给出可点击的重试入口，而不是静默的「暂无版本」。
        /// </summary>
        private async Task<List<T>> GetJsonListWithRetryAsync<T>(string url, string loaderName, CancellationToken ct)
        {
            try
            {
                return await RaceAsync(url, FetchJsonListOnceAsync<T>, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AggregateException agg)
            {
                var inner = agg.Flatten().InnerExceptions;
                // 每个源都说 404 = 该游戏版本确实不受这个加载器支持，给空列表而非错误
                if (inner.All(e => e is LoaderUnsupportedVersionException))
                    return new List<T>();
                throw new InvalidOperationException($"{loaderName} 版本列表拉取失败，请检查网络后重试。", inner.Last());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{loaderName} 版本列表拉取失败，请检查网络后重试。", ex);
            }
        }

        private static async Task<List<T>> FetchJsonListOnceAsync<T>(string url, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Client.GetAsync(url, timeout.Token);
            // 404 不能当「成功但为空」：镜像往往先回 404，会在竞速里盖掉官方源的真实列表。
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new LoaderUnsupportedVersionException();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, timeout.Token)
                   ?? new List<T>();
        }

        /// <summary>某个源明确回答 404（该版本不收录），与网络故障区分开。</summary>
        private sealed class LoaderUnsupportedVersionException : Exception
        {
        }

        private sealed class GameVersionRow
        {
            public string Version { get; set; } = "";
        }

        private sealed class FabricLoaderRow
        {
            public FabricLoaderInfo? Loader { get; set; }
        }

        private sealed class FabricLoaderInfo
        {
            public string Version { get; set; } = "";
            public bool Stable { get; set; }
        }

        private sealed class QuiltLoaderRow
        {
            public QuiltLoaderInfo? Loader { get; set; }
        }

        private sealed class QuiltLoaderInfo
        {
            public string Version { get; set; } = "";
        }

        /// <summary>
        /// BMCLAPI <c>/neoforge/list/{mc}</c> 端点的行结构（HMCL NeoForgeBMCLVersionList 同款）：
        /// <c>rawVersion</c> 形如 <c>21.4.158-beta</c> 或旧式 <c>1.20.1-47.1.5</c>；
        /// <c>installerPath</c> 形如 <c>/maven/net/neoforged/neoforge/21.4.158/neoforge-21.4.158-installer.jar</c>。
        /// </summary>
        private sealed class BmclNeoForgeRow
        {
            public string? RawVersion { get; set; }
            public string? Version { get; set; }
            public string? McVersion { get; set; }
            public string? InstallerPath { get; set; }
        }
    }
}
