using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>模型请求的一次工具调用（id 用于回灌 tool 结果）。</summary>
    public sealed record AiToolCall(string Id, string Name, string Arguments);

    /// <summary>对话消息，支持 assistant 的 tool_calls 与 tool 角色结果。</summary>
    public sealed class AiMessage
    {
        public string Role { get; init; } = "user";
        public string Content { get; init; } = "";
        public string? ToolCallId { get; init; }
        public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }

        public static AiMessage System(string c) => new() { Role = "system", Content = c };
        public static AiMessage User(string c) => new() { Role = "user", Content = c };
        public static AiMessage Assistant(string c) => new() { Role = "assistant", Content = c };
        public static AiMessage AssistantTools(IReadOnlyList<AiToolCall> calls)
            => new() { Role = "assistant", ToolCalls = calls };
        public static AiMessage ToolResult(string toolCallId, string content)
            => new() { Role = "tool", ToolCallId = toolCallId, Content = content };
    }

    /// <summary>可供模型调用的工具定义（ParametersJson 为 JSON Schema）。</summary>
    public sealed record AiToolDef(string Name, string Description, string ParametersJson);

    public abstract record AiEvent;
    public sealed record AiThinkEvent(string Text) : AiEvent;
    public sealed record AiTextEvent(string Text) : AiEvent;
    public sealed record AiToolCallsEvent(IReadOnlyList<AiToolCall> Calls) : AiEvent;

    /// <summary>
    /// NVIDIA NIM 上的 z-ai/glm-5.3：流式 Chat Completions，并单独展示思考过程。
    /// </summary>
    public static class AiChatService
    {
        public const string ProviderNvidia = "nvidia";
        public const string ProviderZhipu = "zhipu";
        public const string ProviderOpenRouter = "openrouter";
        public const string ProviderDeepSeek = "deepseek";
        public const string ProviderOpenAi = "openai";
        public const string ProviderSiliconFlow = "siliconflow";
        public const string ProviderQwen = "qwen";
        public const string ProviderCustom = "custom";

        private static readonly HttpClient Client = CreateClient();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>用户在本机填过 API Key 才能用 AI；启动器不再内置任何密钥。</summary>
        public static bool IsConfigured
            => !string.IsNullOrWhiteSpace(LauncherSettings.Instance.AiApiKey);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        public static string NormalizeProvider(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            ProviderDeepSeek => ProviderDeepSeek,
            ProviderOpenAi => ProviderOpenAi,
            ProviderSiliconFlow => ProviderSiliconFlow,
            ProviderQwen => ProviderQwen,
            ProviderCustom => ProviderCustom,
            ProviderZhipu => ProviderZhipu,
            ProviderOpenRouter => ProviderOpenRouter,
            // 可选模型全部内置在 OpenRouter 上，认不出来的服务商一律按 OpenRouter 解析
            _ => ProviderOpenRouter
        };

        public static (string Endpoint, string Model, string Title, string KeyUrl) Preset(string provider)
        {
            return NormalizeProvider(provider) switch
            {
                ProviderDeepSeek => (
                    "https://api.deepseek.com/v1/chat/completions",
                    "deepseek-chat",
                    "DeepSeek",
                    "https://platform.deepseek.com/api_keys"),
                ProviderOpenAi => (
                    "https://api.openai.com/v1/chat/completions",
                    "gpt-4o-mini",
                    "OpenAI",
                    "https://platform.openai.com/api-keys"),
                ProviderSiliconFlow => (
                    "https://api.siliconflow.cn/v1/chat/completions",
                    "deepseek-ai/DeepSeek-V3",
                    "硅基流动",
                    "https://cloud.siliconflow.cn/account/ak"),
                ProviderQwen => (
                    "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                    "qwen-plus",
                    "通义千问",
                    "https://bailian.console.aliyun.com/"),
                ProviderCustom => (
                    "",
                    "",
                    "自定义",
                    ""),
                ProviderZhipu => (
                    "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                    "glm-4.7-flash",
                    "智谱 GLM",
                    "https://open.bigmodel.cn/usercenter/proj-mgmt/apikeys"),
                ProviderOpenRouter => (
                    // 与其它预置一致：这里给的是**完整请求地址**，NormalizeEndpoint 空值兜底时直接用
                    AiModels.OpenRouterEndpoint + "/chat/completions",
                    AiModels.NemotronFree,
                    AiModels.OpenRouterTitle,
                    "https://openrouter.ai/settings/keys"),
                _ => (
                    AiModels.OpenRouterEndpoint + "/chat/completions",
                    AiModels.NemotronFree,
                    AiModels.OpenRouterTitle,
                    "")
            };
        }

        public static string NormalizeEndpoint(string? value, string provider)
        {
            var url = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
                return Preset(provider).Endpoint;

            url = url.TrimEnd('/');
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return url;
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                || url.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)
                || url.EndsWith("/compatible-mode/v1", StringComparison.OrdinalIgnoreCase))
                return url + "/chat/completions";
            if (url.Contains("/api/paas/v4", StringComparison.OrdinalIgnoreCase))
                return url.TrimEnd('/') + "/chat/completions";
            return url + "/v1/chat/completions";
        }

        public static string ResolveModel() => AiModels.Selected.Id;

        public static async IAsyncEnumerable<AiEvent> StreamAsync(
            IReadOnlyList<AiMessage> messages,
            IReadOnlyList<AiToolDef>? tools = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // 模型、端点与 Key 全部来自内置清单里被选中的那一项（用户在设置页只能在这几档之间挑）
            var option = AiModels.Selected;
            var key = option.ApiKey;
            var provider = NormalizeProvider(option.Provider);
            var endpoint = NormalizeEndpoint(option.Endpoint, provider);
            var model = option.Id;
            // 思考开关/推理强度是智谱推理模型专有字段；NVIDIA NIM 与 flash 一类普通模型会拒绝，按需下发。
            var glmThinking = option.SupportsThinking;
            var payload = JsonSerializer.Serialize(new ChatRequest
            {
                Model = model,
                Stream = true,
                Temperature = 0.4,
                MaxTokens = 16384,
                ReasoningEffort = glmThinking ? "high" : null,
                ChatTemplateKwargs = glmThinking
                    ? new ChatTemplateKwargs { Thinking = true, EnableThinking = true }
                    : null,
                Thinking = glmThinking ? new ThinkingToggle { Type = "enabled" } : null,
                Tools = BuildToolDtos(tools),
                ToolChoice = tools is { Count: > 0 } ? "auto" : null,
                Messages = messages.Select(ToDto).ToList()
            }, JsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(bodyStream);

            if (!response.IsSuccessStatusCode)
            {
                var error = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(FormatHttpError((int)response.StatusCode, error));
            }

            var splitter = new ThinkTagSplitter();
            var acc = new ToolCallAccumulator();
            var first = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (first is not null
                && !first.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && first.TrimStart().StartsWith('{'))
            {
                var rest = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                foreach (var ev in ExtractFullChunks(first + rest, splitter, acc))
                    yield return ev;
                if (acc.Any) yield return new AiToolCallsEvent(acc.Build());
                yield break;
            }

            if (first is not null)
            {
                foreach (var ev in ParseSseLine(first, splitter, acc))
                    yield return ev;
            }

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                foreach (var ev in ParseSseLine(line, splitter, acc))
                    yield return ev;
            }

            if (acc.Any) yield return new AiToolCallsEvent(acc.Build());
        }

        public static async Task<string> CompleteAsync(
            IReadOnlyList<AiMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            await foreach (var ev in StreamAsync(messages, null, cancellationToken).ConfigureAwait(false))
            {
                if (ev is AiTextEvent t)
                    sb.Append(t.Text);
            }
            return sb.ToString();
        }

        public static string BuildSystemPrompt()
        {
            var settings = LauncherSettings.Instance;
            var sb = new StringBuilder();
            sb.AppendLine("你是 XO Launcher 的内置助手，帮助用户解决 Minecraft（Java 版与基岩版）启动、下载、模组、崩溃、Java 与内存等问题。");
            sb.AppendLine("用简体中文回答，先给结论再给步骤，不要编造启动器里不存在的按钮或菜单。");
            sb.AppendLine("不确定时明确说不确定。不要要求用户提供 API Key。");
            sb.AppendLine();
            sb.AppendLine("你可以调用工具直接操作启动器：查询已安装版本、切换/新建账号、修改全局设置、读写单个版本的实例详细设置（内存 / Java / JVM 与游戏参数 / 窗口模式与尺寸 / 显示名）、安装或下载版本与资源、导入整合包、管理资源下载库（查看 / 安装到版本 / 删除）、打开页面、启动游戏。");
            sb.AppendLine("规则：");
            sb.AppendLine("- 需要读取当前有哪些版本时用 list_versions，不要凭空猜版本 ID。");
            sb.AppendLine("- 操作资源库前先用 list_library 查看实际条目，按返回的文件名精确指定要安装或删除的条目。");
            sb.AppendLine("- 要针对某一个版本单独调内存 / Java / 启动参数 / 窗口等，用 get_instance_settings 先看现状、再用 set_instance_setting 修改（只影响该版本，不动全局设置）；全局默认值才用 update_setting。");
            sb.AppendLine("- launch_game 会真正启动游戏并等待启动结果返回：成功会说明游戏窗口已显示，失败会给出退出码与故障摘要。");
            sb.AppendLine("- 排查或修复「某版本无法启动 / 崩溃」类问题时，做完修改后必须再用 launch_game 实际启动一次，等到它返回启动结果：确认成功（窗口显示）才算问题解决；若返回失败，就依据退出码与故障摘要继续排查，不要在没验证的情况下宣称已修复。");
            sb.AppendLine("- 除只读查询（list_versions / search_mods / list_library / get_instance_settings）外的工具都会改变启动器状态，执行前会弹窗让用户确认；用户取消时不要重试，改口解释即可。");
            sb.AppendLine("- 要一次查多个模组时，用 search_mods 的 queries 数组（或在 query 里用顿号/逗号分隔）一次查完，它们会并行执行；不要为每个名字各调一次。");
            sb.AppendLine("- 一次只调用必要的工具，拿到结果后再用简体中文向用户说明发生了什么、下一步建议。");
            sb.AppendLine("- 参数值用简体中文或原样 ID；不确定某版本 ID / 加载器名时先查询再操作。");
            sb.AppendLine();
            sb.AppendLine("当前启动器环境：");
            sb.AppendLine("- 启动器版本: " + LauncherLogService.LauncherVersion);
            sb.AppendLine("- 选中版本: " + (string.IsNullOrWhiteSpace(settings.SelectedVersion) ? "（未选择）" : settings.SelectedVersion));
            sb.AppendLine("- 游戏目录: " + settings.GameDirectory);
            sb.AppendLine("- 内存: " + settings.MemoryMB + " MB");
            sb.AppendLine("- Java: " + (settings.AutoMatchJava ? "自动匹配" : (string.IsNullOrWhiteSpace(settings.JavaPath) ? "未配置" : settings.JavaPath)));
            sb.AppendLine("- 下载源: " + settings.DownloadMirror);

            try
            {
                var installed = InstalledVersionService.Find(settings.SelectedVersion);
                if (installed is not null)
                {
                    sb.AppendLine("- 版本类型: " + installed.LoaderLabel);
                    if (!string.IsNullOrWhiteSpace(installed.MinecraftVersion))
                        sb.AppendLine("- Minecraft: " + installed.MinecraftVersion);
                    var instanceDir = GameInstanceLayout.GetDirectory(settings.GameDirectory, installed.Id);
                    var contentRoot = GameInstanceLayout.ResolveContentRoot(settings.GameDirectory, instanceDir);
                    var mods = InstanceModService.Scan(contentRoot);
                    if (mods.Count > 0)
                    {
                        sb.AppendLine("- 已安装模组 (" + mods.Count + "): "
                            + string.Join("、", mods.Take(40).Select(m => m.ShownName + (m.IsEnabled ? "" : "（已禁用）"))));
                    }
                }

                var versions = InstalledVersionService.Scan(settings.GameDirectory);
                if (versions.Count > 0)
                {
                    sb.AppendLine("- 已安装版本: "
                        + string.Join("、", versions.Take(16).Select(v => v.DisplayName)));
                }
            }
            catch (Exception)
            {
                // 扫描失败不影响对话
            }

            var crash = GameCrashService.LastCrash;
            if (crash is not null)
            {
                sb.AppendLine("- 最近崩溃: " + crash.Kind + " / " + crash.VersionId);
                try
                {
                    var analysis = CrashAnalyzer.Analyze(crash);
                    sb.AppendLine("- 本地规则分析: " + analysis.Headline);
                }
                catch (Exception) { }
            }

            return sb.ToString();
        }

        private static IEnumerable<AiEvent> ParseSseLine(string line, ThinkTagSplitter splitter, ToolCallAccumulator acc)
        {
            if (string.IsNullOrWhiteSpace(line)) yield break;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) yield break;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") yield break;
            foreach (var ev in ExtractChunks(data, splitter, acc))
                yield return ev;
        }

        private static IEnumerable<AiEvent> ExtractFullChunks(string json, ThinkTagSplitter splitter, ToolCallAccumulator acc)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException(ReadErrorMessage(err));
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("模型返回了无法解析的内容。");
            }

            foreach (var ev in ExtractChunks(json, splitter, acc))
                yield return ev;
        }

        private static IEnumerable<AiEvent> ExtractChunks(string json, ThinkTagSplitter splitter, ToolCallAccumulator acc)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                yield break;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                    throw new InvalidOperationException(ReadErrorMessage(err));

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    yield break;

                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    foreach (var ev in EventsFromPart(delta, splitter, acc))
                        yield return ev;
                    yield break;
                }

                if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    foreach (var ev in EventsFromPart(message, splitter, acc))
                        yield return ev;
                }
            }
        }

        private static IEnumerable<AiEvent> EventsFromPart(JsonElement part, ThinkTagSplitter splitter, ToolCallAccumulator acc)
        {
            if (part.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                acc.Push(tc);

            var reasoning = ReadReasoning(part);
            if (!string.IsNullOrEmpty(reasoning))
                yield return new AiThinkEvent(reasoning);

            var content = ReadString(part, "content");
            if (string.IsNullOrEmpty(content))
                yield break;

            foreach (var piece in splitter.Push(content))
                yield return piece.IsThinking ? new AiThinkEvent(piece.Text) : new AiTextEvent(piece.Text);
        }

        private static string? ReadReasoning(JsonElement part)
        {
            var direct = ReadString(part, "reasoning_content", "reasoning_text");
            if (!string.IsNullOrEmpty(direct))
                return direct;

            if (!part.TryGetProperty("reasoning", out var reasoning))
                return null;
            if (reasoning.ValueKind == JsonValueKind.String)
                return reasoning.GetString();
            if (reasoning.ValueKind == JsonValueKind.Object)
                return ReadString(reasoning, "content", "text");
            return null;
        }

        private static string? ReadString(JsonElement obj, params string[] names)
        {
            foreach (var name in names)
            {
                if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var value = el.GetString();
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }
            }
            return null;
        }

        private static string ReadErrorMessage(JsonElement err)
        {
            if (err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? "接口返回错误";
            if (err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString() ?? "接口返回错误";
                return err.ToString();
            }
            return "接口返回错误";
        }

        private static string FormatHttpError(int status, string body)
        {
            var detail = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    detail = ReadErrorMessage(err);
            }
            catch (Exception)
            {
                if (detail.Length > 280)
                    detail = detail[..280] + "…";
            }

            return status switch
            {
                401 or 403 => "内置接口暂时不可用，请稍后重试。",
                404 => "接口地址或模型名称不正确：" + detail,
                429 => "请求过于频繁或额度用尽：" + detail,
                _ => $"接口返回 HTTP {status}：{detail}"
            };
        }

        private sealed class ChatRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; } = "";
            [JsonPropertyName("messages")] public List<ChatMessageDto> Messages { get; set; } = new();
            [JsonPropertyName("stream")] public bool Stream { get; set; }
            [JsonPropertyName("temperature")] public double Temperature { get; set; }
            [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
            [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; set; }
            [JsonPropertyName("chat_template_kwargs")] public ChatTemplateKwargs? ChatTemplateKwargs { get; set; }
            [JsonPropertyName("thinking")] public ThinkingToggle? Thinking { get; set; }
            [JsonPropertyName("tools")] public List<ToolDefDto>? Tools { get; set; }
            [JsonPropertyName("tool_choice")] public string? ToolChoice { get; set; }
        }

        private sealed class ChatTemplateKwargs
        {
            [JsonPropertyName("thinking")] public bool Thinking { get; set; }
            [JsonPropertyName("enable_thinking")] public bool EnableThinking { get; set; }
        }

        private sealed class ThinkingToggle
        {
            [JsonPropertyName("type")] public string Type { get; set; } = "enabled";
        }

        private sealed class ChatMessageDto
        {
            [JsonPropertyName("role")] public string Role { get; set; } = "user";
            [JsonPropertyName("content")] public string? Content { get; set; }
            [JsonPropertyName("tool_calls")] public List<ToolCallDto>? ToolCalls { get; set; }
            [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
        }

        private sealed class ToolCallDto
        {
            [JsonPropertyName("id")] public string Id { get; set; } = "";
            [JsonPropertyName("type")] public string Type { get; set; } = "function";
            [JsonPropertyName("function")] public ToolFunctionDto Function { get; set; } = new();
        }

        private sealed class ToolFunctionDto
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}";
        }

        private sealed class ToolDefDto
        {
            [JsonPropertyName("type")] public string Type { get; set; } = "function";
            [JsonPropertyName("function")] public ToolSchemaDto Function { get; set; } = new();
        }

        private sealed class ToolSchemaDto
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("description")] public string Description { get; set; } = "";
            [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
        }

        private static ChatMessageDto ToDto(AiMessage m) => new()
        {
            Role = m.Role,
            Content = string.IsNullOrEmpty(m.Content) ? null : m.Content,
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls?.Select(t => new ToolCallDto
            {
                Id = t.Id,
                Type = "function",
                Function = new ToolFunctionDto { Name = t.Name, Arguments = t.Arguments }
            }).ToList()
        };

        private static List<ToolDefDto>? BuildToolDtos(IReadOnlyList<AiToolDef>? tools)
        {
            if (tools is null || tools.Count == 0) return null;
            var list = new List<ToolDefDto>();
            foreach (var t in tools)
            {
                JsonElement parameters;
                var json = string.IsNullOrWhiteSpace(t.ParametersJson) ? "{}" : t.ParametersJson;
                try { parameters = JsonDocument.Parse(json).RootElement.Clone(); }
                catch (JsonException) { parameters = JsonDocument.Parse("{}").RootElement.Clone(); }
                list.Add(new ToolDefDto
                {
                    Type = "function",
                    Function = new ToolSchemaDto
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = parameters
                    }
                });
            }
            return list;
        }

        /// <summary>跨 chunk 累积 delta.tool_calls 分片（按 index 归并 id/name/arguments）。</summary>
        private sealed class ToolCallAccumulator
        {
            private sealed class Entry
            {
                public string? Id;
                public string? Name;
                public readonly StringBuilder Args = new();
            }

            private readonly SortedDictionary<int, Entry> _calls = new();

            public bool Any => _calls.Count > 0;

            public void Push(JsonElement toolCalls)
            {
                foreach (var el in toolCalls.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var idx = el.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i)
                        ? i
                        : _calls.Count;
                    if (!_calls.TryGetValue(idx, out var entry))
                    {
                        entry = new Entry();
                        _calls[idx] = entry;
                    }
                    if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id)) entry.Id = id;
                    }
                    if (el.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                        {
                            var name = nEl.GetString();
                            if (!string.IsNullOrEmpty(name)) entry.Name = name;
                        }
                        if (fn.TryGetProperty("arguments", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                            entry.Args.Append(aEl.GetString());
                    }
                }
            }

            public List<AiToolCall> Build()
            {
                var list = new List<AiToolCall>();
                foreach (var kv in _calls)
                {
                    var e = kv.Value;
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    var args = e.Args.Length == 0 ? "{}" : e.Args.ToString();
                    list.Add(new AiToolCall(e.Id ?? Guid.NewGuid().ToString("N"), e.Name!, args));
                }
                return list;
            }
        }

        /// <summary>把 content 里的 &lt;think&gt;...&lt;/think&gt; 拆成思考与正文，兼容跨 chunk 切开的标签。</summary>
        private sealed class ThinkTagSplitter
        {
            private enum Mode { Unknown, InThink, AfterThink, Plain }

            private Mode _mode = Mode.Unknown;
            private readonly StringBuilder _buf = new();

            public IEnumerable<(bool IsThinking, string Text)> Push(string piece)
            {
                if (string.IsNullOrEmpty(piece)) yield break;
                _buf.Append(piece);

                while (true)
                {
                    var s = _buf.ToString();
                    switch (_mode)
                    {
                        case Mode.Unknown:
                            if (s.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
                            {
                                _mode = Mode.InThink;
                                _buf.Remove(0, "<think>".Length);
                                continue;
                            }
                            if ("<think>".StartsWith(s, StringComparison.OrdinalIgnoreCase))
                                yield break;
                            _mode = Mode.Plain;
                            continue;

                        case Mode.InThink:
                            var end = IndexOfIgnoreCase(s, "</think>");
                            if (end >= 0)
                            {
                                if (end > 0)
                                    yield return (true, s[..end]);
                                var rest = s[(end + "</think>".Length)..];
                                if (rest.StartsWith('\n') || rest.StartsWith('\r'))
                                    rest = rest.TrimStart('\r', '\n');
                                _buf.Clear();
                                _buf.Append(rest);
                                _mode = Mode.AfterThink;
                                continue;
                            }
                            if (s.Length > 8)
                            {
                                yield return (true, s[..^8]);
                                var keep = s[^8..];
                                _buf.Clear();
                                _buf.Append(keep);
                            }
                            yield break;

                        default:
                            _buf.Clear();
                            if (s.Length > 0)
                                yield return (false, s);
                            yield break;
                    }
                }
            }

            private static int IndexOfIgnoreCase(string text, string value)
                => text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
