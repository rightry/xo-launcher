using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 启动器全局设置。以 JSON 持久化到 %LocalAppData%\XO_Launcher\settings.json，
    /// 首页版本显示、底栏状态、启动游戏和设置页共享同一份数据。
    /// </summary>
    public sealed class LauncherSettings
    {
        private const string FileName = "settings.json";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private string _gameDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        private string _javaPath = "";
        private long _memoryMB = 4096;
        private string _selectedVersion = "1.21.5";
        private string _username = "Player";
        private string _selectedAccountId = "";
        private string _launcherClientId = Guid.NewGuid().ToString();
        private bool _autoMatchJava = true;
        private string _appTheme = "dark";
        private string _accentColor = ThemeService.DefaultAccent;
        private string _uiFont = ThemeService.DefaultFontFamily;
        private bool _translateModNames = true;
        private bool _closeLauncherAfterLaunch;
        private string _downloadDirectory = ModLibraryService.DefaultDirectory;
        private readonly List<GameFolderEntry> _gameFolders = new();
        private string _activeFolderId = "";

        private LauncherSettings() => Load();

        public static LauncherSettings Instance { get; } = new();

        /// <summary>任一设置项变更后触发（用于首页/底栏/设置页同步刷新）。</summary>
        public event Action? Changed;

        public IReadOnlyList<GameFolderEntry> GameFolders => _gameFolders;

        public string ActiveFolderId => _activeFolderId;

        public string GameDirectory
        {
            get => _gameDirectory;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                string full;
                try { full = Path.GetFullPath(value.Trim()); }
                catch (Exception) { full = value.Trim(); }
                var pathChanged = !string.Equals(_gameDirectory, full, StringComparison.OrdinalIgnoreCase);
                if (pathChanged)
                    _gameDirectory = full;
                var folderChanged = UpsertFolder(full);
                if (pathChanged || folderChanged)
                {
                    Save();
                    Changed?.Invoke();
                }
            }
        }

        public string JavaPath
        {
            get => _javaPath;
            set => SetValue(ref _javaPath, value);
        }

        public long MemoryMB
        {
            get => _memoryMB;
            set => SetValue(ref _memoryMB, value);
        }

        public string SelectedVersion
        {
            get => _selectedVersion;
            set => SetValue(ref _selectedVersion, value);
        }

        public string Username
        {
            get => _username;
            set => SetValue(ref _username, value);
        }

        /// <summary>当前选中的账号 Id（对应 accounts.json）。</summary>
        public string SelectedAccountId
        {
            get => _selectedAccountId;
            set => SetValue(ref _selectedAccountId, value ?? "");
        }

        /// <summary>启动参数 ${clientid}，安装后固定。</summary>
        public string LauncherClientId
        {
            get => _launcherClientId;
            set => SetValue(ref _launcherClientId, string.IsNullOrWhiteSpace(value) ? _launcherClientId : value);
        }

        /// <summary>启动时按游戏所需版本自动选择已检测到的 Java。</summary>
        public bool AutoMatchJava
        {
            get => _autoMatchJava;
            set => SetValue(ref _autoMatchJava, value);
        }

        /// <summary>界面主题：dark、light 或 system（跟随系统亮暗）。</summary>
        public string AppTheme
        {
            get => _appTheme;
            set => SetValue(ref _appTheme, NormalizeTheme(value));
        }

        /// <summary>主题强调色，#RRGGBB。用于按钮、开关、选中态与链接。</summary>
        public string AccentColor
        {
            get => _accentColor;
            set => SetValue(ref _accentColor, NormalizeAccent(value));
        }

        /// <summary>界面字体家族名，例如 Microsoft YaHei UI。</summary>
        public string UiFont
        {
            get => _uiFont;
            set => SetValue(ref _uiFont, NormalizeFont(value));
        }

        private static string NormalizeFont(string? value)
        {
            var name = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return ThemeService.DefaultFontFamily;
            return name;
        }

        private static string NormalizeAccent(string? value)
        {
            var parsed = ThemeService.ParseAccent(value);
            return ThemeService.FormatAccent(parsed);
        }

        private static string NormalizeTheme(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "system" => "system",
            _ => "dark"
        };

        /// <summary>以中文显示模组、光影与资源包名称。</summary>
        public bool TranslateModNames
        {
            get => _translateModNames;
            set => SetValue(ref _translateModNames, value);
        }

        /// <summary>游戏窗口显示后自动退出启动器（退出后将无法进行故障分析）。</summary>
        public bool CloseLauncherAfterLaunch
        {
            get => _closeLauncherAfterLaunch;
            set => SetValue(ref _closeLauncherAfterLaunch, value);
        }

        private bool _autoSwitchToMessages;
        private bool _autoCheckUpdates = true;

        /// <summary>新增下载任务时自动跳转到消息页面。</summary>
        public bool AutoSwitchToMessages
        {
            get => _autoSwitchToMessages;
            set => SetValue(ref _autoSwitchToMessages, value);
        }

        /// <summary>启动时自动检查启动器更新。</summary>
        public bool AutoCheckUpdates
        {
            get => _autoCheckUpdates;
            set => SetValue(ref _autoCheckUpdates, value);
        }

        private bool _showBottomBar;

        /// <summary>显示窗口底部的信息栏（JAVA 版本 / 游戏目录 / 内存分配）。
        /// 关闭后底部只在启动游戏时出现进度条。</summary>
        public bool ShowBottomBar
        {
            get => _showBottomBar;
            set => SetValue(ref _showBottomBar, value);
        }

        private bool _panoramaBackground;

        /// <summary>用当前版本的全景图作为所有页面的背景（自动滚动，卡片改为毛玻璃质感）。</summary>
        public bool PanoramaBackground
        {
            get => _panoramaBackground;
            set => SetValue(ref _panoramaBackground, value);
        }

        private double _panoramaBlurAmount;

        /// <summary>全景图毛玻璃的模糊程度（高斯模糊半径，0 表示不模糊）。范围 0–40。</summary>
        public double PanoramaBlurAmount
        {
            get => _panoramaBlurAmount;
            set => SetValue(ref _panoramaBlurAmount, Math.Clamp(value, 0, 40));
        }

        private bool _welcomeDone;
        private string _seenReleaseNotes = "";

        /// <summary>是否走完过新手引导。没走完时启动先显示引导遮罩，主界面藏在它后面。</summary>
        public bool WelcomeDone
        {
            get => _welcomeDone;
            set => SetValue(ref _welcomeDone, value);
        }

        /// <summary>上次看过的更新说明版本号；比它新的条目会在启动时展示。</summary>
        public string SeenReleaseNotes
        {
            get => _seenReleaseNotes;
            set => SetValue(ref _seenReleaseNotes, value ?? "");
        }

        private double _panoramaScrollSpeed = 5;

        /// <summary>全景滚动速度档位（1 最慢 – 10 最快）。设置页调的是速度而不是秒数。</summary>
        public double PanoramaScrollSpeed
        {
            get => _panoramaScrollSpeed;
            set => SetValue(ref _panoramaScrollSpeed, Math.Clamp(value, 1, 10));
        }

        /// <summary>速度档位换算成一圈（全景环带转完 360°）所需秒数：1 档 600 秒，10 档 60 秒。</summary>
        public static double SpeedToLoopSeconds(double speed) => 600 / Math.Clamp(speed, 1, 10);

        /// <summary>旧配置里的「一圈秒数」换算回速度档位，升级后背景速度不跳变。</summary>
        public static double LoopSecondsToSpeed(double seconds) => Math.Clamp(600 / Math.Clamp(seconds, 10, 600), 1, 10);

        private bool _notificationsEnabled = true;
        private bool _notifyDownloadComplete = true;
        private bool _notifyGameCrash = true;
        private bool _notifyAiConfirm = true;

        /// <summary>应用内通知总开关（右下角通知卡片）。</summary>
        public bool NotificationsEnabled
        {
            get => _notificationsEnabled;
            set => SetValue(ref _notificationsEnabled, value);
        }

        /// <summary>下载完成 / 失败时弹出通知。</summary>
        public bool NotifyDownloadComplete
        {
            get => _notifyDownloadComplete;
            set => SetValue(ref _notifyDownloadComplete, value);
        }

        /// <summary>检测到游戏崩溃时弹出通知。</summary>
        public bool NotifyGameCrash
        {
            get => _notifyGameCrash;
            set => SetValue(ref _notifyGameCrash, value);
        }

        /// <summary>AI 工具调用等待用户确认时弹出系统通知。</summary>
        public bool NotifyAiConfirm
        {
            get => _notifyAiConfirm;
            set => SetValue(ref _notifyAiConfirm, value);
        }

        private bool _aiAgreed;

        /// <summary>是否已读过 AI 使用条款。首次发起对话前必须确认一次，之后不再打扰。</summary>
        public bool AiAgreed
        {
            get => _aiAgreed;
            set => SetValue(ref _aiAgreed, value);
        }

        /// <summary>模组、光影与资源包的下载库根目录。</summary>
        public string DownloadDirectory
        {
            get => _downloadDirectory;
            set => SetValue(ref _downloadDirectory, string.IsNullOrWhiteSpace(value) ? ModLibraryService.DefaultDirectory : value.Trim());
        }

        private string _downloadMirror = "auto";
        private string _aiProvider = AiModels.OpenRouterProvider;
        private string _aiEndpoint = AiModels.OpenRouterEndpoint;
        private string _aiApiKey = "";
        private string _aiModel = AiModels.NemotronFree;
        private string _aiPermission = AiPermissionConfirm;

        /// <summary>AI 工具权限：confirm 每次调用都等用户确认、auto 全部自动执行（含删除，不再弹窗）。</summary>
        public const string AiPermissionConfirm = "confirm";
        public const string AiPermissionAuto = "auto";

        /// <summary>AI 工具权限档。只认 confirm / auto 两个值，其它一律回落到 confirm（默认要确认）。</summary>
        public string AiPermission
        {
            get => _aiPermission;
            set => SetValue(ref _aiPermission, NormalizeAiPermission(value));
        }

        private static string NormalizeAiPermission(string? value)
            => string.Equals(value, AiPermissionAuto, StringComparison.OrdinalIgnoreCase)
                ? AiPermissionAuto
                : AiPermissionConfirm;

        /// <summary>下载镜像策略：auto 智能多源、bmclapi 镜像优先、bmclapi_only 仅镜像源、official 仅官方源。</summary>
        public string DownloadMirror
        {
            get => _downloadMirror;
            set => SetValue(ref _downloadMirror, NormalizeMirror(value));
        }

        private static string NormalizeMirror(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "bmclapi" => "bmclapi",
            "bmclapi_only" => "bmclapi_only",
            "official" => "official",
            _ => "auto"
        };

        /// <summary>AI 服务商。由选中的内置模型决定（见 AiModels），不接受外部写入。</summary>
        public string AiProvider
        {
            get => AiModels.Selected.Provider;
            set => SetValue(ref _aiProvider, AiModels.Selected.Provider);
        }

        /// <summary>接口地址（BaseURL）。同样跟着选中的内置模型走，避免设置文件被改成任意站点。</summary>
        public string AiEndpoint
        {
            get => AiModels.Selected.Endpoint;
            set => SetValue(ref _aiEndpoint, AiModels.Selected.Endpoint);
        }

        /// <summary>
        /// 用户自己申请的 AI API Key。公开版不内置任何密钥，这一项是 AI 功能唯一的密钥来源，
        /// 只写进本机设置文件（%LocalAppData%\XO_Launcher），不会出现在仓库里。
        /// </summary>
        public string AiApiKey
        {
            get => _aiApiKey;
            set => SetValue(ref _aiApiKey, (value ?? "").Trim());
        }

        /// <summary>
        /// 选用的模型 id。只接受 <see cref="AiModels"/> 里的内置项 —— 传进来的值认不出来就忽略，
        /// 这样设置文件里的任意字符串不会被拼成请求地址或模型名。
        /// </summary>
        public string AiModel
        {
            get => _aiModel;
            set
            {
                var matched = AiModels.Find(value);
                if (matched is not null) SetValue(ref _aiModel, matched.Id);
            }
        }

        /// <summary>把路径加入游戏目录列表并设为当前项。已存在则只切换。</summary>
        public GameFolderEntry EnsureGameFolder(string path, string? name = null)
        {
            GameDirectory = path;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var folder = _gameFolders.FirstOrDefault(f => string.Equals(f.Id, _activeFolderId, StringComparison.Ordinal));
                if (folder is not null && !string.Equals(folder.Name, name, StringComparison.Ordinal))
                {
                    folder.Name = name.Trim();
                    Save();
                    Changed?.Invoke();
                }
            }
            return _gameFolders.First(f => string.Equals(f.Id, _activeFolderId, StringComparison.Ordinal));
        }

        public void SwitchGameFolder(string id)
        {
            var folder = _gameFolders.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
            if (folder is null) return;
            GameDirectory = folder.Path;
        }

        public bool RemoveGameFolder(string id)
        {
            if (_gameFolders.Count <= 1) return false;
            var folder = _gameFolders.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
            if (folder is null) return false;
            _gameFolders.Remove(folder);
            if (string.Equals(_activeFolderId, id, StringComparison.Ordinal)
                || string.Equals(_gameDirectory, folder.Path, StringComparison.OrdinalIgnoreCase))
            {
                var next = _gameFolders[0];
                _activeFolderId = next.Id;
                _gameDirectory = next.Path;
            }
            Save();
            Changed?.Invoke();
            return true;
        }

        private bool UpsertFolder(string path)
        {
            var existing = _gameFolders.FirstOrDefault(f =>
                string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (string.Equals(_activeFolderId, existing.Id, StringComparison.Ordinal))
                    return false;
                _activeFolderId = existing.Id;
                return true;
            }

            var entry = new GameFolderEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GameFolderEntry.SuggestName(path),
                Path = path
            };
            _gameFolders.Add(entry);
            _activeFolderId = entry.Id;
            return true;
        }

        private void SetValue<T>(ref T field, T value)
        {
            if (Equals(field, value)) return;
            field = value;
            Save();
            Changed?.Invoke();
        }

        private void Load()
        {
            var hadClientId = false;
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path));
                    if (data is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(data.GameDirectory)) _gameDirectory = data.GameDirectory;
                        _javaPath = data.JavaPath ?? "";
                        if (data.MemoryMB is > 0) _memoryMB = data.MemoryMB.Value;
                        if (!string.IsNullOrWhiteSpace(data.SelectedVersion)) _selectedVersion = data.SelectedVersion;
                        if (!string.IsNullOrWhiteSpace(data.Username)) _username = data.Username;
                        if (!string.IsNullOrWhiteSpace(data.SelectedAccountId)) _selectedAccountId = data.SelectedAccountId;
                        if (!string.IsNullOrWhiteSpace(data.LauncherClientId))
                        {
                            _launcherClientId = data.LauncherClientId;
                            hadClientId = true;
                        }
                        if (data.AutoMatchJava is not null) _autoMatchJava = data.AutoMatchJava.Value;
                        _appTheme = NormalizeTheme(data.AppTheme);
                        if (!string.IsNullOrWhiteSpace(data.AccentColor))
                            _accentColor = NormalizeAccent(data.AccentColor);
                        if (!string.IsNullOrWhiteSpace(data.UiFont))
                            _uiFont = NormalizeFont(data.UiFont);
                        if (!string.IsNullOrWhiteSpace(data.DownloadDirectory))
                            _downloadDirectory = data.DownloadDirectory.Trim();
                        if (data.TranslateModNames is not null)
                            _translateModNames = data.TranslateModNames.Value;
                        if (data.CloseLauncherAfterLaunch is not null)
                            _closeLauncherAfterLaunch = data.CloseLauncherAfterLaunch.Value;
                        if (data.AutoSwitchToMessages is not null)
                            _autoSwitchToMessages = data.AutoSwitchToMessages.Value;
                        if (data.AutoCheckUpdates is not null)
                            _autoCheckUpdates = data.AutoCheckUpdates.Value;
                        if (data.ShowBottomBar is not null)
                            _showBottomBar = data.ShowBottomBar.Value;
                        if (data.WelcomeDone is not null)
                            _welcomeDone = data.WelcomeDone.Value;
                        if (!string.IsNullOrWhiteSpace(data.SeenReleaseNotes))
                            _seenReleaseNotes = data.SeenReleaseNotes!;
                        if (data.PanoramaBackground is not null)
                            _panoramaBackground = data.PanoramaBackground.Value;
                        if (data.PanoramaBlurAmount is not null)
                            _panoramaBlurAmount = Math.Clamp(data.PanoramaBlurAmount.Value, 0, 40);
                        // 新键优先；只有旧配置时把「一圈秒数」换算成速度档位，升级后背景速度不跳变
                        if (data.PanoramaScrollSpeed is not null)
                            _panoramaScrollSpeed = Math.Clamp(data.PanoramaScrollSpeed.Value, 1, 10);
                        else if (data.PanoramaScrollSeconds is not null)
                            _panoramaScrollSpeed = LoopSecondsToSpeed(data.PanoramaScrollSeconds.Value);
                        if (data.NotificationsEnabled is not null)
                            _notificationsEnabled = data.NotificationsEnabled.Value;
                        if (data.NotifyDownloadComplete is not null)
                            _notifyDownloadComplete = data.NotifyDownloadComplete.Value;
                        if (data.NotifyGameCrash is not null)
                            _notifyGameCrash = data.NotifyGameCrash.Value;
                        if (data.NotifyAiConfirm is not null)
                            _notifyAiConfirm = data.NotifyAiConfirm.Value;
                        _aiAgreed = data.AiAgreed == true;
                        if (!string.IsNullOrWhiteSpace(data.DownloadMirror))
                            _downloadMirror = NormalizeMirror(data.DownloadMirror);
                        _aiProvider = AiModels.OpenRouterProvider;
                        _aiEndpoint = AiModels.OpenRouterEndpoint;
                        _aiApiKey = data.AiApiKey ?? "";
                        // 只认内置模型清单里的项，认不出来就用默认档；设置文件里的任意字符串不会被拼成请求地址
                        _aiModel = AiModels.Find(data.AiModel)?.Id ?? AiModels.All[0].Id;
                        _aiPermission = NormalizeAiPermission(data.AiPermission);
                        if (data.GameFolders is { Count: > 0 })
                        {
                            _gameFolders.Clear();
                            foreach (var folder in data.GameFolders)
                            {
                                if (folder is null || string.IsNullOrWhiteSpace(folder.Path))
                                    continue;
                                _gameFolders.Add(new GameFolderEntry
                                {
                                    Id = string.IsNullOrWhiteSpace(folder.Id) ? Guid.NewGuid().ToString("N") : folder.Id,
                                    Name = string.IsNullOrWhiteSpace(folder.Name)
                                        ? GameFolderEntry.SuggestName(folder.Path)
                                        : folder.Name,
                                    Path = folder.Path
                                });
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(data.ActiveFolderId))
                            _activeFolderId = data.ActiveFolderId;
                    }
                }
            }
            catch (Exception)
            {
                // 设置文件损坏时静默回退到默认值
            }

            var seededFolder = UpsertFolder(_gameDirectory);
            if (!hadClientId || seededFolder)
                Save();
        }

        private void Save()
        {
            try
            {
                var path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var data = new SettingsData
                {
                    GameDirectory = _gameDirectory,
                    JavaPath = _javaPath,
                    MemoryMB = _memoryMB,
                    SelectedVersion = _selectedVersion,
                    Username = _username,
                    SelectedAccountId = _selectedAccountId,
                    LauncherClientId = _launcherClientId,
                    AutoMatchJava = _autoMatchJava,
                    AppTheme = _appTheme,
                    AccentColor = _accentColor,
                    UiFont = _uiFont,
                    DownloadDirectory = _downloadDirectory,
                    TranslateModNames = _translateModNames,
                    CloseLauncherAfterLaunch = _closeLauncherAfterLaunch,
                    AutoSwitchToMessages = _autoSwitchToMessages,
                    AutoCheckUpdates = _autoCheckUpdates,
                    ShowBottomBar = _showBottomBar,
                    WelcomeDone = _welcomeDone,
                    SeenReleaseNotes = _seenReleaseNotes,
                    PanoramaBackground = _panoramaBackground,
                    PanoramaBlurAmount = _panoramaBlurAmount,
                    PanoramaScrollSpeed = _panoramaScrollSpeed,
                    NotificationsEnabled = _notificationsEnabled,
                    NotifyDownloadComplete = _notifyDownloadComplete,
                    NotifyGameCrash = _notifyGameCrash,
                    NotifyAiConfirm = _notifyAiConfirm,
                    AiAgreed = _aiAgreed,
                    DownloadMirror = _downloadMirror,
                    AiProvider = AiModels.Selected.Provider,
                    AiEndpoint = AiModels.Selected.Endpoint,
                    AiApiKey = _aiApiKey,
                    AiModel = _aiModel,
                    AiPermission = _aiPermission,
                    GameFolders = _gameFolders.Select(f => new GameFolderEntry
                    {
                        Id = f.Id,
                        Name = f.Name,
                        Path = f.Path
                    }).ToList(),
                    ActiveFolderId = _activeFolderId
                };
                File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            }
            catch (Exception)
            {
                // 写入失败不影响运行
            }
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", FileName);

        private sealed class SettingsData
        {
            public string? GameDirectory { get; set; }
            public string? JavaPath { get; set; }
            public long? MemoryMB { get; set; }
            public string? SelectedVersion { get; set; }
            public string? Username { get; set; }
            public string? SelectedAccountId { get; set; }
            public string? LauncherClientId { get; set; }
            public bool? AutoMatchJava { get; set; }
            public string? AppTheme { get; set; }
            public string? AccentColor { get; set; }
            public string? UiFont { get; set; }
            public string? DownloadDirectory { get; set; }
            public bool? TranslateModNames { get; set; }
            public bool? CloseLauncherAfterLaunch { get; set; }
            public bool? AutoSwitchToMessages { get; set; }
            public bool? AutoCheckUpdates { get; set; }
            public bool? ShowBottomBar { get; set; }
            public bool? WelcomeDone { get; set; }
            public string? SeenReleaseNotes { get; set; }
            public bool? PanoramaBackground { get; set; }
            public double? PanoramaBlurAmount { get; set; }
            public double? PanoramaScrollSpeed { get; set; }
            /// <summary>旧键（一圈秒数），只用于读入换算成速度档位，不再写出。</summary>
            public double? PanoramaScrollSeconds { get; set; }
            public bool? NotificationsEnabled { get; set; }
            public bool? NotifyDownloadComplete { get; set; }
            public bool? NotifyGameCrash { get; set; }
            public bool? NotifyAiConfirm { get; set; }
            public bool? AiAgreed { get; set; }
            public string? DownloadMirror { get; set; }
            public string? AiProvider { get; set; }
            public string? AiEndpoint { get; set; }
            public string? AiApiKey { get; set; }
            public string? AiModel { get; set; }
            public string? AiPermission { get; set; }
            public List<GameFolderEntry>? GameFolders { get; set; }
            public string? ActiveFolderId { get; set; }
        }
    }
}
