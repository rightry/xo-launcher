using System;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XO_Launcher.Views;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 一个可选的模型接入：模型 id + 展示名 + 端点。
    /// 三档全部走 OpenRouter 的 OpenAI 兼容端点；密钥由用户在设置页填进本机配置，仓库里不含任何 Key。
    ///
    /// 图标是「在线取、取不到就用占位字形」的异步资源，所以实现 <see cref="ISelectBoxIconOption"/>
    /// 并用 INotifyPropertyChanged 通知下拉控件：Icon 到达时列表项自己换图，不用重建列表。
    /// </summary>
    internal sealed class AiModelOption : ISelectBoxIconOption, INotifyPropertyChanged
    {
        /// <summary>模型图标还没取到时的占位字形（芯片），保证下拉里永远有一格对齐的图位。</summary>
        public const string DefaultIconGlyph = "";

        public required string Id { get; init; }
        public required string Label { get; init; }
        public required string Provider { get; init; }
        /// <summary>OpenAI 兼容的 BaseURL；实际请求地址由 AiChatService.NormalizeEndpoint 拼出。</summary>
        public required string Endpoint { get; init; }

        /// <summary>这一档用的密钥：一律取用户在本机填的那把，没填就是空串（AI 页与设置页会提示去填）。</summary>
        public string ApiKey => LauncherSettings.Instance.AiApiKey;

        /// <summary>
        /// 是否下发 thinking / reasoning_effort。这是智谱推理模型的专有字段，
        /// OpenRouter 上的模型会拒绝未知参数，所以三档都不发。
        /// </summary>
        public bool SupportsThinking { get; init; }

        public string Note { get; init; } = "";

        /// <summary>模型/服务商图标的在线地址；留空表示这一档只用占位字形。</summary>
        public string IconUrl { get; init; } = "";

        public string IconGlyph { get; init; } = DefaultIconGlyph;

        private BitmapImage? _icon;

        /// <summary>已取到的图标；为空说明还没取到或本机取不到（下拉里显示 <see cref="IconGlyph"/>）。</summary>
        public ImageSource? Icon => _icon;

        public bool HasIcon => _icon is not null;

        public void SetIcon(BitmapImage? icon)
        {
            if (_icon == icon) return;
            _icon = icon;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon)));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    internal static class AiModels
    {
        /// <summary>三档模型挂载的 OpenAI 兼容基础地址（公开信息，不是密钥）。</summary>
        public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1";
        public const string OpenRouterProvider = AiChatService.ProviderOpenRouter;
        public const string OpenRouterTitle = "OpenRouter";

        public const string NemotronFree = "nvidia/nemotron-3-ultra-550b-a55b:free";
        public const string LagunaFree = "poolside/laguna-s-2.1:free";
        public const string QwenFree = "qwen/qwen3.8-27b:free";

        public static readonly AiModelOption[] All =
        {
            new()
            {
                Id = NemotronFree,
                Label = "Nemotron 3 Ultra 550B (free)",
                Provider = AiChatService.ProviderOpenRouter,
                Endpoint = OpenRouterEndpoint,
                // NVIDIA 那边没有可直接引用的方形图标地址（OpenRouter 自己也是回落到 Google 的
                // favicon 服务取，本机取不到），所以这一档固定用占位字形。
                Note = "1M 上下文；实测 3 秒内开始返回，支持工具调用。"
            },
            new()
            {
                Id = LagunaFree,
                Label = "Laguna S 2.1 (free)",
                Provider = AiChatService.ProviderOpenRouter,
                Endpoint = OpenRouterEndpoint,
                IconUrl = "https://poolside.ai/favicon/apple-touch-icon.png",
                Note = "262K 上下文；免费池会偶发 429 限流，遇到时换一档。"
            },
            new()
            {
                Id = QwenFree,
                Label = "Qwen3.8 27B (free)",
                Provider = AiChatService.ProviderOpenRouter,
                Endpoint = OpenRouterEndpoint,
                IconUrl = "https://openrouter.ai/images/icons/Qwen.png",
                Note = "262K 上下文；免费池会偶发 429 限流，遇到时换一档。"
            },
        };

        public static AiModelOption? Find(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            foreach (var option in All)
                if (string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
                    return option;
            return null;
        }

        /// <summary>当前选中的模型；设置里的值认不出来时回落到第一项（内置默认）。</summary>
        public static AiModelOption Selected
            => Find(LauncherSettings.Instance.AiModel) ?? All[0];

        public static string RequestUrl(AiModelOption option)
            => AiChatService.NormalizeEndpoint(option.Endpoint, option.Provider);
    }
}
