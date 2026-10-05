using System;
using System.Collections.Generic;

namespace XO_Launcher.Cli
{
    public enum CliCommand
    {
        None,    // 无参数：正常开 GUI
        Help,
        Launch,  // GUI 意图
        Open,    // GUI 意图
        Install,
        Import,
        Download,
        List,
        Account,
        Set
    }

    /// <summary>解析后的命令行请求。</summary>
    public sealed class CliRequest
    {
        public CliCommand Command { get; init; }
        /// <summary>第一个位置参数（版本 ID / 页面名 / 链接 / set 的键）。</summary>
        public string? Arg { get; init; }
        /// <summary>第二个位置参数（set 的值）。</summary>
        public string? Arg2 { get; init; }
        public IReadOnlyDictionary<string, string> Options { get; init; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>无法识别的命令名（用于 help 提示）。</summary>
        public string? UnknownCommand { get; init; }

        /// <summary>无窗口执行、跑完即退的命令。</summary>
        public bool IsHeadless => Command is CliCommand.Install or CliCommand.Import
            or CliCommand.Download or CliCommand.List or CliCommand.Account
            or CliCommand.Set or CliCommand.Help;

        /// <summary>需要开主窗口再执行的命令。</summary>
        public bool IsGuiIntent => Command is CliCommand.Launch or CliCommand.Open;

        public string? GetOption(string key)
            => Options.TryGetValue(key, out var v) && !string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                ? v
                : (Options.ContainsKey(key) ? "" : null);

        public bool HasFlag(string key) => Options.ContainsKey(key);
    }

    /// <summary>
    /// 极简命令行解析：第一个非选项 token 为命令，其余非选项 token 依次为位置参数；
    /// <c>--key value</c> / <c>--key=value</c> / <c>--flag</c> 归入选项（大小写不敏感）。
    /// </summary>
    public static class CliParser
    {
        public static CliRequest Parse(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pos = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.IsNullOrEmpty(a)) continue;

                if (a.StartsWith("--", StringComparison.Ordinal))
                {
                    var kv = a.Substring(2);
                    int eq = kv.IndexOf('=');
                    if (eq >= 0)
                    {
                        opts[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                    }
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        opts[kv] = args[++i];
                    }
                    else
                    {
                        opts[kv] = "true";
                    }
                }
                else if (a.Length > 1 && a[0] == '-')
                {
                    opts[a.Substring(1)] = "true";
                }
                else
                {
                    pos.Add(a);
                }
            }

            if (pos.Count == 0)
            {
                if (opts.ContainsKey("help") || opts.ContainsKey("h"))
                    return new CliRequest { Command = CliCommand.Help, Options = opts };
                return new CliRequest { Command = CliCommand.None, Options = opts };
            }

            var cmd = MapCommand(pos[0].ToLowerInvariant(), out var unknown);
            return new CliRequest
            {
                Command = cmd,
                Arg = pos.Count > 1 ? pos[1] : null,
                Arg2 = pos.Count > 2 ? pos[2] : null,
                Options = opts,
                UnknownCommand = unknown
            };
        }

        private static CliCommand MapCommand(string s, out string? unknown)
        {
            unknown = null;
            switch (s)
            {
                case "help": case "-h": case "?": return CliCommand.Help;
                case "launch": case "start": case "play": return CliCommand.Launch;
                case "open": case "goto": case "nav": return CliCommand.Open;
                case "install": case "add": return CliCommand.Install;
                case "import": return CliCommand.Import;
                case "download": case "dl": return CliCommand.Download;
                case "list": case "ls": case "versions": return CliCommand.List;
                case "account": case "user": return CliCommand.Account;
                case "set": case "config": return CliCommand.Set;
                default: unknown = s; return CliCommand.Help;
            }
        }
    }
}
