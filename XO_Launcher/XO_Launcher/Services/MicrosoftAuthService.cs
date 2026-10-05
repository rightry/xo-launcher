using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 微软正版 Minecraft 登录。
    /// 流程与 HMCL / Prism 相同：授权码 + PKCE，自动打开系统浏览器，
    /// 由本机回环 HTTP 接收回调，再依次换取 Xbox Live、XSTS、Minecraft 令牌。
    /// 不在启动器内嵌入登录页。
    /// </summary>
    public static class MicrosoftAuthService
    {
        /// <summary>
        /// Prism Launcher 公开的 Azure 公共客户端。已注册 localhost 回环，
        /// 并获准调用 Minecraft API；XO Launcher 使用同一套浏览器跳转方案。
        /// </summary>
        public const string AzureClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";

        private const string Scope = "XboxLive.signin offline_access";
        private const string AuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
        private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        public static async Task<GameAccount> LoginWithBrowserAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report("正在准备微软登录...");
            OAuthTokens tokens;
            try
            {
                tokens = await AuthorizeViaLoopbackAsync(progress, cancellationToken);
            }
            catch (MicrosoftAuthException ex) when (ex.CanFallbackToDeviceCode)
            {
                LauncherLogService.Warn("Auth", "本机回环登录不可用，改用设备代码：" + ex.Message);
                tokens = await AuthorizeViaDeviceCodeAsync(progress, cancellationToken);
            }

            progress?.Report("正在完成 Minecraft 正版验证...");
            return await ExchangeForMinecraftAsync(tokens, progress, cancellationToken);
        }

        public static async Task<GameAccount> RefreshAsync(
            GameAccount account,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!account.IsMicrosoft)
                return account;
            if (string.IsNullOrWhiteSpace(account.RefreshToken))
                throw new MicrosoftAuthException("微软登录已失效，请重新登录。");

            progress?.Report("正在刷新微软登录...");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = AzureClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = account.RefreshToken,
                ["scope"] = Scope
            };
            var tokens = await RequestTokensAsync(form, cancellationToken);
            if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
                tokens = tokens with { RefreshToken = account.RefreshToken };
            return await ExchangeForMinecraftAsync(tokens, progress, cancellationToken);
        }

        public static bool NeedsRefresh(GameAccount account)
        {
            if (!account.IsMicrosoft) return false;
            if (string.IsNullOrWhiteSpace(account.AccessToken) || account.AccessToken == "0")
                return true;
            if (account.AccessTokenExpiry is null) return true;
            return account.AccessTokenExpiry.Value <= DateTimeOffset.UtcNow.AddMinutes(2);
        }

        public static async Task<GameAccount> UploadSkinAsync(
            GameAccount account,
            string filePath,
            string variant,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (account is null || !account.IsMicrosoft)
                throw new MicrosoftAuthException("只有微软正版账号可以更换皮肤。");
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new MicrosoftAuthException("请选择一张 PNG 皮肤文件。");
            if (!filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new MicrosoftAuthException("皮肤必须是 PNG 图片。");

            if (NeedsRefresh(account))
                account = await RefreshAsync(account, progress, cancellationToken);

            progress?.Report("正在上传皮肤...");
            var model = string.Equals(variant, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(model), "variant");
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var image = new ByteArrayContent(bytes);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(image, "file", Path.GetFileName(filePath));

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins")
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var response = await Client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("上传皮肤失败：" + Truncate(text));

            var (uuid, name, skin) = await GetMinecraftProfileAsync(account.AccessToken, cancellationToken);
            SkinAvatarService.Invalidate(uuid);
            account.Uuid = uuid;
            account.Username = name;
            account.SkinUrl = skin;
            LauncherLogService.Info("Auth", "已更新正版皮肤：" + name);
            return account;
        }

        private static async Task<OAuthTokens> AuthorizeViaLoopbackAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var verifier = CreateCodeVerifier();
            var challenge = CreateCodeChallenge(verifier);
            var state = CreateCodeVerifier()[..24];
            var port = GetFreePort();
            var redirectUri = $"http://127.0.0.1:{port}/";

            using var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new MicrosoftAuthException("无法在本机建立登录回调，将改用设备代码。", ex)
                {
                    CanFallbackToDeviceCode = true
                };
            }

            var url =
                AuthorizeUrl
                + "?client_id=" + Uri.EscapeDataString(AzureClientId)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&response_mode=query"
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&prompt=select_account"
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&state=" + Uri.EscapeDataString(state);

            progress?.Report("已打开系统浏览器，请在微软页面完成登录");
            OpenBrowser(url);

            HttpListenerContext context;
            var contextTask = listener.GetContextAsync();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    context = await contextTask.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new MicrosoftAuthException("浏览器登录超时，请重试。");
                }
            }
            catch
            {
                try { listener.Stop(); } catch (Exception) { }
                try { await contextTask; } catch (Exception) { }
                throw;
            }

            try
            {
                var query = ParseQuery(context.Request.Url?.Query ?? "");
                WriteCallbackPage(context.Response, query.ContainsKey("code") && !query.ContainsKey("error"));

                if (query.TryGetValue("error", out var error))
                {
                    var desc = query.GetValueOrDefault("error_description") ?? error;
                    if (string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase))
                        throw new MicrosoftAuthException("已取消微软登录。");
                    throw new MicrosoftAuthException("微软登录失败：" + desc);
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                    throw new MicrosoftAuthException("浏览器未返回授权码，请重试。");
                if (query.TryGetValue("state", out var returnedState) && returnedState != state)
                    throw new MicrosoftAuthException("登录校验失败，请重试。");

                progress?.Report("正在向微软换取令牌...");
                var form = new Dictionary<string, string>
                {
                    ["client_id"] = AzureClientId,
                    ["code"] = code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirectUri,
                    ["code_verifier"] = verifier,
                    ["scope"] = Scope
                };
                return await RequestTokensAsync(form, cancellationToken);
            }
            finally
            {
                try { listener.Stop(); } catch (Exception) { }
            }
        }

        private static async Task<OAuthTokens> AuthorizeViaDeviceCodeAsync(
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = AzureClientId,
                    ["scope"] = Scope
                })
            };
            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("无法开始微软设备登录：" + Truncate(body));

            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var deviceCode = root.GetProperty("device_code").GetString()
                ?? throw new MicrosoftAuthException("微软未返回设备代码。");
            var userCode = root.TryGetProperty("user_code", out var uc) ? uc.GetString() : null;
            var interval = root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
            var complete = root.TryGetProperty("verification_uri_complete", out var vu)
                ? vu.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(complete))
                complete = root.TryGetProperty("verification_uri", out var v) ? v.GetString() : "https://www.microsoft.com/link";

            progress?.Report(string.IsNullOrWhiteSpace(userCode)
                ? "已打开系统浏览器，请在微软页面确认登录"
                : $"已打开系统浏览器，如需手动输入代码：{userCode}");
            OpenBrowser(complete!);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, interval)), cancellationToken);

                var poll = new Dictionary<string, string>
                {
                    ["client_id"] = AzureClientId,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode
                };
                using var tokenResponse = await Client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                    {
                        Content = new FormUrlEncodedContent(poll)
                    },
                    cancellationToken);
                var tokenBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
                using var tokenJson = JsonDocument.Parse(string.IsNullOrWhiteSpace(tokenBody) ? "{}" : tokenBody);
                if (tokenJson.RootElement.TryGetProperty("error", out var err))
                {
                    var error = err.GetString();
                    if (error is "authorization_pending" or "slow_down")
                    {
                        if (error == "slow_down") interval += 2;
                        continue;
                    }
                    if (error == "expired_token")
                        throw new MicrosoftAuthException("浏览器登录超时，请重试。");
                    if (error == "access_denied")
                        throw new MicrosoftAuthException("已取消微软登录。");
                    throw new MicrosoftAuthException("微软登录失败：" + error);
                }

                if (!tokenResponse.IsSuccessStatusCode)
                    throw new MicrosoftAuthException("微软登录失败：" + Truncate(tokenBody));
                return ReadTokens(tokenJson.RootElement);
            }
        }

        private static async Task<GameAccount> ExchangeForMinecraftAsync(
            OAuthTokens tokens,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report("正在登录 Xbox Live...");
            var (xblToken, uhs, _) = await AuthenticateXboxLiveAsync(tokens.AccessToken, cancellationToken);

            progress?.Report("正在获取 Minecraft 授权...");
            var (xstsToken, xuid) = await AuthorizeXstsAsync(xblToken, uhs, cancellationToken);

            progress?.Report("正在登录 Minecraft 服务...");
            var (mcToken, expiresIn) = await LoginMinecraftAsync(uhs, xstsToken, cancellationToken);

            await EnsureOwnsJavaEditionAsync(mcToken, cancellationToken);

            progress?.Report("正在读取角色信息...");
            var (uuid, name, skin) = await GetMinecraftProfileAsync(mcToken, cancellationToken);

            LauncherLogService.Info("Auth", "微软正版登录成功：" + name);
            return new GameAccount
            {
                Id = uuid,
                Type = GameAccount.TypeMicrosoft,
                Username = name,
                Uuid = uuid,
                AccessToken = mcToken,
                RefreshToken = tokens.RefreshToken,
                Xuid = string.IsNullOrWhiteSpace(xuid) ? "0" : xuid,
                AccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)),
                SkinUrl = skin
            };
        }

        private static async Task<(string Token, string Uhs, string? Xuid)> AuthenticateXboxLiveAsync(
            string msaAccessToken,
            CancellationToken cancellationToken)
        {
            var ticket = msaAccessToken.StartsWith("d=", StringComparison.Ordinal) || msaAccessToken.StartsWith("t=", StringComparison.Ordinal)
                ? msaAccessToken
                : "d=" + msaAccessToken;
            var payload = new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = ticket
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            };
            using var doc = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", payload, cancellationToken);
            return ReadXboxToken(doc.RootElement, null);
        }

        private static async Task<(string Token, string Xuid)> AuthorizeXstsAsync(
            string xblToken,
            string uhs,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xblToken }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://xsts.auth.xboxlive.com/xsts/authorize")
            {
                Content = JsonContent(payload)
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await Client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized || !response.IsSuccessStatusCode)
                throw MapXboxError(text);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var result = ReadXboxToken(doc.RootElement, uhs);
            return (result.Token, result.Xuid ?? "0");
        }

        private static async Task<(string Token, int ExpiresIn)> LoginMinecraftAsync(
            string uhs,
            string xstsToken,
            CancellationToken cancellationToken)
        {
            var payload = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox")
            {
                Content = JsonContent(payload)
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await Client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new MicrosoftAuthException("Minecraft 服务拒绝了当前登录应用。请稍后重试。");
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("登录 Minecraft 失败：" + Truncate(text));

            using var doc = JsonDocument.Parse(text);
            var token = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new MicrosoftAuthException("Minecraft 未返回访问令牌。");
            var expires = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 86400;
            return (token, expires);
        }

        private static async Task EnsureOwnsJavaEditionAsync(string mcToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("无法确认 Minecraft 购买状态。");
        }

        private static async Task<(string Uuid, string Name, string? Skin)> GetMinecraftProfileAsync(
            string mcToken,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            using var response = await Client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new MicrosoftAuthException("该微软账号没有 Minecraft Java 版角色。请确认已购买正版，或先在官方启动器中创建角色名。");
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("读取 Minecraft 角色失败：" + Truncate(text));

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var uuid = root.GetProperty("id").GetString()
                ?? throw new MicrosoftAuthException("角色信息缺少 UUID。");
            var name = root.GetProperty("name").GetString()
                ?? throw new MicrosoftAuthException("角色信息缺少玩家名。");
            string? skin = null;
            if (root.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in skins.EnumerateArray())
                {
                    if (item.TryGetProperty("state", out var state)
                        && string.Equals(state.GetString(), "ACTIVE", StringComparison.OrdinalIgnoreCase)
                        && item.TryGetProperty("url", out var url))
                    {
                        skin = url.GetString();
                        break;
                    }
                }
            }
            return (uuid, name, skin);
        }

        private static (string Token, string Uhs, string? Xuid) ReadXboxToken(JsonElement root, string? expectedUhs)
        {
            if (root.TryGetProperty("XErr", out var xerr) && xerr.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                throw MapXboxError(root.GetRawText());

            var token = root.GetProperty("Token").GetString()
                ?? throw new MicrosoftAuthException("Xbox 未返回令牌。");
            if (!root.TryGetProperty("DisplayClaims", out var claims)
                || !claims.TryGetProperty("xui", out var xui)
                || xui.GetArrayLength() == 0)
                throw new MicrosoftAuthException("Xbox 返回的账号信息不完整。");

            var first = xui[0];
            var uhs = first.GetProperty("uhs").GetString()
                ?? throw new MicrosoftAuthException("Xbox 返回的账号信息不完整。");
            if (expectedUhs is not null && !string.Equals(expectedUhs, uhs, StringComparison.Ordinal))
                throw new MicrosoftAuthException("Xbox 账号校验失败。");
            var xuid = first.TryGetProperty("xid", out var xid) ? xid.GetString() : null;
            return (token, uhs, xuid);
        }

        private static MicrosoftAuthException MapXboxError(string body)
        {
            long code = 0;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                if (doc.RootElement.TryGetProperty("XErr", out var xerr))
                {
                    if (xerr.ValueKind == JsonValueKind.Number) code = xerr.GetInt64();
                    else long.TryParse(xerr.GetString(), out code);
                }
            }
            catch (JsonException)
            {
                // 使用原始响应
            }

            return code switch
            {
                2148916227 => new MicrosoftAuthException("该 Xbox 账号已被封禁，无法登录。"),
                2148916233 => new MicrosoftAuthException("该微软账号还没有 Xbox 档案。请先打开 xbox.com 登录一次后再试。"),
                2148916235 => new MicrosoftAuthException("当前地区无法使用 Xbox Live。"),
                2148916236 or 2148916237 => new MicrosoftAuthException("该账号需要在 Xbox 页面完成成人验证。"),
                2148916238 => new MicrosoftAuthException("儿童账号需要由成人加入家庭组后才能登录 Minecraft。请到 account.microsoft.com/family 处理。"),
                _ => new MicrosoftAuthException("Xbox 登录失败" + (code == 0 ? "。" : $"（{code}）。"))
            };
        }

        private static async Task<OAuthTokens> RequestTokensAsync(
            Dictionary<string, string> form,
            CancellationToken cancellationToken)
        {
            using var response = await Client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new FormUrlEncodedContent(form)
                },
                cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("向微软换取令牌失败：" + Truncate(text));
            using var doc = JsonDocument.Parse(text);
            return ReadTokens(doc.RootElement);
        }

        private static OAuthTokens ReadTokens(JsonElement root)
        {
            var access = root.GetProperty("access_token").GetString()
                ?? throw new MicrosoftAuthException("微软未返回访问令牌。");
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            return new OAuthTokens(access, refresh);
        }

        private static async Task<JsonDocument> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent(payload)
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await Client.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MicrosoftAuthException("Xbox 登录失败：" + Truncate(text));
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }

        private static StringContent JsonContent(object payload)
            => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        private static void OpenBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                throw new MicrosoftAuthException("无法打开系统浏览器：" + ex.Message, ex);
            }
        }

        private static void WriteCallbackPage(HttpListenerResponse response, bool success)
        {
            var html = success
                ? """
                  <!DOCTYPE html><html><head><meta charset="utf-8"><title>登录成功</title>
                  <style>body{margin:0;background:#0E0E0E;color:#F0F0F0;font-family:"Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", sans-serif;display:flex;align-items:center;justify-content:center;height:100vh}
                  .c{text-align:center}h1{color:#7CBD4B;font-size:22px}p{color:#9E9E9E}</style></head>
                  <body><div class="c"><h1>登录成功</h1><p>可以关闭此页面，返回 XO Launcher。</p></div>
                  <script>setTimeout(function(){window.close();},800);</script></body></html>
                  """
                : """
                  <!DOCTYPE html><html><head><meta charset="utf-8"><title>登录未完成</title>
                  <style>body{margin:0;background:#0E0E0E;color:#F0F0F0;font-family:"Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", sans-serif;display:flex;align-items:center;justify-content:center;height:100vh}
                  .c{text-align:center}h1{color:#E8B4B4;font-size:22px}p{color:#9E9E9E}</style></head>
                  <body><div class="c"><h1>登录未完成</h1><p>可以关闭此页面，返回启动器重试。</p></div></body></html>
                  """;
            var bytes = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            try
            {
                response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
                // 浏览器可能提前断开
            }
            finally
            {
                try { response.Close(); } catch (Exception) { }
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (query.StartsWith('?')) query = query[1..];
            if (string.IsNullOrWhiteSpace(query)) return result;
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
                var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                result[key] = value;
            }
            return result;
        }

        private static string CreateCodeVerifier()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64Url(bytes);
        }

        private static string CreateCodeChallenge(string verifier)
        {
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
            return Base64Url(hash);
        }

        private static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static int GetFreePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        private static string Truncate(string text)
        {
            text = text.Replace('\n', ' ').Trim();
            return text.Length <= 180 ? text : text[..180] + "...";
        }

        private readonly record struct OAuthTokens(string AccessToken, string? RefreshToken);
    }

    public sealed class MicrosoftAuthException : Exception
    {
        public bool CanFallbackToDeviceCode { get; init; }

        public MicrosoftAuthException(string message) : base(message) { }

        public MicrosoftAuthException(string message, Exception inner) : base(message, inner) { }
    }
}
