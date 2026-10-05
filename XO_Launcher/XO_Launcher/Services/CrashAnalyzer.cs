using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 根据崩溃报告、latest.log、hs_err 与启动日志匹配常见故障并给出处理建议。
    /// </summary>
    public static class CrashAnalyzer
    {
        private const int MaxFileBytes = 512 * 1024;
        private static readonly Regex ClassFileVersion = new(
            @"(?:class file version|major version)\s+(\d+)(?:\.0)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static CrashAnalysis Analyze(GameCrashInfo crash)
        {
            var blob = ReadBlob(crash);
            var findings = MatchRules(blob, crash).ToList();
            if (findings.Count == 0)
            {
                findings.Add(new CrashFinding
                {
                    Id = "unknown",
                    Title = "未能自动识别具体原因",
                    Advice = string.IsNullOrWhiteSpace(blob)
                        ? "当前版本没有可用的崩溃报告或游戏日志。启动一次游戏后再分析，或选择一份 crash-report / latest.log / hs_err 文件。"
                        : "日志里没有匹配到已知故障特征。请把崩溃日志导出后对照模组列表、Java 版本与内存设置排查，或带到社区求助。",
                    Severity = "提示"
                });
            }

            return new CrashAnalysis
            {
                Headline = findings[0].Title,
                Excerpt = ExtractExcerpt(blob),
                Findings = findings
            };
        }

        public static GameCrashInfo FromLogFile(string path)
        {
            var name = Path.GetFileName(path);
            string? crash = null, hs = null, latest = null, launch = null;
            try
            {
                var head = ReadHead(path, 4096);
                if (head.Contains("---- Minecraft Crash Report ----", StringComparison.Ordinal)
                    || name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase))
                    crash = path;
                else if (head.Contains("A fatal error has been detected by the Java Runtime", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("hs_err", StringComparison.OrdinalIgnoreCase))
                    hs = path;
                else if (name.Contains("xo-launch", StringComparison.OrdinalIgnoreCase))
                    launch = path;
                else
                    latest = path;
            }
            catch (Exception)
            {
                latest = path;
            }

            var kind = crash is not null ? "Minecraft 崩溃"
                : hs is not null ? "JVM 崩溃"
                : "日志分析";
            return new GameCrashInfo
            {
                VersionId = Path.GetFileNameWithoutExtension(path),
                InstanceDirectory = Path.GetDirectoryName(path) ?? "",
                Kind = kind,
                CrashReportPath = crash,
                HsErrPath = hs,
                LatestLogPath = latest,
                LaunchLogPath = launch,
                Summary = kind
            };
        }

        public static string FormatForExport(CrashAnalysis analysis)
        {
            var sb = new StringBuilder();
            sb.AppendLine("----- 故障分析 -----");
            sb.AppendLine("结论: " + analysis.Headline);
            foreach (var finding in analysis.Findings)
            {
                sb.AppendLine($"[{finding.Severity}] {finding.Title}");
                sb.AppendLine(finding.Advice);
                if (!string.IsNullOrWhiteSpace(finding.Evidence))
                    sb.AppendLine("依据: " + finding.Evidence);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(analysis.Excerpt))
            {
                sb.AppendLine("----- 日志摘录 -----");
                sb.AppendLine(analysis.Excerpt);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>给 AI 助手用的崩溃材料：规则分析 + 报告/日志摘录（不含启动器日志与令牌）。</summary>
        public static string BuildAssistantPacket(GameCrashInfo crash, int maxChars = 14000)
        {
            var analysis = Analyze(crash);
            var sb = new StringBuilder();
            sb.AppendLine($"崩溃类型: {crash.Kind}");
            if (!string.IsNullOrWhiteSpace(crash.VersionId))
                sb.AppendLine($"版本: {crash.VersionId}");
            if (crash.ExitCode != 0)
                sb.AppendLine($"退出码: {crash.ExitCode}");
            if (!string.IsNullOrWhiteSpace(crash.Summary))
                sb.AppendLine($"摘要: {crash.Summary}");
            sb.AppendLine();
            sb.Append(FormatForExport(analysis));
            AppendSlice(sb, crash.CrashReportPath, 160 * 1024, fromEnd: false);
            AppendSlice(sb, crash.HsErrPath, 80 * 1024, fromEnd: false);
            AppendSlice(sb, crash.LatestLogPath, 80 * 1024, fromEnd: true);

            var text = sb.ToString().Trim();
            if (text.Length <= maxChars) return text;
            return text[..2000] + "\n…（中间已省略）…\n" + text[^(maxChars - 2200)..];
        }

        private static void AppendSlice(StringBuilder sb, string? path, int maxBytes, bool fromEnd)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            sb.AppendLine().AppendLine("##### " + Path.GetFileName(path));
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var take = (int)Math.Min(stream.Length, maxBytes);
                if (fromEnd && stream.Length > take) stream.Seek(-take, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var slice = reader.ReadToEnd();
                if (!fromEnd && stream.Length > take)
                    slice += "\n…（文件后部已省略）";
                sb.AppendLine(slice);
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取失败：" + ex.Message);
            }
        }

        private static IEnumerable<CrashFinding> MatchRules(string blob, GameCrashInfo crash)
        {
            if (string.IsNullOrWhiteSpace(blob) && crash.ExitCode == 0)
                yield break;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in Rules)
            {
                if (!rule.Matches(blob)) continue;
                if (!seen.Add(rule.Id)) continue;
                var finding = rule.Build(blob);
                yield return finding;
            }

            var javaHint = JavaVersionHint(blob);
            if (javaHint is not null && seen.Add(javaHint.Id))
                yield return javaHint;

            if (crash.ExitCode != 0 && seen.Count == 0)
            {
                yield return new CrashFinding
                {
                    Id = "exit-code",
                    Title = crash.ExitCode == 1 && crash.Kind == "启动失败"
                        ? "游戏进程很快退出"
                        : $"进程异常退出（退出码 {crash.ExitCode}）",
                    Advice = crash.Kind == "启动失败"
                        ? "常见原因是内存分配过大、Java 版本不匹配，或 JVM 参数无效。请到「内存管理 / JVM 参数」检查，并查看下方日志摘录。"
                        : "游戏没有正常结束。请结合下方日志摘录，检查模组、Java 与内存设置。",
                    Severity = "警告"
                };
            }
        }

        private static CrashFinding? JavaVersionHint(string blob)
        {
            var match = ClassFileVersion.Match(blob);
            if (!match.Success) return null;
            if (!int.TryParse(match.Groups[1].Value, out var major)) return null;
            var need = major switch
            {
                52 => 8,
                55 => 11,
                61 => 17,
                65 => 21,
                66 => 22,
                67 => 23,
                68 => 24,
                69 => 25,
                _ => major >= 49 ? major - 44 : 0
            };
            if (need <= 0) return null;
            return new CrashFinding
            {
                Id = "java-classfile",
                Title = $"当前 Java 过旧，这个版本需要 Java {need}",
                Advice = $"日志显示 class file version {major}。请在版本设置中开启「自动匹配 Java」，或手动选择 Java {need}（javaw.exe）。旧版游戏不要误用太新的参数。",
                Severity = "严重",
                Evidence = match.Value
            };
        }

        private static string ReadBlob(GameCrashInfo crash)
        {
            var sb = new StringBuilder();
            sb.AppendLine(crash.Kind);
            if (!string.IsNullOrWhiteSpace(crash.Summary))
                sb.AppendLine(crash.Summary);
            Append(sb, crash.CrashReportPath);
            Append(sb, crash.HsErrPath);
            Append(sb, crash.LatestLogPath);
            Append(sb, crash.LaunchLogPath);
            var debug = Path.Combine(crash.InstanceDirectory ?? "", "logs", "debug.log");
            Append(sb, debug);
            Append(sb, LauncherLogService.LogFile, 96 * 1024);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string? path, int maxBytes = MaxFileBytes)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            sb.AppendLine().AppendLine("##### " + path);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var take = (int)Math.Min(stream.Length, maxBytes);
                if (stream.Length > take) stream.Seek(-take, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                sb.AppendLine(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取失败：" + ex.Message);
            }
        }

        private static string ReadHead(string path, int bytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(stream.Length, bytes);
            var buffer = new byte[take];
            _ = stream.Read(buffer, 0, take);
            return Encoding.UTF8.GetString(buffer);
        }

        private static string ExtractExcerpt(string blob)
        {
            if (string.IsNullOrWhiteSpace(blob)) return "没有可读的日志内容。";
            var lines = blob.Replace("\r\n", "\n").Split('\n');
            var start = IndexOfMarker(lines,
                "---- Minecraft Crash Report ----",
                "A fatal error has been detected by the Java Runtime",
                "Encountered an unexpected exception",
                "Could not create the Java Virtual Machine",
                "Error occurred during initialization of VM",
                "Exception in thread");
            if (start < 0)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("Exception", StringComparison.OrdinalIgnoreCase)
                        || lines[i].Contains("Caused by:", StringComparison.OrdinalIgnoreCase))
                    {
                        start = i;
                        break;
                    }
                }
            }
            if (start < 0) start = Math.Max(0, lines.Length - 40);
            var take = lines.Skip(start).Take(48)
                .Select(l => l.TrimEnd())
                .Where(l => l.Length > 0 && !l.StartsWith("##### ", StringComparison.Ordinal));
            var text = string.Join(Environment.NewLine, take).Trim();
            return string.IsNullOrWhiteSpace(text) ? "日志中没有找到异常片段。" : text;
        }

        private static int IndexOfMarker(string[] lines, params string[] markers)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var marker in markers)
                {
                    if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return -1;
        }

        private static readonly Rule[] Rules =
        {
            new("oom", "严重", "内存不足",
                "游戏堆内存不够。请到版本设置「内存管理」降低或适当提高分配，并关闭占用内存的其他程序。不要一次性把内存拉满，系统需要留空。",
                any: new[] { "java.lang.OutOfMemoryError", "Java heap space", "GC overhead limit exceeded", "Metaspace" }),
            new("jvm-create", "严重", "无法创建 Java 虚拟机",
                "JVM 启动失败。最常见原因是本版本内存分配过大，或使用了 32 位 Java。请降低内存（建议不超过物理内存的一半），并改用 64 位 javaw.exe。",
                any: new[] { "Could not create the Java Virtual Machine", "Could not reserve enough space", "Invalid maximum heap size", "The specified size exceeds the maximum representable size", "Error occurred during initialization of VM" }),
            new("bad-jvm-arg", "严重", "JVM 参数无效",
                "有无法识别或不适用于当前 Java 的启动参数。请到版本设置「JVM 参数」删掉额外参数后再试，尤其是从网上复制来的 -XX: 选项。",
                any: new[] { "Unrecognized option", "Unrecognized VM option", "Could not find agent library", "Error: Could not find or load main class" }),
            new("java-too-old", "严重", "Java 版本过低",
                "当前 Java 无法运行这个 Minecraft / 加载器版本。请开启「按游戏版本自动匹配 Java」，或手动选择更高版本的官方 Java（1.17+ 需要 17，1.20.5+ 需要 21）。",
                any: new[] { "UnsupportedClassVersionError", "unsupported class file version", "has been compiled by a more recent version of the Java Runtime", "Unsupported major.minor version", "java.lang.UnsupportedClassVersionError" }),
            new("openj9", "警告", "正在使用 OpenJ9",
                "OpenJ9 与不少模组/Mixin 不兼容。请改用 Eclipse Temurin、Microsoft 或 Mojang 官方的 HotSpot Java（javaw.exe）。",
                any: new[] { "OpenJ9", "J9VMInternals", "Eclipse OpenJ9" }),
            new("hs-err", "严重", "Java 虚拟机在本地代码中崩溃",
                "这是 JVM / 显卡驱动 / 原生库问题，而不是普通的 Minecraft 异常。请更新显卡驱动，确认 natives 完整，并避免把 -Xmx 设得过大。必要时更换 Java 发行版。",
                any: new[] { "A fatal error has been detected by the Java Runtime Environment", "EXCEPTION_ACCESS_VIOLATION", "Problematic frame:" }),
            new("graphics", "警告", "显卡或渲染库异常",
                "GLFW / OpenGL / 显卡驱动未能正常初始化。请更新显卡驱动、在系统图形设置里指定独显，并关掉覆盖层（如 Discord、NVIDIA 滤镜）。笔记本外接屏时也可试一下切回内屏。",
                any: new[] { "GLFW error", "Failed to create GLFW window", "Could not initialize class org.lwjgl", "lwjgl.dll", "nvoglv64", "atio6axx", "ig7icd64", "ig9icd64", "No OpenGL context" }),
            new("natives", "严重", "原生库缺失或无法加载",
                "natives 没解压好，或被安全软件拦截。请删除该版本目录下的 natives 文件夹后重新启动，让启动器重新解压；并把游戏目录加入杀毒软件白名单。",
                any: new[] { "UnsatisfiedLinkError", "Can't load IA 32-bit .dll on a AMD 64-bit platform", "Native library", "failed to load a library", "Unable to extract natives" }),
            new("fabric-api", "严重", "缺少 Fabric API",
                "Fabric 模组依赖 Fabric API。请到「下载」页安装对应游戏版本的 Fabric API，或把 fabric-api 的 jar 放进该版本的 mods 文件夹。",
                any: new[] { "fabric-api", "net.fabricmc.fabric-api", "Mod 'fabric-api'", "requires fabric" },
                all: new[] { "fabric" }),
            new("mod-resolution", "严重", "模组缺失依赖或互不兼容",
                "加载器在解析模组时失败。请根据日志里的 Mod X requires Y 安装缺失模组，或移除冲突的模组。版本设置「模组管理」里可以临时禁用可疑 jar。",
                any: new[] { "ModResolutionException", "Missing required mods", "Incompatible mods found", "requires version", "Unmet dependency", "Failed to load mod" }),
            new("duplicate-mod", "严重", "安装了重复的模组",
                "mods 文件夹里有两份相同模组（不同版本也会冲突）。请只保留一份，删掉重复的 jar。",
                any: new[] { "DuplicateModsFoundException", "duplicate mods", "ModDuplicationException", "Found duplicate mods" }),
            new("mixin", "严重", "Mixin 冲突",
                "多个模组在改同一处代码时冲突。请更新模组，或轮流禁用最近添加的模组来定位。Fabric 上不要装 OptiFine，Forge 上注意 Mixin 引导是否重复。",
                any: new[] { "MixinApplyError", "mixin apply failed", "Mixin transformation of", "Error applying Mixin", "org.spongepowered.asm.mixin" }),
            new("optifine-fabric", "严重", "OptiFine 与 Fabric 不兼容",
                "Fabric 请改用 Sodium / Iris 等替代，不要安装 OptiFine。从该版本的 mods 文件夹移除 OptiFine 后再启动。",
                all: new[] { "optifine", "fabric" }),
            new("optifine-sodium", "严重", "OptiFine 与 Sodium 冲突",
                "两者不要一起用。Fabric 只留 Sodium（和 Iris），Forge 若用 OptiFine 就不要再装 Sodium 移植。",
                all: new[] { "optifine", "sodium" }),
            new("forge-mod", "严重", "Forge / NeoForge 模组加载失败",
                "某个模组在加载阶段崩溃。根据崩溃报告里的 Mod File / Exception 禁用对应模组，并确认它支持当前 Minecraft 与加载器版本。",
                any: new[] { "LoaderExceptionModCrash", "ModLoadingException", "net.minecraftforge", "net.neoforged", "Error loading class" }),
            new("nosuch", "警告", "模组与游戏/加载器版本不匹配",
                "出现 NoSuchMethod / NoSuchField / NoClassDefFound，通常是模组版本不对，或缺少前置。请核对 Minecraft 版本、加载器版本，并补齐依赖。",
                any: new[] { "NoSuchMethodError", "NoSuchFieldError", "NoClassDefFoundError", "ClassNotFoundException", "AbstractMethodError" }),
            new("module", "警告", "Java 模块系统拒绝访问",
                "较新的 Java 默认禁止反射部分内部 API。请开启自动匹配 Java，或在 JVM 参数中按模组说明添加 --add-opens，不要使用过新的 Java 跑旧模组。",
                any: new[] { "InaccessibleObjectException", "module java.base does not", "cannot access class" }),
            new("file-lock", "警告", "文件被占用或没有权限",
                "游戏目录或存档被占用。请关闭已打开的 Minecraft、资源管理器预览和同步盘，以管理员以外的普通用户运行，并把游戏目录从只读/受控文件夹里排除。",
                any: new[] { "AccessDeniedException", "The process cannot access the file", "being used by another process", "Permission denied" }),
            new("disk", "严重", "磁盘空间不足",
                "请清理磁盘后再启动，尤其是游戏目录所在盘。资源与模组下载也会失败。",
                any: new[] { "No space left on device", "There is not enough space on the disk" }),
        };

        private sealed class Rule
        {
            private readonly string[] _any;
            private readonly string[] _all;
            private readonly string _severity;
            private readonly string _title;
            private readonly string _advice;

            public string Id { get; }

            public Rule(string id, string severity, string title, string advice, string[]? any = null, string[]? all = null)
            {
                Id = id;
                _severity = severity;
                _title = title;
                _advice = advice;
                _any = any ?? Array.Empty<string>();
                _all = all ?? Array.Empty<string>();
            }

            public bool Matches(string blob)
            {
                if (_all.Length > 0 && _all.Any(k => IndexOf(blob, k) < 0))
                    return false;
                if (_any.Length == 0) return _all.Length > 0;
                return _any.Any(k => IndexOf(blob, k) >= 0);
            }

            public CrashFinding Build(string blob)
            {
                string? evidence = null;
                foreach (var key in _any.Concat(_all))
                {
                    var i = IndexOf(blob, key);
                    if (i < 0) continue;
                    var from = Math.Max(0, i - 40);
                    var len = Math.Min(blob.Length - from, key.Length + 80);
                    evidence = blob.Substring(from, len).Replace('\n', ' ').Trim();
                    break;
                }
                return new CrashFinding
                {
                    Id = Id,
                    Title = _title,
                    Advice = _advice,
                    Severity = _severity,
                    Evidence = evidence
                };
            }

            private static int IndexOf(string blob, string key)
                => blob.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        }
    }
}
