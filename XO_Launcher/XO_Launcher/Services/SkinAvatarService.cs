using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>从正版皮肤贴图裁出头部（含帽子层），缓存到本地供界面显示。</summary>
    public static class SkinAvatarService
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        public static string CacheDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "avatars");

        public static string SkinsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "skins");

        public static string FilePath(string uuid)
        {
            var hex = (uuid ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
            return Path.Combine(CacheDirectory, hex + ".png");
        }

        public static string FullSkinPath(string uuid)
        {
            var hex = (uuid ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
            return Path.Combine(SkinsDirectory, hex + ".png");
        }

        /// <summary>
        /// 远端（mojang / url）下载的皮肤缓存，与用户本地皮肤分开存放。
        /// 原先远端缓存和本地皮肤共用同一个 <c>{uuid}.png</c>：切到 mojang 会误把旧本地文件
        /// 当成远端缓存直接返回（皮肤不变），切回 local 又可能读到远端下载的文件。
        /// 分开后两边互不覆盖。
        /// </summary>
        public static string RemoteSkinPath(string uuid)
        {
            var hex = (uuid ?? "").Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
            return Path.Combine(SkinsDirectory, hex + ".remote.png");
        }

        /// <summary>内置默认皮肤（Steve / Alex）ms-appx URI 列表——最后兜底，避免无皮肤可显示。</summary>
        public const string DefaultSkinClassicPath = "ms-appx:///Assets/skin-default/steve.png";
        public const string DefaultSkinSlimPath = "ms-appx:///Assets/skin-default/alex.png";

        public static string DefaultSkinUri(bool slim) =>
            slim ? DefaultSkinSlimPath : DefaultSkinClassicPath;

        public static string? CachedPath(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return null;
            var path = FilePath(uuid);
            return File.Exists(path) ? path : null;
        }

        public static void Invalidate(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return;
            // 只清「派生」的头部缓存。完整皮肤文件对于 local 来源就是用户数据本身，
            // 误删会导致刚保存的皮肤丢失（PickLocalSkin / 切换来源时都走这里）。
            TryDelete(FilePath(uuid));
        }

        /// <summary>
        /// 清掉远端下载的完整皮肤缓存（下次从网络重拉）。
        /// 仅 mojang / url 来源调用；local 来源绝不能调（那就是用户文件本身）。
        /// </summary>
        public static void InvalidateRemote(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return;
            // 远端缓存与本地皮肤已分文件：这里两个都清，
            // 但用户的 LocalSkinPath 指向的文件本身绝不动。
            TryDelete(FilePath(uuid));
            TryDelete(RemoteSkinPath(uuid));
        }

        /// <summary>把选中文件拷贝到 SkinsDirectory/{uuid}.png；返回绝对路径。</summary>
        public static async Task<string> SetLocalSkinAsync(string uuid, string sourcePngPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uuid))
                throw new ArgumentException("uuid 不能为空", nameof(uuid));
            if (string.IsNullOrWhiteSpace(sourcePngPath) || !File.Exists(sourcePngPath))
                throw new FileNotFoundException("找不到本地皮肤文件", sourcePngPath);

            var hex = uuid.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
            var target = Path.Combine(SkinsDirectory, hex + ".png");
            Directory.CreateDirectory(SkinsDirectory);
            await using (var src = new FileStream(sourcePngPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, ct);
            }
            // 切到 local 后头缓存就过时了，重生成
            TryDelete(FilePath(uuid));
            return target;
        }

        /// <summary>从账号配置解析出实际生效的皮肤文件路径（同步版，命中本地文件直接返回）。</summary>
        public static string? ResolveLocalSkinFile(GameAccount account)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Uuid)) return null;
            // 1. 账号指定的本地路径（绝对或相对 LocalAppData）
            var explicitPath = account.LocalSkinPath;
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                if (Path.IsPathRooted(explicitPath) && File.Exists(explicitPath)) return explicitPath;
                var relative = Path.Combine(SkinsDirectory, Path.GetFileName(explicitPath));
                if (File.Exists(relative)) return relative;
            }
            // 2. SkinsDirectory/{uuid}.png 兜底
            var byUuid = FullSkinPath(account.Uuid);
            if (File.Exists(byUuid)) return byUuid;
            return null;
        }

        public static async Task CacheFullSkinAsync(string uuid, byte[] png)
        {
            if (string.IsNullOrWhiteSpace(uuid) || png is null || png.Length == 0) return;
            Directory.CreateDirectory(SkinsDirectory);
            await File.WriteAllBytesAsync(RemoteSkinPath(uuid), png);
        }

        public static async Task<string?> GetFullSkinPathAsync(GameAccount account, CancellationToken cancellationToken = default)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Uuid))
                return null;

            // local → 用本地 PNG，命中就直接返回（无须走网络）
            if (string.Equals(account.SkinSource, GameAccount.SkinSourceLocal, StringComparison.OrdinalIgnoreCase))
            {
                var local = ResolveLocalSkinFile(account);
                if (!string.IsNullOrEmpty(local)) return local;
                // local 但文件丢了：回落 default 占位
                return null;
            }

            // default → 不读网络
            if (string.Equals(account.SkinSource, GameAccount.SkinSourceDefault, StringComparison.OrdinalIgnoreCase))
                return null;

            // 注意：远端缓存单独放在 *.remote.png，绝不能复用 FullSkinPath（那是用户的本地皮肤）。
            var cache = RemoteSkinPath(account.Uuid);
            if (File.Exists(cache) && new FileInfo(cache).Length > 0)
                return cache;

            var url = account.SkinUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (account.IsMicrosoft)
                    url = $"https://crafatar.com/skins/{account.UuidHyphenated}";
                else if (string.Equals(account.SkinSource, GameAccount.SkinSourceUrl, StringComparison.OrdinalIgnoreCase))
                    return null; // url 但没给：回落默认
                else
                    return null;
            }
            if (string.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                using var response = await Client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) return File.Exists(cache) ? cache : null;
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                Directory.CreateDirectory(SkinsDirectory);
                await File.WriteAllBytesAsync(cache, bytes, cancellationToken);
                return File.Exists(cache) ? cache : null;
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Skin", "缓存完整皮肤失败：" + ex.Message);
                return File.Exists(cache) ? cache : null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        public static async Task<string?> GetHeadPathAsync(GameAccount account, CancellationToken cancellationToken = default)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.Uuid))
                return null;

            // 头缓存仍在 → 直接复用（避免重复渲染 64x64 头部）
            var cache = FilePath(account.Uuid);
            if (File.Exists(cache) && new FileInfo(cache).Length > 0)
                return cache;

            // local → 直接从本地 PNG 渲染头部，命中即返回
            if (string.Equals(account.SkinSource, GameAccount.SkinSourceLocal, StringComparison.OrdinalIgnoreCase))
            {
                var local = ResolveLocalSkinFile(account);
                if (!string.IsNullOrEmpty(local))
                {
                    var bytes = await File.ReadAllBytesAsync(local, cancellationToken);
                    Directory.CreateDirectory(CacheDirectory);
                    await RenderHeadAsync(bytes, cache);
                    return File.Exists(cache) ? cache : null;
                }
                return null; // default 占位：返回 null 由 UI 走 PlayerAvatar 内置 fallback
            }

            // default → 不联网，不渲染；返回 null 让 UI 走 fallback（FontIcon 占位）
            if (string.Equals(account.SkinSource, GameAccount.SkinSourceDefault, StringComparison.OrdinalIgnoreCase))
                return null;

            // microsoft / url：必须有 SkinUrl 才能取，否则走 crafatar 兜底
            var url = account.SkinUrl;
            if (string.IsNullOrWhiteSpace(url) && account.IsMicrosoft)
                url = $"https://crafatar.com/skins/{account.UuidHyphenated}";

            if (string.IsNullOrWhiteSpace(url))
                return await DownloadFallbackAsync(account.Uuid, cache, cancellationToken);

            try
            {
                using var response = await Client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return await DownloadFallbackAsync(account.Uuid, cache, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                Directory.CreateDirectory(CacheDirectory);
                await CacheFullSkinAsync(account.Uuid, bytes);
                await RenderHeadAsync(bytes, cache);
                return File.Exists(cache) ? cache : null;
            }
            catch (Exception ex)
            {
                LauncherLogService.Warn("Avatar", "读取正版皮肤失败：" + ex.Message);
                return await DownloadFallbackAsync(account.Uuid, cache, cancellationToken);
            }
        }

        private static async Task<string?> DownloadFallbackAsync(string uuid, string cache, CancellationToken cancellationToken)
        {
            try
            {
                var url = $"https://crafatar.com/avatars/{uuid}?overlay=true&size=64";
                using var response = await Client.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode) return CachedPath(uuid);
                Directory.CreateDirectory(CacheDirectory);
                await File.WriteAllBytesAsync(cache, await response.Content.ReadAsByteArrayAsync(cancellationToken), cancellationToken);
                return cache;
            }
            catch (Exception)
            {
                return CachedPath(uuid);
            }
        }

        private static async Task RenderHeadAsync(byte[] skinPng, string outputPath)
        {
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(skinPng);
                await writer.StoreAsync();
            }
            input.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(input);
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var src = pixelData.DetachPixelData();
            var srcW = (int)decoder.PixelWidth;
            var srcH = (int)decoder.PixelHeight;
            if (srcW < 16 || srcH < 16) return;

            const int face = 8;
            const int scale = 8;
            var outSize = face * scale;
            var dst = new byte[outSize * outSize * 4];
            Blit(src, srcW, srcH, dst, outSize, 8, 8, scale, overlay: false);
            if (srcW >= 64 && srcH >= 32)
                Blit(src, srcW, srcH, dst, outSize, 40, 8, scale, overlay: true);

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                (uint)outSize, (uint)outSize, 96, 96, dst);
            await encoder.FlushAsync();
            output.Seek(0);

            var buffer = new byte[output.Size];
            using (var reader = new DataReader(output.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)output.Size);
                reader.ReadBytes(buffer);
            }
            await File.WriteAllBytesAsync(outputPath, buffer);
        }

        private static void Blit(byte[] src, int srcW, int srcH, byte[] dst, int outSize, int sx, int sy, int scale, bool overlay)
        {
            const int face = 8;
            if (sx + face > srcW || sy + face > srcH) return;
            for (var y = 0; y < face; y++)
            {
                for (var x = 0; x < face; x++)
                {
                    var si = ((sy + y) * srcW + (sx + x)) * 4;
                    var a = src[si + 3];
                    if (overlay && a < 10) continue;
                    for (var oy = 0; oy < scale; oy++)
                    {
                        for (var ox = 0; ox < scale; ox++)
                        {
                            var di = (((y * scale) + oy) * outSize + (x * scale) + ox) * 4;
                            dst[di] = src[si];
                            dst[di + 1] = src[si + 1];
                            dst[di + 2] = src[si + 2];
                            dst[di + 3] = overlay ? a : (byte)255;
                        }
                    }
                }
            }
        }
    }
}
