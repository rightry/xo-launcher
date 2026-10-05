using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using XO_Launcher.Services;

namespace XO_Launcher.Views
{
    public sealed partial class AiPage : UserControl
    {
        private readonly List<(string Role, string Content)> _history = new();
        private readonly List<DialogueTurn> _turns = new();
        private CancellationTokenSource? _cts;
        private bool _busy;
        private string? _pendingApiPrompt;

        private bool _syncingModel;

        public AiPage()
        {
            InitializeComponent();
            _syncingModel = true;
            ModelSelect.ItemsSource = AiModels.All;
            ModelSelect.SelectedItem = AiModels.Selected;
            _syncingModel = false;
            SelectPermission(LauncherSettings.Instance.AiPermission);
            AiModelIconService.Start();
            Loaded += (_, _) => RefreshBanner();
            LauncherSettings.Instance.Changed += () => DispatcherQueue.TryEnqueue(RefreshBanner);
        }

        /// <summary>把设置里的权限档落到下拉上（内联项在页面构造后才可见，所以走 SelectedItem 匹配 Tag）。</summary>
        private void SelectPermission(string permission)
        {
            var match = PermissionSelect.Items.FirstOrDefault(i => i.Tag is string tag
                && string.Equals(tag, permission, StringComparison.Ordinal));
            if (match is not null) PermissionSelect.SelectedItem = match;
        }

        private void PermissionSelect_SelectionChanged(object sender, EventArgs e)
        {
            if (PermissionSelect.SelectedItem is not SelectBoxItem { Tag: string tag }) return;
            LauncherSettings.Instance.AiPermission = tag;
        }

        public void RefreshBanner()
        {
            SetupBanner.Visibility = Visibility.Collapsed;
            var option = AiModels.Selected;
            SubtitleText.Text = $"当前模型 {option.Label} · OpenRouter。可分析崩溃、模组与启动问题。";
            // 设置页也可能改模型，这里同步下拉；_syncingModel 挡住回写，避免两边互相触发
            _syncingModel = true;
            try { ModelSelect.SelectedItem = option; }
            finally { _syncingModel = false; }
        }

        private void ModelSelect_SelectionChanged(object sender, EventArgs e)
        {
            if (_syncingModel) return;
            if (ModelSelect.SelectedItem is not AiModelOption option) return;
            LauncherSettings.Instance.AiModel = option.Id;
            RefreshBanner();
        }

        /// <summary>
        /// 首次使用 AI 前必须读一次条款：内容涉及「答案可能错、提问会发给第三方、写操作要确认」。
        /// 不同意就不发起请求，输入内容原样留着。
        /// </summary>
        private const string AiTerms =
            "使用 AI 助手前请知悉：\n\n"
            + "· 回答由第三方大模型生成，可能包含看似合理但实际错误的信息，甚至虚构不存在的模组、版本号或参数。据此改动游戏前请自行核实。\n"
            + "· 你的提问与必要的上下文会发送到 OpenRouter（openrouter.ai）及其上游模型服务处理；请勿在此输入密码、令牌等隐私信息。\n"
            + "· AI 可以调用启动器工具读取版本、日志等信息；涉及写入或删改的操作（如安装模组、修改设置）都会先弹出确认，未经你点击确认不会执行。输入框旁的「权限」默认是每次确认，改成全部自动后不再弹确认（包括删除资源库文件），请自行斟酌。\n"
            + "· 免费模型池可能限流或临时不可用，可以在输入框左侧换一个模型。\n"
            + "· 继续使用即表示你自行承担由此产生的后果。";

        private async Task<bool> EnsureTermsAgreedAsync()
        {
            var settings = LauncherSettings.Instance;
            if (settings.AiAgreed) return true;
            var result = await AppDialog.ShowAsync(
                XamlRoot, "AI 使用说明", AiTerms,
                primary: "我已阅读并同意", close: "不同意", maxWidth: 640);
            var agreed = result == AppDialogResult.Primary;
            if (agreed) settings.AiAgreed = true;
            return agreed;
        }

        /// <summary>从其它页面带入问题。display 显示在气泡里，apiPrompt 为发给模型的完整内容。</summary>
        public void EnqueuePrompt(string display, string? apiPrompt = null, bool autoSend = false)
        {
            _pendingApiPrompt = apiPrompt;
            InputBox.Text = display ?? "";
            RefreshBanner();
            if (autoSend)
                _ = SendAsync();
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow window)
                window.OpenAiSettings();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            StopStreaming();
            _history.Clear();
            _turns.Clear();
            ChatHost.Children.Clear();
            EmptyPanel.Visibility = Visibility.Visible;
            InputBox.Text = "";
            _pendingApiPrompt = null;
            UpdateRetryLast();
        }

        private void RetryLast_Click(object sender, RoutedEventArgs e)
        {
            if (_turns.Count == 0 || _busy) return;
            _ = SendAsync(_turns[^1]);
        }

        private void SuggestCrash_Click(object sender, RoutedEventArgs e)
        {
            var crash = GameCrashService.LastCrash ?? GameCrashService.Collect();
            var packet = CrashAnalyzer.BuildAssistantPacket(crash);
            EnqueuePrompt(
                "请根据最近一次崩溃日志做深度分析，给出最可能的原因和具体解决步骤。",
                "请根据下面的崩溃材料做深度分析。先给出最可能的原因，再给出在 XO Launcher 里可操作的步骤（版本设置、模组管理、Java、内存等）。如果材料不足，说明还缺哪份日志。\n\n"
                + packet,
                autoSend: true);
        }

        private void SuggestJava_Click(object sender, RoutedEventArgs e)
            => EnqueuePrompt("我当前选中的 Minecraft 版本应该用哪个 Java？启动器里该怎么选？", autoSend: true);

        private void SuggestMods_Click(object sender, RoutedEventArgs e)
            => EnqueuePrompt("根据当前版本和加载器，推荐几款常用的优化模组，并说明不要和哪些模组一起装。", autoSend: true);

        private void SuggestMemory_Click(object sender, RoutedEventArgs e)
            => EnqueuePrompt("我应该给这个版本分配多少内存？太多或太少分别会怎样？", autoSend: true);

        private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

        private void Stop_Click(object sender, RoutedEventArgs e) => StopStreaming();

        private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
            e.Handled = true;
            _ = SendAsync();
        }

        private async Task SendAsync(DialogueTurn? retry = null)
        {
            if (_busy) return;
            if (!AiChatService.IsConfigured)
            {
                var go = await AppDialog.ShowAsync(
                    XamlRoot, "还没有填写 API Key",
                    "启动器不内置任何模型密钥。请先在「设置 → AI 助手」里填入你自己的 OpenRouter（或兼容端点）密钥，再回来提问。",
                    primary: "打开设置", close: "稍后");
                if (go == AppDialogResult.Primary && App.MainWindow is MainWindow window)
                    window.OpenAiSettings();
                return;
            }
            if (!await EnsureTermsAgreedAsync()) return;

            DialogueTurn dialogue;
            AssistantTurn turn;
            if (retry is not null)
            {
                var index = _turns.IndexOf(retry);
                if (index < 0) return;
                TruncateAfter(index);
                retry.AssistantText = "";
                retry.Assistant.Reset();
                dialogue = retry;
                turn = retry.Assistant;
                RebuildHistory(includeCurrentAssistant: false);
            }
            else
            {
                var display = (InputBox.Text ?? "").Trim();
                var api = string.IsNullOrWhiteSpace(_pendingApiPrompt) ? display : _pendingApiPrompt.Trim();
                _pendingApiPrompt = null;
                if (string.IsNullOrWhiteSpace(api)) return;

                InputBox.Text = "";
                EmptyPanel.Visibility = Visibility.Collapsed;
                var userCard = AddUserBubble(display);
                turn = AddAssistantTurn();
                dialogue = new DialogueTurn
                {
                    Display = display,
                    Api = api,
                    UserCard = userCard,
                    Assistant = turn
                };
                _turns.Add(dialogue);
                _history.Add(("user", api));
                while (_history.Count > 20)
                    _history.RemoveAt(0);
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            var answer = new StringBuilder();
            var segment = new StringBuilder();
            var anyThink = false;
            // 打字机：正文按固定节奏逐字揭开，而不是一到就整段刷上去。只能在 UI 线程上碰。
            var typewriter = new Typewriter(text =>
            {
                turn.ShowAnswer(text);
                ScrollToEnd();
            });

            var messages = new List<AiMessage> { AiMessage.System(AiChatService.BuildSystemPrompt()) };
            foreach (var h in _history)
                messages.Add(new AiMessage { Role = h.Role, Content = h.Content });

            try
            {
                var round = 0;
                var streamToken = _cts.Token;
                while (true)
                {
                    IReadOnlyList<AiToolCall>? pending = null;
                    var thinking = new StringBuilder();
                    var lastThinkUi = DateTime.MinValue;
                    Task<ThinkBlock>? thinkBlock = null;
                    var thinkFinished = false;

                    // 每轮思考使用独立思考块；本轮开始输出正文或调用工具时折叠定稿
                    void FinalizeThink()
                    {
                        if (thinkBlock is null || thinkFinished) return;
                        thinkFinished = true;
                        var block = thinkBlock;
                        DispatcherQueue.TryEnqueue(() => block.Result.Finish());
                    }

                    await foreach (var ev in AiChatService
                        .StreamAsync(messages, LauncherToolService.Catalog, streamToken)
                        .ConfigureAwait(false))
                    {
                        switch (ev)
                        {
                            case AiThinkEvent t:
                                anyThink = true;
                                thinking.Append(t.Text);
                                thinkBlock ??= AddThinkBlockAsync(turn);
                                if ((DateTime.UtcNow - lastThinkUi).TotalMilliseconds < 50) break;
                                lastThinkUi = DateTime.UtcNow;
                                var thinkSnapshot = thinking.ToString();
                                var captured = thinkBlock;
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    captured.Result.Show(thinkSnapshot, streaming: true);
                                    ScrollToEnd();
                                });
                                break;

                            case AiTextEvent x:
                                FinalizeThink();
                                answer.Append(x.Text);
                                segment.Append(x.Text);
                                var delta = x.Text;
                                DispatcherQueue.TryEnqueue(() => typewriter.Append(delta));
                                break;

                            case AiToolCallsEvent tc:
                                pending = tc.Calls;
                                break;
                        }
                    }
                    FinalizeThink();

                    if (pending is null || pending.Count == 0) break;
                    if (++round > MaxToolRounds)
                    {
                        const string note = "（已达单轮工具调用上限，停止继续调用。）";
                        answer.Append(answer.Length > 0 ? "\n\n" : "").Append(note);
                        segment.Append(segment.Length > 0 ? "\n\n" : "").Append(note);
                        var noteSnapshot = segment.ToString();
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            typewriter.Flush();
                            turn.ShowAnswer(noteSnapshot);
                        });
                        break;
                    }

                    messages.Add(AiMessage.AssistantTools(pending));
                    foreach (var call in pending)
                    {
                        if (streamToken.IsCancellationRequested)
                        {
                            messages.Add(AiMessage.ToolResult(call.Id, "用户停止了本次对话，未执行。"));
                            continue;
                        }
                        var describe = LauncherToolService.Describe(call.Name, call.Arguments);
                        if (segment.Length > 0)
                        {
                            segment.Clear();
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                typewriter.Flush();   // 已经收到的字先落地，别被下一段冲掉
                                typewriter.Reset();
                                turn.BeginAnswerSegment();
                            });
                        }
                        var card = await AddToolCardAsync(turn, describe).ConfigureAwait(false);

                        string result;
                        if (LauncherToolService.NeedsConfirmation(call.Name, call.Arguments))
                        {
                            NotificationService.AiConfirm(describe);

                            var confirm = await AddConfirmCardAsync(turn, describe).ConfigureAwait(false);
                            var ok = await confirm.WaitAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                            if (ok)
                            {
                                result = await ExecuteToolAsync(call, card).ConfigureAwait(false);
                            }
                            else
                            {
                                card.MarkCanceled("未执行：用户未确认此操作。");
                                result = "用户取消了此操作，未执行。";
                            }
                        }
                        else
                        {
                            result = await ExecuteToolAsync(call, card).ConfigureAwait(false);
                        }

                        messages.Add(AiMessage.ToolResult(call.Id, result));
                    }
                }

                var full = answer.ToString().Trim();
                var lastSegment = segment.ToString().Trim();
                string? fallback = null;
                if (string.IsNullOrWhiteSpace(full))
                    fallback = anyThink
                        ? "模型只返回了思考过程，没有给出最终回答。请再试一次。"
                        : "模型没有返回内容。请稍后重试。";
                FinishTurn(dialogue, fallback ?? full);
                DispatcherQueue.TryEnqueue(() =>
                {
                    typewriter.Flush();
                    if (fallback is not null)
                        turn.ShowAnswer(fallback);
                    else if (lastSegment.Length > 0)
                        turn.ShowAnswer(lastSegment);   // 收尾用去掉首尾空白的定稿文本再渲染一次
                    ScrollToEnd();
                });
            }
            catch (OperationCanceledException)
            {
                var text = answer.ToString().Trim();
                var stopped = string.IsNullOrWhiteSpace(text);
                FinishTurn(dialogue, stopped ? "已停止生成。" : text);
                DispatcherQueue.TryEnqueue(() =>
                {
                    typewriter.Flush();   // 停在中途也要把已收到的字全部落地
                    if (stopped)
                    {
                        turn.BeginAnswerSegment();
                        turn.ShowAnswer("已停止生成。");
                    }
                    ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AI", "对话失败", ex);
                var text = "请求失败：" + ex.Message;
                FinishTurn(dialogue, text);
                DispatcherQueue.TryEnqueue(() =>
                {
                    typewriter.Flush();
                    turn.BeginAnswerSegment();
                    turn.ShowAnswer(text);
                    ScrollToEnd();
                });
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                DispatcherQueue.TryEnqueue(() => SetBusy(false));
            }
        }

        private const int MaxToolRounds = 6;

        /// <summary>新一轮思考开始时插入一个独立思考块（UI 线程创建）。</summary>
        private Task<ThinkBlock> AddThinkBlockAsync(AssistantTurn turn)
        {
            var tcs = new TaskCompletionSource<ThinkBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(() =>
            {
                var block = turn.AddThink();
                ScrollToEnd();
                tcs.TrySetResult(block);
            });
            return tcs.Task;
        }

        /// <summary>在助手气泡里插入一张工具卡片（UI 线程创建）。</summary>
        private Task<ToolCard> AddToolCardAsync(AssistantTurn turn, string describe)
        {
            var tcs = new TaskCompletionSource<ToolCard>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(() =>
            {
                var card = turn.AddTool(describe);
                ScrollToEnd();
                tcs.TrySetResult(card);
            });
            return tcs.Task;
        }

        /// <summary>在助手气泡里插入一张确认卡片，等待用户点击「执行 / 取消」。</summary>
        private Task<ConfirmCard> AddConfirmCardAsync(AssistantTurn turn, string describe)
        {
            var tcs = new TaskCompletionSource<ConfirmCard>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(() =>
            {
                var card = turn.AddConfirm(describe);
                ScrollToEnd();
                tcs.TrySetResult(card);
            });
            return tcs.Task;
        }

        private Task<string> ExecuteToolAsync(AiToolCall call, ToolCard? card)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = _cts?.Token ?? CancellationToken.None;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    card?.MarkRunning();
                    string result;
                    if (LauncherToolService.IsGuiTool(call.Name))
                    {
                        var req = LauncherToolService.BuildRequest(call.Name, call.Arguments);
                        if (call.Name == LauncherToolService.ToolLaunchGame)
                        {
                            // 启动游戏改为同步等待「启动结果」：窗口出现即判成功，进程早退/崩溃则回传退出码与故障分析，
                            // 让模型据此判断（例如修复启动问题后）是否真的解决，而不是只回一句「已触发」。
                            result = App.MainWindow is MainWindow launchWindow
                                ? await launchWindow.LaunchGameForAiAsync(req, token)
                                : "无法启动：主窗口不可用。";
                        }
                        else
                        {
                            if (App.MainWindow is MainWindow window)
                                window.ApplyCliIntent(req);
                            result = "已切换到对应页面。";
                        }
                    }
                    else
                    {
                        result = await LauncherToolService.ExecuteAsync(call.Name, call.Arguments, token);
                    }
                    card?.MarkDone(result);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    LauncherLogService.Error("AI", "工具执行失败：" + call.Name, ex);
                    card?.MarkFailed(ex.Message);
                    tcs.TrySetResult("执行失败：" + ex.Message);
                }
            });
            return tcs.Task;
        }

        /// <summary>代码里新建 FontIcon 的快捷方式（务必带上 IconFontFamily，否则字形不显示）。</summary>
        private static FontIcon Glyph(string g, double size, string brushKey) => new()
        {
            FontFamily = ThemeService.IconFontFamily,
            Glyph = g,
            FontSize = size,
            Foreground = ThemeService.Brush(brushKey)
        };

        private static Border CircleAvatar(string glyph, string bgBrush, string fgBrush) => new()
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = ThemeService.Brush(bgBrush),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                FontFamily = ThemeService.IconFontFamily,
                Glyph = glyph,
                FontSize = 15,
                Foreground = ThemeService.Brush(fgBrush),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        private static Grid AvatarRow(FrameworkElement avatar, FrameworkElement content, bool avatarOnLeft)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var first = avatarOnLeft ? avatar : content;
            var second = avatarOnLeft ? content : avatar;
            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 1);
            row.Children.Add(first);
            row.Children.Add(second);
            row.HorizontalAlignment = avatarOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            return row;
        }

        private void StopStreaming()
        {
            try { _cts?.Cancel(); }
            catch (Exception) { }
        }

        private void FinishTurn(DialogueTurn dialogue, string text)
        {
            dialogue.AssistantText = text;
            if (_history.Count == 0 || _history[^1].Role != "assistant")
                _history.Add(("assistant", text));
            else
                _history[^1] = ("assistant", text);
            while (_history.Count > 20)
                _history.RemoveAt(0);
        }

        private void TruncateAfter(int index)
        {
            for (var i = _turns.Count - 1; i > index; i--)
            {
                ChatHost.Children.Remove(_turns[i].UserCard);
                ChatHost.Children.Remove(_turns[i].Assistant.Card);
                _turns.RemoveAt(i);
            }
        }

        private void RebuildHistory(bool includeCurrentAssistant)
        {
            _history.Clear();
            for (var i = 0; i < _turns.Count; i++)
            {
                var item = _turns[i];
                _history.Add(("user", item.Api));
                var last = i == _turns.Count - 1;
                if (!string.IsNullOrWhiteSpace(item.AssistantText) && (!last || includeCurrentAssistant))
                    _history.Add(("assistant", item.AssistantText));
            }
            while (_history.Count > 20)
                _history.RemoveAt(0);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SendButton.IsEnabled = !busy;
            // 生成中途换模型会让这一轮前后混用两个模型，直接锁住下拉
            ModelSelect.IsEnabled = !busy;
            StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            InputBox.IsEnabled = !busy;
            UpdateRetryLast();
            foreach (var item in _turns)
            {
                item.Assistant.ShowRetry(!busy);
                item.Assistant.ShowDisclaimer(!busy);
            }
        }

        private void UpdateRetryLast()
        {
            if (RetryLastButton is null) return;
            RetryLastButton.Visibility = _turns.Count > 0 && !_busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private UIElement AddUserBubble(string text)
        {
            var bubble = new Border
            {
                Background = ThemeService.Brush("AccentBrush"),
                CornerRadius = new CornerRadius(12, 4, 12, 12),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 560,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.Brush("AccentOnBrush")
                }
            };

            var meta = new TextBlock
            {
                Text = "我 · " + DateTime.Now.ToString("HH:mm"),
                FontSize = 11,
                Foreground = ThemeService.Brush("TextTertiaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 2, 3)
            };

            var column = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 600
            };
            column.Children.Add(meta);
            column.Children.Add(bubble);

            var row = AvatarRow(
                CircleAvatar("\uE789", "NeutralButtonBrush", "TextPrimaryBrush"),
                column,
                avatarOnLeft: false);

            ChatHost.Children.Add(row);
            ScrollToEnd();
            return row;
        }

        private AssistantTurn AddAssistantTurn()
        {
            var turn = AssistantTurn.Create();
            turn.RetryButton.Click += (_, _) =>
            {
                var owner = _turns.Find(t => t.Assistant == turn);
                if (owner is not null)
                    _ = SendAsync(owner);
            };
            ChatHost.Children.Add(turn.Card);
            ScrollToEnd();
            return turn;
        }

        private sealed class DialogueTurn
        {
            public required string Display { get; init; }
            public required string Api { get; init; }
            public required UIElement UserCard { get; init; }
            public required AssistantTurn Assistant { get; init; }
            public string AssistantText { get; set; } = "";
        }

        private sealed class AssistantTurn
        {
            public required UIElement Card { get; init; }
            public required StackPanel Flow { get; init; }
            public required RichTextBlock Answer { get; init; }
            public required Button RetryButton { get; init; }
            public required TextBlock Disclaimer { get; init; }

            private bool _placeholder = true;

            public static AssistantTurn Create()
            {
                var answer = new RichTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(2, 2, 2, 0)
                };
                MarkdownRenderer.Render(answer, "正在思考…");

                var retry = new Button
                {
                    Content = "重新提问",
                    Height = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = 11,
                    Background = ThemeService.Brush("NeutralButtonBrush"),
                    Foreground = ThemeService.Brush("NeutralButtonForegroundBrush"),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0),
                    Visibility = Visibility.Collapsed
                };

                // 时间顺序流：思考块 / 工具卡片 / 确认卡片依次插入，回答与重试按钮固定在末尾
                var flow = new StackPanel { Spacing = 8 };
                flow.Children.Add(answer);
                flow.Children.Add(retry);

                // 回答卡片自带一层亚克力底：开全景背景时正文直接压在画面上会读不清
                var card = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MaxWidth = 680,
                    Background = ThemeService.Brush("CardAcrylicBrush"),
                    BorderBrush = ThemeService.Brush("DividerBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(16, 12, 16, 12),
                    Child = flow
                };

                var disclaimer = new TextBlock
                {
                    Text = "内容由 AI 生成，可能存在错误，请自行核实后再据此操作。",
                    FontSize = 11,
                    Foreground = ThemeService.Brush("TextTertiaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 6, 0, 0),
                    Visibility = Visibility.Collapsed
                };

                var meta = new TextBlock
                {
                    Text = "AI 助手 · " + DateTime.Now.ToString("HH:mm"),
                    FontSize = 11,
                    Foreground = ThemeService.Brush("TextTertiaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 0, 0, 4)
                };

                var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
                column.Children.Add(meta);
                column.Children.Add(card);
                column.Children.Add(disclaimer);

                var root = AvatarRow(
                    CircleAvatar("\uE9AC", "AccentDarkBrush", "AccentOnBrush"),
                    column,
                    avatarOnLeft: true);

                return new AssistantTurn
                {
                    Card = root,
                    Flow = flow,
                    Answer = answer,
                    RetryButton = retry,
                    Disclaimer = disclaimer
                };
            }

            private RichTextBlock? _currentAnswer;

            private int InsertIndex => Flow.Children.Count - 1;

            private void ClearPlaceholder()
            {
                if (!_placeholder) return;
                _placeholder = false;
                Flow.Children.Remove(Answer);
            }

            /// <summary>结束当前回答分段：之后的正文在已插入的卡片后面新开一块。</summary>
            public void BeginAnswerSegment() => _currentAnswer = null;

            /// <summary>新一轮思考：插入独立思考块，不复用之前的思考框。</summary>
            public ThinkBlock AddThink()
            {
                ClearPlaceholder();
                var block = new ThinkBlock();
                Flow.Children.Insert(InsertIndex, block.Root);
                return block;
            }

            /// <summary>插入一张工具卡片（执行状态在卡片上就地更新）；后续正文自动新开分段。</summary>
            public ToolCard AddTool(string describe)
            {
                ClearPlaceholder();
                var card = new ToolCard(describe);
                Flow.Children.Insert(InsertIndex, card.Root);
                _currentAnswer = null;
                return card;
            }

            /// <summary>插入一张内联确认卡片（代替模态弹窗）。</summary>
            public ConfirmCard AddConfirm(string describe)
            {
                ClearPlaceholder();
                var card = new ConfirmCard(describe);
                Flow.Children.Insert(InsertIndex, card.Root);
                _currentAnswer = null;
                return card;
            }

            public void Reset()
            {
                while (Flow.Children.Count > 1)
                    Flow.Children.RemoveAt(0);
                _currentAnswer = null;
                _placeholder = true;
                MarkdownRenderer.Render(Answer, "正在思考…");
                Flow.Children.Insert(0, Answer);
                ShowRetry(false);
            }

            public void ShowRetry(bool visible)
            {
                RetryButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            /// <summary>生成结束后才露出「AI 生成不可信」这行，避免边打字边提示。</summary>
            public void ShowDisclaimer(bool visible)
            {
                Disclaimer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            /// <summary>把正文渲染进当前分段块；分段不存在时在流的末尾（重试按钮前）新建一块。</summary>
            public void ShowAnswer(string markdown)
            {
                ClearPlaceholder();
                if (_currentAnswer is null)
                {
                    _currentAnswer = new RichTextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        Margin = new Thickness(2, 2, 2, 0)
                    };
                    Flow.Children.Insert(InsertIndex, _currentAnswer);
                }
                MarkdownRenderer.Render(_currentAnswer, markdown);
            }
        }

        /// <summary>
        /// 打字机：把流式到达的正文按固定节奏逐字揭开，而不是一收到就整段刷上去。
        /// 模型吐字远快于动画，所以积压越多每帧揭开越多（自适应追赶）—— 否则答案早结束了字还在慢慢爬；
        /// 积压清空后定时器自动停。只能在 UI 线程上用（Tick 直接重渲染 RichTextBlock）。
        /// </summary>
        private sealed class Typewriter
        {
            private const int TickMs = 20;
            private const int CatchUpThreshold = 240;
            private const int NormalStep = 3;

            private readonly DispatcherTimer _timer;
            private readonly Action<string> _reveal;
            private readonly StringBuilder _pending = new();
            private readonly StringBuilder _shown = new();

            public Typewriter(Action<string> reveal)
            {
                _reveal = reveal;
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
                _timer.Tick += (_, _) => Tick();
            }

            public void Append(string delta)
            {
                if (string.IsNullOrEmpty(delta)) return;
                _pending.Append(delta);
                if (!_timer.IsEnabled) _timer.Start();
            }

            /// <summary>立刻把剩下的字全部揭开：本轮结束、被停止、报错或要另起一段之前都走这里。</summary>
            public void Flush()
            {
                _timer.Stop();
                if (_pending.Length == 0) return;
                _shown.Append(_pending.ToString());
                _pending.Clear();
                _reveal(_shown.ToString());
            }

            /// <summary>正文另起一段（工具卡片之后）：丢掉缓冲，从空开始。</summary>
            public void Reset()
            {
                _timer.Stop();
                _pending.Clear();
                _shown.Clear();
            }

            private void Tick()
            {
                if (_pending.Length == 0)
                {
                    _timer.Stop();
                    return;
                }
                var step = _pending.Length > CatchUpThreshold
                    ? Math.Max(4, _pending.Length / 12)
                    : NormalStep;
                step = Math.Min(step, _pending.Length);
                _shown.Append(_pending.ToString(0, step));
                _pending.Remove(0, step);
                _reveal(_shown.ToString());
            }
        }

        /// <summary>单轮思考过程块：流式输出时展开，定稿后折叠为「思考过程」。</summary>
        private sealed class ThinkBlock
        {
            public Border Root { get; }
            private readonly Expander _expander;
            private readonly TextBlock _header;
            private readonly TextBlock _body;

            public ThinkBlock()
            {
                _header = new TextBlock
                {
                    Text = "正在思考…",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                };
                _body = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                };

                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var icon = Glyph("\uEA89", 12, "TextSecondaryBrush");
                icon.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(icon);
                head.Children.Add(_header);

                _expander = new Expander
                {
                    Header = head,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 220,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = _body
                    },
                    IsExpanded = true,
                    Background = null,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };

                Root = new Border
                {
                    Background = ThemeService.Brush("RecessedBackgroundBrush"),
                    BorderBrush = ThemeService.Brush("DividerBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 2, 8, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = _expander
                };
            }

            public void Show(string text, bool streaming)
            {
                _header.Text = streaming ? "正在思考…" : "思考过程";
                _body.Text = text;
            }

            public void Finish()
            {
                _header.Text = "思考过程";
                _expander.IsExpanded = false;
            }
        }

        /// <summary>单条工具调用卡片：状态图标 + 操作描述 + 结果摘要（仿 Qoder 风格）。</summary>
        private sealed class ToolCard
        {
            private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 63, 185, 80));
            private static readonly SolidColorBrush FailBrush = new(Color.FromArgb(255, 229, 72, 77));

            public Border Root { get; }
            private readonly Grid _statusHost;
            private readonly FontIcon _statusIcon;
            private readonly ProgressRing _spinner;
            private readonly TextBlock _result;
            private bool _expanded;

            public ToolCard(string describe)
            {
                _statusIcon = new FontIcon
                {
                    FontFamily = ThemeService.IconFontFamily,
                    Glyph = "\uE921",
                    FontSize = 12,
                    Foreground = ThemeService.Brush("TextSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _spinner = new ProgressRing
                {
                    Width = 13,
                    Height = 13,
                    IsActive = false,
                    Visibility = Visibility.Collapsed,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _statusHost = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                _statusHost.Children.Add(_statusIcon);
                _statusHost.Children.Add(_spinner);

                var title = new TextBlock
                {
                    Text = describe,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeService.Brush("TextPrimaryBrush")
                };
                _result = new TextBlock
                {
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    // 默认只露 6 行，点一下展开；失败与取消直接全展开（见 ShowResult）。
                    MaxLines = 6,
                    IsTextSelectionEnabled = true,
                    Visibility = Visibility.Collapsed,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                };
                ToolTipService.SetToolTip(_result, "点击展开 / 收起完整内容");
                _result.Tapped += (_, _) =>
                {
                    _expanded = !_expanded;
                    _result.MaxLines = _expanded ? 0 : 6;
                };

                var head = new Grid { ColumnSpacing = 8 };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(_statusHost, 0);
                Grid.SetColumn(title, 1);
                head.Children.Add(_statusHost);
                head.Children.Add(title);

                var stack = new StackPanel { Spacing = 4 };
                stack.Children.Add(head);
                stack.Children.Add(_result);

                Root = new Border
                {
                    Background = ThemeService.Brush("RecessedBackgroundBrush"),
                    BorderBrush = ThemeService.Brush("DividerBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 7, 10, 7),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = stack
                };
            }

            public void MarkRunning()
            {
                _statusIcon.Visibility = Visibility.Collapsed;
                _spinner.Visibility = Visibility.Visible;
                _spinner.IsActive = true;
            }

            public void MarkDone(string result)
            {
                _spinner.IsActive = false;
                _spinner.Visibility = Visibility.Collapsed;
                _statusIcon.Visibility = Visibility.Visible;
                _statusIcon.Glyph = "\uE73E";
                _statusIcon.Foreground = SuccessBrush;
                ShowResult(result, expanded: false);
            }

            public void MarkFailed(string message)
            {
                _spinner.IsActive = false;
                _spinner.Visibility = Visibility.Collapsed;
                _statusIcon.Visibility = Visibility.Visible;
                _statusIcon.Glyph = "\uE711";
                _statusIcon.Foreground = FailBrush;
                ShowResult("失败：" + message, expanded: true);
            }

            public void MarkCanceled(string message)
            {
                _spinner.IsActive = false;
                _spinner.Visibility = Visibility.Collapsed;
                _statusIcon.Visibility = Visibility.Visible;
                _statusIcon.Glyph = "\uE711";
                _statusIcon.Foreground = ThemeService.Brush("TextSecondaryBrush");
                ShowResult(message, expanded: true);
            }

            /// <summary>
            /// 展示工具返回的原文。这里原来会把所有换行压成空格、再截到 300 字，
            /// 于是「匹配到 N 个候选，请用精确文件名重试」这类多行错误根本读不全；
            /// 现在保留换行，成功结果默认折 6 行可点开，失败与取消直接全展开。
            /// </summary>
            private void ShowResult(string result, bool expanded)
            {
                var text = (result ?? "").Trim();
                _expanded = expanded;
                _result.MaxLines = expanded ? 0 : 6;
                _result.Text = text;
                _result.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>内联确认卡片：在聊天流里允许或拒绝 AI 的工具调用请求（代替模态弹窗）。</summary>
        private sealed class ConfirmCard
        {
            public Border Root { get; }
            private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Button _ok;
            private readonly Button _cancel;
            private readonly TextBlock _status;

            public ConfirmCard(string describe)
            {
                var icon = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeService.Brush("AccentDarkBrush"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new FontIcon
                    {
                        FontFamily = ThemeService.IconFontFamily,
                        Glyph = "\uE9AC",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };

                var title = new TextBlock
                {
                    Text = "AI 请求执行操作",
                    FontSize = 12,
                    FontWeight = new FontWeight { Weight = 600 },
                    Foreground = ThemeService.Brush("TextPrimaryBrush")
                };
                var body = new TextBlock
                {
                    Text = describe,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                };
                var textCol = new StackPanel();
                textCol.Children.Add(title);
                textCol.Children.Add(body);

                var head = new Grid { ColumnSpacing = 10 };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(icon, 0);
                Grid.SetColumn(textCol, 1);
                head.Children.Add(icon);
                head.Children.Add(textCol);

                _ok = MakeButton("执行", ThemeService.Brush("AccentBrush"), ThemeService.Brush("AccentOnBrush"));
                _cancel = MakeButton("取消", ThemeService.Brush("NeutralButtonBrush"), ThemeService.Brush("NeutralButtonForegroundBrush"));
                _status = new TextBlock
                {
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                    Foreground = ThemeService.Brush("TextSecondaryBrush")
                };
                _ok.Click += (_, _) => Choose(true);
                _cancel.Click += (_, _) => Choose(false);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                actions.Children.Add(_ok);
                actions.Children.Add(_cancel);
                actions.Children.Add(_status);

                var stack = new StackPanel { Spacing = 10 };
                stack.Children.Add(head);
                stack.Children.Add(actions);

                Root = new Border
                {
                    Background = ThemeService.Brush("CardAcrylicBrush"),
                    BorderBrush = ThemeService.Brush("AccentBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = stack
                };
            }

            /// <summary>等待用户点击「执行 / 取消」；对话被停止时按取消处理。</summary>
            public Task<bool> WaitAsync(CancellationToken ct)
            {
                if (ct.CanBeCanceled)
                    ct.Register(() => UiDispatch.Run(() => Choose(null)));
                return _tcs.Task;
            }

            private void Choose(bool? allowed)
            {
                if (_tcs.Task.IsCompleted) return;
                _ok.Visibility = Visibility.Collapsed;
                _cancel.Visibility = Visibility.Collapsed;
                _status.Visibility = Visibility.Visible;
                _status.Text = allowed switch
                {
                    true => "已允许执行",
                    false => "用户已取消",
                    _ => "已停止"
                };
                _tcs.TrySetResult(allowed == true);
            }

            private static Button MakeButton(string text, Brush bg, Brush fg) => new()
            {
                Content = text,
                Height = 28,
                Padding = new Thickness(16, 0, 16, 0),
                FontSize = 11,
                Background = bg,
                Foreground = fg,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4)
            };
        }

        private void ScrollToEnd()
        {
            ChatScroll.UpdateLayout();
            ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true);
        }
    }
}
