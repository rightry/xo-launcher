using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>账号列表与当前选中账号，持久化到 %LocalAppData%\XO_Launcher\accounts.json。</summary>
    public sealed class AccountStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly List<GameAccount> _accounts = new();
        private string _selectedId = "";

        private AccountStore() => Load();

        public static AccountStore Instance { get; } = new();

        public event Action? Changed;

        public IReadOnlyList<GameAccount> Accounts => _accounts;

        public GameAccount Selected
            => _accounts.FirstOrDefault(a => string.Equals(a.Id, _selectedId, StringComparison.OrdinalIgnoreCase))
               ?? _accounts.First();

        public void Select(string id)
        {
            if (_accounts.All(a => !string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
                return;
            _selectedId = id;
            SyncSettings();
            Save();
            Changed?.Invoke();
        }

        public GameAccount AddOffline(string username)
        {
            username = username.Trim();
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("请输入离线玩家名称。");
            if (username.Length > 16)
                throw new InvalidOperationException("离线玩家名最多 16 个字符。");

            var uuid = OfflineUuid(username);
            var existing = _accounts.FirstOrDefault(a => !a.IsMicrosoft
                && string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Select(existing.Id);
                return existing;
            }

            var account = new GameAccount
            {
                Id = uuid,
                Type = GameAccount.TypeOffline,
                Username = username,
                Uuid = uuid.Replace("-", "", StringComparison.Ordinal),
                AccessToken = "0",
                Xuid = "0"
            };
            _accounts.Add(account);
            _selectedId = account.Id;
            SyncSettings();
            Save();
            Changed?.Invoke();
            return account;
        }

        public void AddOrReplaceMicrosoft(GameAccount account)
        {
            var index = _accounts.FindIndex(a => a.IsMicrosoft
                && string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _accounts[index] = account;
            else _accounts.Add(account);
            _selectedId = account.Id;
            SyncSettings();
            Save();
            Changed?.Invoke();
        }

        /// <summary>
        /// 把当前内存里的账号列表（含对现有账号字段的修改）写回磁盘并广播 Changed。
        /// Skin 页面改 SkinSource / SkinVariant / SkinUrl / LocalSkinPath 等都用这个。
        /// </summary>
        public void Persist()
        {
            SyncSettings();
            Save();
            Changed?.Invoke();
        }

        public void Remove(string id)
        {
            var removed = _accounts.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;
            if (_accounts.Count == 0)
                AddOffline(string.IsNullOrWhiteSpace(LauncherSettings.Instance.Username)
                    ? "Player"
                    : LauncherSettings.Instance.Username);
            if (_accounts.All(a => !string.Equals(a.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
                _selectedId = _accounts[0].Id;
            SyncSettings();
            Save();
            Changed?.Invoke();
        }

        public async Task<LaunchSession> EnsureLaunchSessionAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var account = Selected;
            if (account.IsMicrosoft && MicrosoftAuthService.NeedsRefresh(account))
            {
                var refreshed = await MicrosoftAuthService.RefreshAsync(account, progress, cancellationToken);
                AddOrReplaceMicrosoft(refreshed);
                account = refreshed;
            }
            return account.ToLaunchSession(LauncherSettings.Instance.LauncherClientId);
        }

        private void SyncSettings()
        {
            var settings = LauncherSettings.Instance;
            settings.SelectedAccountId = _selectedId;
            settings.Username = Selected.Username;
        }

        private void Load()
        {
            try
            {
                var path = StorePath;
                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOptions);
                    if (data?.Accounts is { Count: > 0 })
                    {
                        _accounts.AddRange(data.Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Username)));
                        _selectedId = data.SelectedId ?? "";
                    }
                }
            }
            catch (Exception)
            {
                _accounts.Clear();
            }

            if (_accounts.Count == 0)
            {
                var name = string.IsNullOrWhiteSpace(LauncherSettings.Instance.Username)
                    ? "Player"
                    : LauncherSettings.Instance.Username;
                var uuid = OfflineUuid(name);
                _accounts.Add(new GameAccount
                {
                    Id = uuid,
                    Type = GameAccount.TypeOffline,
                    Username = name,
                    Uuid = uuid.Replace("-", "", StringComparison.Ordinal),
                    AccessToken = "0",
                    Xuid = "0"
                });
                _selectedId = uuid;
            }

            if (_accounts.All(a => !string.Equals(a.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                var match = _accounts.FirstOrDefault(a =>
                    string.Equals(a.Username, LauncherSettings.Instance.Username, StringComparison.OrdinalIgnoreCase));
                _selectedId = (match ?? _accounts[0]).Id;
            }

            if (!string.IsNullOrWhiteSpace(LauncherSettings.Instance.SelectedAccountId)
                && _accounts.Any(a => string.Equals(a.Id, LauncherSettings.Instance.SelectedAccountId, StringComparison.OrdinalIgnoreCase)))
                _selectedId = LauncherSettings.Instance.SelectedAccountId;

            Save();
        }

        private void Save()
        {
            try
            {
                var path = StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var data = new StoreData
                {
                    SelectedId = _selectedId,
                    Accounts = _accounts
                };
                File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
            }
            catch (Exception)
            {
                // 写入失败不影响运行
            }
        }

        /// <summary>与原版离线模式一致的 UUID v3（OfflinePlayer:名称）。</summary>
        public static string OfflineUuid(string username)
        {
            var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
            return Convert.ToHexString(hash[..4]).ToLowerInvariant()
                   + "-" + Convert.ToHexString(hash[4..6]).ToLowerInvariant()
                   + "-" + Convert.ToHexString(hash[6..8]).ToLowerInvariant()
                   + "-" + Convert.ToHexString(hash[8..10]).ToLowerInvariant()
                   + "-" + Convert.ToHexString(hash[10..16]).ToLowerInvariant();
        }

        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XO_Launcher", "accounts.json");

        private sealed class StoreData
        {
            public string? SelectedId { get; set; }
            public List<GameAccount>? Accounts { get; set; }
        }
    }
}
