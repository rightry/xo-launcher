using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XO_Launcher.Api;
using XO_Launcher.Cli;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 把命令行那套启动器功能包装成 AI 可调用的工具（function-calling）。
    /// 数据类工具（list/install/import/download/account/set）复用 <see cref="CliRunner"/>，
    /// 把它的控制台输出捕获成字符串回灌给模型；GUI 类工具（launch/open）由 AiPage
    /// 通过 <c>MainWindow.ApplyCliIntent</c> 执行。
    /// </summary>
    public static class LauncherToolService
    {
        public const string ToolListVersions = "list_versions";
        public const string ToolOpenPage = "open_page";
        public const string ToolLaunchGame = "launch_game";
        public const string ToolInstallVersion = "install_version";
        public const string ToolImportModpack = "import_modpack";
        public const string ToolDownloadResource = "download_resource";
        public const string ToolManageAccount = "manage_account";
        public const string ToolUpdateSetting = "update_setting";
        public const string ToolSearchMods = "search_mods";
        public const string ToolInstallMod = "install_mod";
        public const string ToolSearchModpacks = "search_modpacks";
        public const string ToolInstallModpack = "install_modpack";
        public const string ToolListLibrary = "list_library";
        public const string ToolInstallLibraryItem = "install_library_item";
        public const string ToolRemoveInstanceMod = "remove_mod";
        public const string ToolDeleteLibraryItem = "delete_library_item";
        public const string ToolGetInstanceSettings = "get_instance_settings";
        public const string ToolSetInstanceSetting = "set_instance_setting";

        private static readonly AiToolDef[] CatalogValue =
        {
            new(ToolListVersions,
                "列出游戏目录里已安装的所有版本（含原版与各加载器），并标出当前选中的版本。只读，不改动任何东西。当需要知道有哪些版本、版本 ID 或当前选中项时调用。",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),

            new(ToolOpenPage,
                "打开启动器界面里的某个页面。",
                "{\"type\":\"object\",\"properties\":{\"page\":{\"type\":\"string\",\"description\":\"要打开的页面\",\"enum\":[\"home\",\"download\",\"library\",\"online\",\"ai\",\"skin\",\"settings\",\"messages\"]}},\"required\":[\"page\"],\"additionalProperties\":false}"),

            new(ToolLaunchGame,
                "启动 Minecraft 游戏。可选临时覆盖版本、玩家名、内存、Java 路径；不传则用当前设置。这是会真正拉起游戏进程的操作。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"version\":{\"type\":\"string\",\"description\":\"要启动的版本 ID，缺省用当前选中版本\"},"
                + "\"user\":{\"type\":\"string\",\"description\":\"临时使用的玩家名\"},"
                + "\"memory\":{\"type\":\"integer\",\"description\":\"临时分配的内存 MB\"},"
                + "\"java\":{\"type\":\"string\",\"description\":\"临时使用的 Java 可执行文件路径\"}"
                + "},\"additionalProperties\":false}"),

            new(ToolInstallVersion,
                "安装一个 Minecraft 版本（原版或带加载器）。这是耗时的大额下载操作。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"version\":{\"type\":\"string\",\"description\":\"Minecraft 版本 ID，例如 1.20.4\"},"
                + "\"loader\":{\"type\":\"string\",\"description\":\"加载器，缺省 vanilla\",\"enum\":[\"vanilla\",\"fabric\",\"forge\",\"neoforge\",\"quilt\"]},"
                + "\"loader_version\":{\"type\":\"string\",\"description\":\"加载器版本，缺省取该加载器推荐版本\"},"
                + "\"name\":{\"type\":\"string\",\"description\":\"实例显示名，缺省自动生成\"}"
                + "},\"required\":[\"version\"],\"additionalProperties\":false}"),

            new(ToolImportModpack,
                "通过 xolauncher:// 分享链接导入自制整合包（安装版本+加载器+模组+配置）。耗时下载操作。",
                "{\"type\":\"object\",\"properties\":{\"link\":{\"type\":\"string\",\"description\":\"xolauncher:// 开头的分享链接\"}},\"required\":[\"link\"],\"additionalProperties\":false}"),

            new(ToolDownloadResource,
                "下载资源：传 http(s) 直链则把文件下到下载目录；传版本 ID 则等同安装该版本。耗时下载操作。",
                "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"description\":\"http(s) 直链，或要下载安装的版本 ID\"}},\"required\":[\"target\"],\"additionalProperties\":false}"),

            new(ToolManageAccount,
                "查询或切换游戏账号。不传 name 时返回当前账号；传 name 时新建并选中一个离线账号。",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"离线玩家名；不传表示只查询当前账号\"}},\"additionalProperties\":false}"),

            new(ToolUpdateSetting,
                "修改一项启动器设置。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"key\":{\"type\":\"string\",\"description\":\"设置项\",\"enum\":[\"memory\",\"gamedir\",\"downloaddir\",\"java\",\"accent\",\"theme\",\"font\",\"mirror\",\"username\",\"version\"]},"
                + "\"value\":{\"type\":\"string\",\"description\":\"新值（memory 传 MB 数字）\"}"
                + "},\"required\":[\"key\",\"value\"],\"additionalProperties\":false}"),

            new(ToolSearchMods,
                "按名称在 Modrinth 上搜索模组，返回候选列表（名称、slug、简介、支持的游戏版本）。只读。用户给出模组名、需要拿到可安装的模组或确认模组是否存在时调用；拿到 slug 后可用 install_mod 安装。要查多个模组时用 queries 一次查完，不要连续调用多次。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"query\":{\"type\":\"string\",\"description\":\"单个模组名称或关键词\"},"
                + "\"queries\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"一次查多个模组名（最多 8 个，并行搜索）。也可以在一个 query 里用逗号/顿号/分号分隔多个名字\"},"
                + "\"limit\":{\"type\":\"integer\",\"description\":\"每个关键词返回条数，默认 6，最多 15\"}"
                + "},\"additionalProperties\":false}"),

            new(ToolInstallMod,
                "搜索并一键下载某个模组及其必需前置模组到资源下载库（仅 Modrinth）。这是耗时的下载操作：工具会等文件全部下载结束才返回，结果里带每个文件的成功/失败状态，因此调用前不要向用户承诺「稍后完成」，拿到返回即可按实际结果说明。文件入库后仍需用户在版本设置的「模组管理」里安装到实例。缺省按当前选中游戏版本的 Minecraft 版本与加载器选取兼容文件。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\",\"description\":\"模组名称或 Modrinth slug / 项目 ID\"},"
                + "\"game_version\":{\"type\":\"string\",\"description\":\"Minecraft 版本，缺省用当前选中实例的版本\"},"
                + "\"loader\":{\"type\":\"string\",\"description\":\"加载器，缺省用当前选中实例的加载器\",\"enum\":[\"fabric\",\"forge\",\"neoforge\",\"quilt\"]}"
                + "},\"required\":[\"name\"],\"additionalProperties\":false}"),

            new(ToolSearchModpacks,
                "在 Modrinth 上搜索整合包，返回候选列表（名称、slug、下载量、支持的 Minecraft 版本、简介）。只读。用户想要现成的整合包（某个主题、某个模组组合、或「给我一个能直接玩的 XX」）时调用；拿到 slug 后用 install_modpack 安装。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"query\":{\"type\":\"string\",\"description\":\"整合包名称或关键词\"},"
                + "\"limit\":{\"type\":\"integer\",\"description\":\"返回条数，默认 6，最多 15\"}"
                + "},\"required\":[\"query\"],\"additionalProperties\":false}"),

            new(ToolInstallModpack,
                "从 Modrinth 安装整合包：按 slug / 项目 ID / 名称找到项目，取其 .mrpack 版本，自动装对应 Minecraft 版本与加载器、下载包内模组并写入 overrides，结果是一个新的游戏实例。这是耗时的下载操作，会等安装真正结束才返回，返回里带实例 ID 与失败原因。只支持 Modrinth 的 .mrpack；用户之间分享的 xolauncher:// 链接要用 import_modpack。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\",\"description\":\"整合包 slug、项目 ID 或名称\"},"
                + "\"game_version\":{\"type\":\"string\",\"description\":\"可选：只取兼容该 Minecraft 版本的整合包版本\"}"
                + "},\"required\":[\"name\"],\"additionalProperties\":false}"),

            new(ToolListLibrary,
                "列出本机资源下载库里的内容（模组 / 光影 / 资源包 / 整合包），显示文件名、显示名、版本、适配的游戏版本与大小。只读。需要用 install_mod 下载的内容入库后确认、或安装/删除资源库条目之前查看时调用。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"type\":{\"type\":\"string\",\"description\":\"资源类型，缺省 mod\",\"enum\":[\"mod\",\"shader\",\"resourcepack\",\"modpack\"]}"
                + "},\"additionalProperties\":false}"),

            new(ToolInstallLibraryItem,
                "把资源库里已有的条目安装（复制）到某个游戏版本的实例目录。按文件名或显示名匹配，匹配到多个时会列出候选让你重新指定。整合包（modpack）不支持安装到版本。"
                + "换版本时必须先用 remove_mod 删掉该版本里的旧模组：同名文件会被跳过（等于没换成功），而文件名不同的旧版本会和新版本共存，游戏会报重复模组或直接崩溃。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\",\"description\":\"资源库条目的文件名或显示名\"},"
                + "\"type\":{\"type\":\"string\",\"description\":\"资源类型，缺省 mod\",\"enum\":[\"mod\",\"shader\",\"resourcepack\"]},"
                + "\"version\":{\"type\":\"string\",\"description\":\"目标游戏版本 ID，缺省用当前选中版本\"}"
                + "},\"required\":[\"name\"],\"additionalProperties\":false}"),

            new(ToolRemoveInstanceMod,
                "从某个游戏版本（实例）的目录里删除指定的模组 / 光影 / 资源包文件。按文件名、显示名与 mod id 匹配：匹配到多个时返回候选列表让你用精确文件名重试，不会替你猜。只删该版本目录里的文件，资源库里的原文件保留（之后还能再装）。这是写操作，会先弹确认。"
                + "替换模组的标准流程是：先 remove_mod 删掉旧文件，再 install_library_item 装新文件。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\",\"description\":\"要删除的模组文件名、显示名或 mod id\"},"
                + "\"type\":{\"type\":\"string\",\"description\":\"资源类型，缺省 mod\",\"enum\":[\"mod\",\"shader\",\"resourcepack\"]},"
                + "\"version\":{\"type\":\"string\",\"description\":\"目标游戏版本 ID，缺省用当前选中版本\"}"
                + "},\"required\":[\"name\"],\"additionalProperties\":false}"),

            new(ToolDeleteLibraryItem,
                "从资源下载库中删除条目（不会动任何游戏版本里已安装的文件）。按文件名或显示名匹配，匹配到多个时会列出候选让你重新指定；要删除某内容的全部版本时传 all=true。删除不可恢复。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"name\":{\"type\":\"string\",\"description\":\"资源库条目的文件名或显示名\"},"
                + "\"type\":{\"type\":\"string\",\"description\":\"资源类型，缺省 mod\",\"enum\":[\"mod\",\"shader\",\"resourcepack\",\"modpack\"]},"
                + "\"all\":{\"type\":\"boolean\",\"description\":\"为 true 时删除全部匹配条目，缺省 false\"}"
                + "},\"required\":[\"name\"],\"additionalProperties\":false}"),

            new(ToolGetInstanceSettings,
                "查看某个游戏版本(实例)的详细启动设置：是否启用独立设置、生效的内存 / Java / 自动匹配 Java、窗口模式与尺寸、额外 JVM 与游戏参数、自定义显示名，并同时列出全局回落值。只读。排查某版本专属的启动/内存/Java/参数问题时先用它看清现状。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"version\":{\"type\":\"string\",\"description\":\"版本 ID，缺省用当前选中版本\"}"
                + "},\"additionalProperties\":false}"),

            new(ToolSetInstanceSetting,
                "修改某个游戏版本(实例)的详细启动设置（每实例覆盖；会自动开启该版本的独立设置使其生效）。用于只针对单个版本调整内存、Java、自动匹配 Java、额外 JVM / 游戏参数、窗口模式与尺寸、显示名等，而不影响全局设置。改前建议先用 get_instance_settings 查看现状。",
                "{\"type\":\"object\",\"properties\":{"
                + "\"version\":{\"type\":\"string\",\"description\":\"版本 ID，缺省用当前选中版本\"},"
                + "\"key\":{\"type\":\"string\",\"description\":\"设置项\",\"enum\":[\"memory\",\"java\",\"auto_java\",\"jvm_args\",\"game_args\",\"window_mode\",\"width\",\"height\",\"name\",\"independent\"]},"
                + "\"value\":{\"type\":\"string\",\"description\":\"新值：memory/width/height 传正整数；auto_java/independent 传 true 或 false；window_mode 传 default/fullscreen/maximized/custom；java/jvm_args/game_args/name 传文本，传空字符串表示清空\"}"
                + "},\"required\":[\"key\",\"value\"],\"additionalProperties\":false}")
        };

        public static IReadOnlyList<AiToolDef> Catalog => CatalogValue;

        /// <summary>
        /// 执行前是否需要用户确认。默认一律先等确认；用户在 AI 页输入框旁的「权限」里
        /// 选成 auto 后全部静默执行（含删除资源库文件），所以那一档在 UI 上写清了风险。
        /// </summary>
        public static bool NeedsConfirmation(string tool, string argsJson)
            => LauncherSettings.Instance.AiPermission != LauncherSettings.AiPermissionAuto;

        /// <summary>是否需要开主窗口执行（由 AiPage 调 MainWindow）。</summary>
        public static bool IsGuiTool(string tool) => tool is ToolLaunchGame or ToolOpenPage;

        /// <summary>给确认弹窗与工具日志用的一句话中文描述。</summary>
        public static string Describe(string tool, string argsJson)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            switch (tool)
            {
                case ToolListVersions:
                    return "查询已安装版本列表（只读）";
                case ToolOpenPage:
                    return $"打开页面：{Str(root, "page") ?? "home"}";
                case ToolLaunchGame:
                {
                    var v = Str(root, "version");
                    return string.IsNullOrWhiteSpace(v) ? "启动游戏（用当前选中版本）" : $"启动游戏：{v}";
                }
                case ToolInstallVersion:
                {
                    var v = Str(root, "version") ?? "?";
                    var loader = Str(root, "loader");
                    var suffix = string.IsNullOrWhiteSpace(loader) || loader == "vanilla" ? "" : $" + {loader}";
                    return $"安装版本：{v}{suffix}";
                }
                case ToolImportModpack:
                    return "导入整合包分享链接";
                case ToolDownloadResource:
                    return $"下载资源：{Str(root, "target") ?? "?"}";
                case ToolManageAccount:
                {
                    var n = Str(root, "name");
                    return string.IsNullOrWhiteSpace(n) ? "查询当前账号（只读）" : $"新建并切换离线账号：{n}";
                }
                case ToolUpdateSetting:
                    return $"修改设置：{Str(root, "key") ?? "?"} = {Str(root, "value") ?? ""}";
                case ToolSearchMods:
                {
                    // 一次查多个时卡片标题要能看出来，不能只显示第一个或 "?"
                    var names = ReadSearchQueries(root);
                    var shown = names.Count == 0
                        ? "?"
                        : string.Join("、", names.Take(4)) + (names.Count > 4 ? $" 等 {names.Count} 个" : "");
                    return $"搜索模组：{shown}（只读）";
                }
                case ToolInstallMod:
                {
                    var gv = Str(root, "game_version");
                    var ld = Str(root, "loader");
                    var ctx = gv is null && ld is null ? "" : $"（{gv ?? "当前版本"} / {ld ?? "当前加载器"}）";
                    return $"一键下载模组及前置：{Str(root, "name") ?? "?"}{ctx}";
                }
                case ToolSearchModpacks:
                    return $"搜索整合包：{Str(root, "query") ?? "?"}（只读）";
                case ToolInstallModpack:
                {
                    var gv = Str(root, "game_version");
                    return $"安装整合包：{Str(root, "name") ?? "?"}"
                        + (string.IsNullOrWhiteSpace(gv) ? "" : $"（兼容 {gv}）");
                }
                case ToolListLibrary:
                    return $"查看资源库：{ContentKind.DisplayName(NormalizeKind(Str(root, "type")))}（只读）";
                case ToolInstallLibraryItem:
                {
                    var v = Str(root, "version");
                    return $"从资源库安装到版本：{Str(root, "name") ?? "?"}"
                        + (string.IsNullOrWhiteSpace(v) ? "（当前选中版本）" : $"（{v}）");
                }
                case ToolRemoveInstanceMod:
                {
                    var v = Str(root, "version");
                    return $"从版本删除模组：{Str(root, "name") ?? "?"}"
                        + (string.IsNullOrWhiteSpace(v) ? "（当前选中版本）" : $"（{v}）");
                }
                case ToolDeleteLibraryItem:
                    return $"从资源库删除：{Str(root, "name") ?? "?"}";
                case ToolGetInstanceSettings:
                {
                    var v = Str(root, "version");
                    return string.IsNullOrWhiteSpace(v) ? "查看当前版本的实例设置（只读）" : $"查看版本 {v} 的实例设置（只读）";
                }
                case ToolSetInstanceSetting:
                {
                    var v = Str(root, "version");
                    var target = string.IsNullOrWhiteSpace(v) ? "当前版本" : v;
                    return $"修改版本设置：{target} · {Str(root, "key") ?? "?"} = {Str(root, "value") ?? ""}";
                }
                default:
                    return tool;
            }
        }

        /// <summary>执行数据类工具，返回给模型阅读的文本结果。GUI 工具不在此处理。</summary>
        public static async Task<string> ExecuteAsync(string tool, string argsJson, CancellationToken ct = default)
        {
            if (IsGuiTool(tool))
                return "该操作需在界面上执行，已由启动器处理。";

            if (tool == ToolSearchMods)
                return await SearchModsAsync(argsJson, ct);
            if (tool == ToolInstallMod)
                return await InstallModAsync(argsJson, ct);
            if (tool == ToolSearchModpacks)
                return await SearchModpacksAsync(argsJson, ct);
            if (tool == ToolInstallModpack)
                return await InstallModpackAsync(argsJson, ct);
            if (tool == ToolListLibrary)
                return ListLibrary(argsJson);
            if (tool == ToolInstallLibraryItem)
                return InstallLibraryItem(argsJson);
            if (tool == ToolRemoveInstanceMod)
                return RemoveInstanceMod(argsJson);
            if (tool == ToolDeleteLibraryItem)
                return DeleteLibraryItem(argsJson);
            if (tool == ToolGetInstanceSettings)
                return GetInstanceSettings(argsJson);
            if (tool == ToolSetInstanceSetting)
                return SetInstanceSetting(argsJson);

            CliRequest req;
            try
            {
                req = BuildRequest(tool, argsJson);
            }
            catch (Exception ex)
            {
                return "参数错误：" + ex.Message;
            }

            var sb = new StringBuilder();
            ConsoleInterop.SetSink(sb);
            int code;
            try
            {
                code = await CliRunner.RunHeadlessAsync(req).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", tool + " 执行失败", ex);
                return "执行失败：" + ex.Message;
            }
            finally
            {
                ConsoleInterop.SetSink(null);
            }

            var text = sb.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(text))
                text = code == 0 ? "已完成。" : $"未完成（退出码 {code}）。";
            else if (code != 0)
                text += $"\n（退出码 {code}）";
            return text;
        }

        /// <summary>把工具调用参数映射成 <see cref="CliRequest"/>。</summary>
        public static CliRequest BuildRequest(string tool, string argsJson)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            switch (tool)
            {
                case ToolListVersions:
                    return new CliRequest { Command = CliCommand.List, Options = opts };

                case ToolOpenPage:
                    return new CliRequest { Command = CliCommand.Open, Arg = Str(root, "page"), Options = opts };

                case ToolLaunchGame:
                    Put(opts, "user", Str(root, "user"));
                    Put(opts, "memory", Str(root, "memory"));
                    Put(opts, "java", Str(root, "java"));
                    return new CliRequest { Command = CliCommand.Launch, Arg = Str(root, "version"), Options = opts };

                case ToolInstallVersion:
                    Put(opts, "loader", Str(root, "loader"));
                    Put(opts, "loader-version", Str(root, "loader_version"));
                    Put(opts, "name", Str(root, "name"));
                    return new CliRequest { Command = CliCommand.Install, Arg = Str(root, "version"), Options = opts };

                case ToolImportModpack:
                    return new CliRequest { Command = CliCommand.Import, Arg = Str(root, "link"), Options = opts };

                case ToolDownloadResource:
                    return new CliRequest { Command = CliCommand.Download, Arg = Str(root, "target"), Options = opts };

                case ToolManageAccount:
                    return new CliRequest { Command = CliCommand.Account, Arg = Str(root, "name"), Options = opts };

                case ToolUpdateSetting:
                    return new CliRequest { Command = CliCommand.Set, Arg = Str(root, "key"), Arg2 = Str(root, "value"), Options = opts };

                default:
                    throw new InvalidOperationException("未知工具：" + tool);
            }
        }

        // ==================== 模组搜索 / 一键安装（Modrinth） ====================

        /// <summary>一次最多并行搜多少个关键词。再多就超出单次回答能装下的信息量了。</summary>
        private const int MaxSearchQueries = 8;

        private static async Task<string> SearchModsAsync(string argsJson, CancellationToken ct)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var queries = ReadSearchQueries(root);
            if (queries.Count == 0)
                return "请提供要搜索的模组名称：query 给单个，或 queries 一次给多个。";

            var limit = 6;
            if (int.TryParse(Str(root, "limit"), out var l)) limit = Math.Clamp(l, 1, 15);

            var api = new ModrinthApi();
            // 多关键词并行搜：串行会把每个请求的往返时间叠加成十几秒
            var tasks = queries.Select(query => SearchOneModAsync(api, query, limit, ct)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            if (queries.Count == 1) return results[0];

            var sb = new StringBuilder();
            sb.AppendLine($"一次查询 {queries.Count} 个关键词：{string.Join("、", queries)}");
            for (var i = 0; i < queries.Count; i++)
            {
                sb.AppendLine();
                sb.AppendLine($"【{queries[i]}】");
                sb.AppendLine(results[i]);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 收集要搜的关键词：queries 数组 + query 单串（按常见分隔符拆开），去重并截断到上限。
        /// 模型有时会把「sodium、iris、lithium」整串塞进 query，这里一并兜住。
        /// </summary>
        private static List<string> ReadSearchQueries(JsonElement root)
        {
            var raw = new List<string>();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("queries", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        raw.Add(item.GetString() ?? "");
            }
            raw.Add(Str(root, "query") ?? "");

            return raw
                .SelectMany(part => part.Split(new[] { ',', '，', '、', ';', '；', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries))
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxSearchQueries)
                .ToList();
        }

        private static async Task<string> SearchOneModAsync(
            ModrinthApi api, string query, int limit, CancellationToken ct)
        {
            try
            {
                var resp = await api.SearchAsync(ContentKind.Mod, query, 0, limit, "downloads", null, ct);
                var hits = resp.Hits ?? new List<ModrinthHit>();
                if (hits.Count == 0) return $"没找到与「{query}」相关的模组。";

                var sb = new StringBuilder();
                sb.AppendLine($"找到约 {resp.TotalHits} 个相关模组，显示前 {hits.Count} 个（可用 install_mod 按 slug 安装）：");
                foreach (var h in hits)
                {
                    var desc = h.Description ?? "";
                    if (desc.Length > 60) desc = desc[..60] + "…";
                    var games = h.Versions is { Count: > 0 }
                        ? string.Join(",", h.Versions.TakeLast(4))
                        : "—";
                    sb.AppendLine($"- 《{h.Title}》 slug={h.Slug} id={h.ProjectId} | 游戏版本: {games} | {desc}");
                }
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "search_mods 失败", ex);
                return $"「{query}」搜索失败：{ex.Message}";
            }
        }

        private static async Task<string> InstallModAsync(string argsJson, CancellationToken ct)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var name = Str(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return "请提供模组名称或 slug。";

            var gameVersion = Str(root, "game_version");
            var loader = NormalizeLoader(Str(root, "loader"));
            if (string.IsNullOrWhiteSpace(gameVersion) || string.IsNullOrWhiteSpace(loader))
            {
                var inst = InstalledVersionService.Find(LauncherSettings.Instance.SelectedVersion);
                if (inst is not null)
                {
                    if (string.IsNullOrWhiteSpace(gameVersion)) gameVersion = inst.MinecraftVersion;
                    if (string.IsNullOrWhiteSpace(loader)) loader = NormalizeLoader(inst.LoaderId);
                }
            }
            if (string.IsNullOrWhiteSpace(gameVersion))
                return "无法确定 Minecraft 版本：请先在首页选中一个游戏版本，或在参数里给出 game_version。";
            if (string.IsNullOrWhiteSpace(loader))
                return "当前选中的实例是原版（没有模组加载器），装不了模组。启动器不会替你补装加载器："
                    + "请让用户到「下载」页选 Minecraft 版本后安装 Fabric / Forge / NeoForge / Quilt 加载器版本，再回来重试。"
                    + "（若用户已在参数里指定了 loader 与版本，也可以直接用 install_version 装一个带加载器的实例。）";

            try
            {
                var api = new ModrinthApi();
                var title = name;
                ModrinthVersion? version = null;

                try { version = await api.GetLatestCompatibleAsync(name, gameVersion, loader, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { version = null; }

                if (version is null)
                {
                    var resp = await api.SearchAsync(ContentKind.Mod, name, 0, 5, "downloads", null, ct);
                    var hit = (resp.Hits ?? new List<ModrinthHit>()).FirstOrDefault();
                    if (hit is null) return $"没找到叫「{name}」的模组。";
                    title = string.IsNullOrWhiteSpace(hit.Title) ? name : hit.Title;
                    try { version = await api.GetLatestCompatibleAsync(hit.ProjectId, gameVersion, loader, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { version = null; }
                }

                if (version is null)
                    return $"《{title}》没有适配 {gameVersion} / {loader} 的可下载版本。";

                var plans = await ModDependencyService.ResolveAsync(api, version, gameVersion, loader, ct);
                if (plans.Count == 0) return $"《{title}》没有可下载的文件。";

                var fingerprint = ModDependencyService.ScanLibrary(ContentKind.Mod);
                var enqueued = ModDependencyService.EnqueueToLibrary(plans, ContentKind.Mod, fingerprint);
                var depCount = plans.Count(p => p.IsDependency);
                var sb = new StringBuilder();
                sb.Append($"《{title}》{version.VersionNumber}");
                sb.Append(depCount > 0 ? $" 及其 {depCount} 个前置" : "（无前置）");
                sb.AppendLine($"（共 {plans.Count} 个文件，目标 {gameVersion} / {loader}）：");
                foreach (var p in plans.Take(30))
                {
                    var tag = p.IsDependency ? "前置" : "本体";
                    var state = fingerprint.Contains(p) ? "（已在资源库，跳过）" : "";
                    sb.AppendLine($"  {tag} · {p.ProjectTitle}（{p.Filename}）{state}");
                }
                if (plans.Count > 30) sb.AppendLine($"  …等共 {plans.Count} 个");
                sb.AppendLine($"新下载 {enqueued.Tasks.Count} 个；已在资源库 {enqueued.InLibrary} 个（跳过）；已在队列 {enqueued.InQueue} 个。");

                if (enqueued.Tasks.Count == 0)
                {
                    sb.Append("资源库里已有全部内容，无需重复下载。请提示用户到版本设置的「模组管理」里安装到实例。");
                    return sb.ToString();
                }

                // 等下载真的跑完再回复：只报「已加入队列」的话，模型会对用户说「装好了」，
                // 而文件其实还在下载甚至已经失败。
                var interrupted = false;
                try
                {
                    await DownloadService.WaitForAsync(enqueued.Tasks, ct);
                }
                catch (OperationCanceledException)
                {
                    interrupted = true;
                }

                var succeeded = enqueued.Tasks.Count(t => t.Status == "已完成");
                var unfinished = enqueued.Tasks.Where(t => t.Status != "已完成").ToList();
                sb.AppendLine(interrupted
                    ? $"用户中断了本次对话，等待停止；{unfinished.Count} 个文件仍在后台继续下载，可在「消息」页看进度。"
                    : $"下载结束：成功 {succeeded} 个，未成功 {unfinished.Count} 个。");
                foreach (var t in unfinished.Take(10))
                {
                    var detail = string.IsNullOrWhiteSpace(t.Detail) ? "" : " · " + t.Detail;
                    sb.AppendLine($"  未成功 · {t.Title}（{t.FileName}）：{t.Status}{detail}");
                }
                if (succeeded > 0)
                    sb.Append("成功的文件已进资源库，请提示用户到版本设置的「模组管理」里安装到实例。");
                else if (!interrupted)
                    sb.Append("没有一个文件下载成功：请让用户检查网络，或在设置里把下载源换成镜像（可用 update_setting 改 mirror）。");
                return sb.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "install_mod 失败", ex);
                return "安装失败：" + ex.Message;
            }
        }

        // ==================== 整合包（Modrinth .mrpack） ====================

        private static async Task<string> SearchModpacksAsync(string argsJson, CancellationToken ct)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var query = Str(root, "query");
            if (string.IsNullOrWhiteSpace(query)) return "缺少参数 query：请给出整合包名称或关键词。";
            var limit = 6;
            if (int.TryParse(Str(root, "limit"), out var l)) limit = Math.Clamp(l, 1, 15);

            try
            {
                var api = new ModrinthApi();
                var resp = await api.SearchAsync(ContentKind.Modpack, query, 0, limit, "downloads", null, ct);
                var hits = resp.Hits ?? new List<ModrinthHit>();
                if (hits.Count == 0) return $"没找到与「{query}」相关的整合包。";

                var sb = new StringBuilder();
                sb.AppendLine($"找到约 {resp.TotalHits} 个相关整合包，显示前 {hits.Count} 个（用 install_modpack 按 slug 安装）：");
                foreach (var h in hits)
                {
                    var desc = h.Description ?? "";
                    if (desc.Length > 60) desc = desc[..60] + "…";
                    var games = h.Versions is { Count: > 0 } ? string.Join(",", h.Versions.TakeLast(4)) : "—";
                    sb.AppendLine($"- 《{h.Title}》 slug={h.Slug} id={h.ProjectId} | Minecraft 版本: {games} | 下载 {h.Downloads} | {desc}");
                }
                sb.AppendLine("注意：只有 Modrinth 的 .mrpack 能自动安装，CurseForge 整合包不行。");
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "search_modpacks 失败", ex);
                return "搜索整合包失败：" + ex.Message;
            }
        }

        private static async Task<string> InstallModpackAsync(string argsJson, CancellationToken ct)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var name = Str(root, "name");
            var wantVersion = Str(root, "game_version");
            if (string.IsNullOrWhiteSpace(name)) return "缺少参数 name：请给出整合包 slug、项目 ID 或名称。";

            try
            {
                var api = new ModrinthApi();
                var project = await api.GetProjectAsync(name.Trim(), ct);
                if (project is null)
                {
                    var resp = await api.SearchAsync(ContentKind.Modpack, name, 0, 5, "downloads", null, ct);
                    var hit = (resp.Hits ?? new List<ModrinthHit>()).FirstOrDefault();
                    if (hit is null) return $"没找到叫「{name}」的整合包。先用 search_modpacks 确认 slug。";
                    project = await api.GetProjectAsync(hit.ProjectId, ct);
                    if (project is null) return $"整合包《{hit.Title}》详情获取失败，请稍后再试。";
                }
                if (!string.Equals(project.ProjectType, ContentKind.Modpack, StringComparison.OrdinalIgnoreCase))
                    return $"「{project.Title}」不是整合包（Modrinth 类型：{project.ProjectType}）。单个模组请用 install_mod。";

                var versions = await api.GetVersionsAsync(
                    string.IsNullOrWhiteSpace(project.Slug) ? project.Id : project.Slug, ct);
                // ModpackInstallService 解析的是 modrinth.index.json，只认 .mrpack 文件
                var candidates = versions
                    .Where(v => v.Files.Any(f => IsMrpack(f.Filename) && !string.IsNullOrWhiteSpace(ModpackFileUrl(v, f))))
                    .ToList();
                if (candidates.Count == 0)
                    return $"《{project.Title}》没有可自动安装的 .mrpack 版本（CurseForge 包或仅有其他格式时不支持）。";

                if (!string.IsNullOrWhiteSpace(wantVersion))
                {
                    var matched = candidates
                        .Where(v => v.GameVersions.Any(g => string.Equals(g, wantVersion, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (matched.Count == 0)
                        return $"《{project.Title}》没有兼容 Minecraft {wantVersion} 的整合包版本；现有版本支持："
                            + string.Join("、", candidates.SelectMany(v => v.GameVersions).Distinct().TakeLast(12));
                    candidates = matched;
                }

                var picked = candidates
                    .OrderBy(v => v.VersionType.ToLowerInvariant() switch
                    {
                        "release" => 0,
                        "beta" => 1,
                        _ => 2
                    })
                    .ThenByDescending(v => DateTimeOffset.TryParse(v.DatePublished, out var d) ? d : DateTimeOffset.MinValue)
                    .FirstOrDefault()!;
                var file = picked.Files.FirstOrDefault(f => f.Primary && IsMrpack(f.Filename))
                    ?? picked.Files.First(f => IsMrpack(f.Filename));
                var url = ModpackFileUrl(picked, file);
                if (string.IsNullOrWhiteSpace(url)) return "整合包下载地址缺失，无法安装。";

                var mc = picked.GameVersions.FirstOrDefault() ?? wantVersion ?? "";
                var loader = picked.Loaders.FirstOrDefault(l => l is "fabric" or "quilt" or "forge" or "neoforge") ?? "";
                if (DownloadService.Instance.IsDuplicate(file.Filename))
                    return $"《{project.Title}》已在安装队列中，请到「消息」页查看进度。";

                var label = string.IsNullOrWhiteSpace(mc) ? file.Filename : project.Title + " " + mc;
                // switchToMessages=false：不能因为 AI 在装包就把用户从对话页跳走，进度仍能在「消息」页看。
                var task = DownloadService.Instance.StartInstall(project.Title, label, switchToMessages: false);
                // 用户在对话里点「停止」应当连安装一起停掉，所以把两个 token 串在一起。
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Token);
                try
                {
                    var id = await ModpackInstallService.InstallAsync(
                        project.Title, picked, url, task, linked.Token, project.IconUrl);
                    return $"整合包《{project.Title}》{picked.VersionNumber} 已安装为新实例 {id}"
                        + $"（Minecraft {mc}{(string.IsNullOrWhiteSpace(loader) ? "" : " / " + loader)}）。"
                        + "请提示用户回首页选中该实例后启动。";
                }
                catch (Exception ex) when (DownloadService.IsCanceled(ex, task.Token) || DownloadService.IsCanceled(ex, linked.Token))
                {
                    DownloadService.Instance.MarkCancelled(task);
                    return "整合包安装已取消（用户停止），实例可能只装了一半，请到首页删除残留实例后重试。";
                }
                catch (Exception ex)
                {
                    LauncherLogService.Error("AiTool", "install_modpack 失败", ex);
                    DownloadService.Instance.UpdateTask(task, t =>
                    {
                        t.Status = "下载失败";
                        t.Detail = ex.Message;
                        t.ProgressHint = "安装失败";
                    });
                    return "整合包安装失败：" + ex.Message;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "install_modpack 准备失败", ex);
                return "安装整合包失败：" + ex.Message;
            }
        }

        private static bool IsMrpack(string? filename)
            => !string.IsNullOrWhiteSpace(filename)
               && filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase);

        /// <summary>下载地址缺失时按 Modrinth CDN 规则拼出（与整合包详情页同一套回退）。</summary>
        private static string ModpackFileUrl(ModrinthVersion version, ModrinthVersionFile file)
        {
            if (!string.IsNullOrWhiteSpace(file.Url)) return file.Url;
            if (string.IsNullOrWhiteSpace(version.ProjectId) || string.IsNullOrWhiteSpace(version.Id)
                || string.IsNullOrWhiteSpace(file.Filename))
                return "";
            return $"https://cdn.modrinth.com/data/{version.ProjectId}/versions/{version.Id}/{Uri.EscapeDataString(file.Filename)}";
        }

        // ==================== 资源下载库管理 ====================

        private static string ListLibrary(string argsJson)
        {
            using var doc = Parse(argsJson);
            var kind = NormalizeKind(Str(doc.RootElement, "type"));
            var noun = ContentKind.DisplayName(kind);
            try
            {
                var dir = ModLibraryService.GetDirectory(kind);
                var items = ModLibraryService.Scan(kind);
                if (items.Count == 0)
                    return $"资源库中暂无{noun}（目录：{dir}）。可用 install_mod 或 download_resource 下载内容入库。";

                var sb = new StringBuilder();
                sb.AppendLine($"资源库共有 {items.Count} 个{noun}（目录：{dir}）：");
                foreach (var item in items.Take(80))
                {
                    sb.Append($"- {item.ShownName} | 文件: {item.Name}");
                    if (!string.IsNullOrWhiteSpace(item.Version)) sb.Append($" | 版本: {item.Version}");
                    if (!string.IsNullOrWhiteSpace(item.GameVersions)) sb.Append($" | 适配 MC: {item.GameVersions}");
                    sb.Append($" | {item.SizeText}");
                    sb.AppendLine();
                }
                if (items.Count > 80) sb.AppendLine($"…等共 {items.Count} 个");
                sb.Append($"可用 install_library_item 安装到游戏版本，或 delete_library_item 删除。");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "list_library 失败", ex);
                return $"查询资源库失败：{ex.Message}";
            }
        }

        private static string InstallLibraryItem(string argsJson)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var name = Str(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return "请提供要安装的资源库条目名称。";
            var kind = NormalizeKind(Str(root, "type"));
            if (!ContentKind.IsInstallable(kind))
                return "整合包（modpack）不能直接安装到版本：Modrinth 整合包请用 install_modpack（会自动建一个新实例），"
                    + "用户之间分享的 xolauncher:// 链接用 import_modpack。";
            var noun = ContentKind.DisplayName(kind);

            var versionId = Str(root, "version") ?? LauncherSettings.Instance.SelectedVersion;
            if (string.IsNullOrWhiteSpace(versionId))
                return "无法确定目标游戏版本：请先在首页选中一个版本，或在参数里给出 version。";
            var target = InstalledVersionService.Find(versionId);
            if (target is null)
                return $"没有找到版本「{versionId}」，请先用 list_versions 查看已安装版本。";
            if (target.IsBedrock)
                return $"「{target.DisplayName}」是基岩版，不支持从资源库安装{noun}。";

            List<InstanceModItem> matches;
            try
            {
                matches = FindLibraryMatches(kind, name!);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "install_library_item 扫描失败", ex);
                return "查询资源库失败：" + ex.Message;
            }

            if (matches.Count == 0)
                return $"资源库里没有匹配「{name}」的{noun}。请先用 list_library 查看有哪些条目，或用 install_mod 下载。";
            if (matches.Count > 1)
                return $"匹配到 {matches.Count} 个条目，请根据目标版本（{target.DisplayName} / MC {target.MinecraftVersion}）选出最合适的一个，用精确文件名重试：\n"
                    + FormatCandidates(matches);

            var item = matches[0];
            try
            {
                var gameDir = LauncherSettings.Instance.GameDirectory;
                var instanceDir = GameInstanceLayout.GetDirectory(gameDir, target.Id);
                var contentDir = GameInstanceLayout.ResolveContentRoot(gameDir, instanceDir);
                var (installed, skipped) = ModLibraryService.InstallToInstance(contentDir, kind, matches);
                if (installed > 0)
                    return $"已把《{item.ShownName}》（{item.Name}）安装到 {target.DisplayName}。";
                if (skipped > 0)
                    return $"{target.DisplayName} 中已存在同名文件，已跳过：{item.Name}（无需重复安装）。";
                return $"安装失败：源文件已不存在（{item.Name}），可能已被移动或删除。";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "install_library_item 失败", ex);
                return "安装失败：" + ex.Message;
            }
        }

        private static string DeleteLibraryItem(string argsJson)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var name = Str(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return "请提供要删除的资源库条目名称。";
            var kind = NormalizeKind(Str(root, "type"));
            var all = Str(root, "all") == "true";
            var noun = ContentKind.DisplayName(kind);

            try
            {
                var matches = FindLibraryMatches(kind, name!);
                if (matches.Count == 0)
                    return $"资源库里没有匹配「{name}」的{noun}，无需删除。";
                if (matches.Count > 1 && !all)
                    return $"匹配到 {matches.Count} 个条目。请指定精确文件名重试；若确认要全部删除，传 all=true：\n"
                        + FormatCandidates(matches);

                foreach (var item in matches)
                    ModLibraryService.DeleteFromLibrary(item);
                return matches.Count == 1
                    ? $"已从资源库删除：{matches[0].ShownName}（{matches[0].Name}）"
                    : $"已从资源库删除 {matches.Count} 个{noun}：" + string.Join("、", matches.Select(m => m.Name));
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "delete_library_item 失败", ex);
                return "删除失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 从某个版本的目录里删掉指定的模组 / 光影 / 资源包。
        /// 匹配到多个时把候选交回模型让它用精确文件名重试，绝不自己挑一个删；
        /// 只动该版本目录里的文件，资源库原文件保留。
        /// </summary>
        private static string RemoveInstanceMod(string argsJson)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var name = Str(root, "name");
            if (string.IsNullOrWhiteSpace(name)) return "请提供要删除的模组名称或文件名。";
            var kind = NormalizeKind(Str(root, "type"));
            var noun = ContentKind.DisplayName(kind);

            if (!TryResolveInstance(Str(root, "version"), out var target, out var instanceDir, out var err))
                return err!;
            if (target!.IsBedrock) return $"「{target.DisplayName}」是基岩版，不支持这样删除{noun}。";

            try
            {
                var contentDir = GameInstanceLayout.ResolveContentRoot(
                    LauncherSettings.Instance.GameDirectory, instanceDir);
                var items = InstanceModService.Scan(contentDir, kind);
                var matches = FindInstanceModMatches(items, name!);

                if (matches.Count == 0)
                    return items.Count == 0
                        ? $"「{target.DisplayName}」里没有任何{noun}，无需删除。"
                        : $"「{target.DisplayName}」的{noun}目录里没有匹配「{name}」的文件。当前有：\n"
                          + FormatCandidates(items);
                if (matches.Count > 1)
                    return $"匹配到 {matches.Count} 个{noun}，请根据下面的列表用精确文件名重试：\n"
                        + FormatCandidates(matches);

                var item = matches[0];
                InstanceModService.Delete(item);
                return $"已从 {target.DisplayName} 删除：{item.ShownName}（{item.Name}）。"
                    + "资源库里的原文件仍保留，需要时可再装回。";
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "remove_mod 失败", ex);
                return "删除失败：" + ex.Message;
            }
        }

        // ==================== 每版本 / 实例详细设置 ====================

        private static string GetInstanceSettings(string argsJson)
        {
            using var doc = Parse(argsJson);
            if (!TryResolveInstance(Str(doc.RootElement, "version"), out var target, out var instanceDir, out var err))
                return err!;

            var g = LauncherSettings.Instance;
            var meta = GameInstanceLayout.ReadMeta(instanceDir);
            var resolved = GameInstanceLayout.ResolveLaunch(target!.Id);
            var independent = meta?.IndependentSettings == true;

            var sb = new StringBuilder();
            sb.AppendLine($"版本「{target.DisplayName}」（{target.Id}）的实例设置：");
            sb.AppendLine($"- 独立设置：{(independent ? "已开启（下列每实例覆盖生效）" : "未开启（当前全部沿用全局设置）")}");
            sb.AppendLine($"- 生效内存：{resolved.MemoryMB} MB　[全局 {g.MemoryMB} MB｜实例覆盖 {(meta?.MemoryMB is > 0 ? meta.MemoryMB + " MB" : "无")}]");
            var java = string.IsNullOrWhiteSpace(resolved.JavaPath) ? "（未指定，按自动匹配）" : resolved.JavaPath;
            sb.AppendLine($"- 生效 Java：{java}　[自动匹配 Java：{(resolved.AutoMatchJava ? "开" : "关")}]");
            sb.AppendLine($"- 窗口模式：{resolved.WindowMode}　尺寸：{resolved.WindowWidth}×{resolved.WindowHeight}");
            sb.AppendLine($"- 额外 JVM 参数：{(string.IsNullOrWhiteSpace(resolved.ExtraJvmArgs) ? "（无）" : resolved.ExtraJvmArgs)}");
            sb.AppendLine($"- 额外游戏参数：{(string.IsNullOrWhiteSpace(resolved.ExtraGameArgs) ? "（无）" : resolved.ExtraGameArgs)}");
            sb.AppendLine($"- 自定义显示名：{(string.IsNullOrWhiteSpace(meta?.DisplayName) ? "（无）" : meta!.DisplayName)}");
            sb.Append("可用 set_instance_setting 修改上述任一项（会自动开启独立设置使改动对该版本生效）。");
            return sb.ToString();
        }

        private static string SetInstanceSetting(string argsJson)
        {
            using var doc = Parse(argsJson);
            var root = doc.RootElement;
            var key = Str(root, "key")?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(key)) return "请提供要修改的设置项 key。";
            var value = Str(root, "value");
            if (!TryResolveInstance(Str(root, "version"), out var target, out var instanceDir, out var err))
                return err!;
            if (!System.IO.Directory.Exists(instanceDir))
                return $"版本目录不存在：{instanceDir}";

            var meta = GameInstanceLayout.GetOrCreateMeta(instanceDir!);
            string describe;
            switch (key)
            {
                case "memory":
                    if (!long.TryParse(value, out var mb) || mb <= 0) return "memory 需为正整数（单位 MB）。";
                    meta.MemoryMB = mb; describe = $"内存 = {mb} MB"; break;
                case "java":
                    meta.JavaPath = value; describe = $"Java 路径 = {(string.IsNullOrWhiteSpace(value) ? "（已清空，改用全局/自动匹配）" : value)}"; break;
                case "auto_java":
                    if (ParseBool(value) is not bool aj) return "auto_java 需为 true 或 false。";
                    meta.AutoMatchJava = aj; describe = $"自动匹配 Java = {(aj ? "开" : "关")}"; break;
                case "jvm_args":
                    meta.ExtraJvmArgs = value; describe = $"额外 JVM 参数 = {(string.IsNullOrWhiteSpace(value) ? "（已清空）" : value)}"; break;
                case "game_args":
                    meta.ExtraGameArgs = value; describe = $"额外游戏参数 = {(string.IsNullOrWhiteSpace(value) ? "（已清空）" : value)}"; break;
                case "window_mode":
                {
                    var m = value?.Trim().ToLowerInvariant();
                    if (m is not ("default" or "fullscreen" or "maximized" or "custom"))
                        return "window_mode 只能是 default / fullscreen / maximized / custom。";
                    meta.WindowMode = m; describe = $"窗口模式 = {m}"; break;
                }
                case "width":
                    if (!int.TryParse(value, out var w) || w <= 0) return "width 需为正整数。";
                    meta.WindowWidth = w; describe = $"窗口宽度 = {w}"; break;
                case "height":
                    if (!int.TryParse(value, out var h) || h <= 0) return "height 需为正整数。";
                    meta.WindowHeight = h; describe = $"窗口高度 = {h}"; break;
                case "name":
                    meta.DisplayName = value; describe = $"显示名 = {(string.IsNullOrWhiteSpace(value) ? "（已清空）" : value)}"; break;
                case "independent":
                    meta.IndependentSettings = ParseBool(value) ?? true; describe = $"独立设置 = {(meta.IndependentSettings ? "开" : "关")}"; break;
                default:
                    return $"未知的设置项 key：{key}。可用：memory / java / auto_java / jvm_args / game_args / window_mode / width / height / name / independent。";
            }

            // 显示名与独立开关本身无需独立设置；其余每实例覆盖必须开启独立设置才会生效
            if (key is not ("name" or "independent") && !meta.IndependentSettings)
                meta.IndependentSettings = true;

            try
            {
                GameInstanceLayout.WriteMeta(instanceDir!, meta);
            }
            catch (Exception ex)
            {
                LauncherLogService.Error("AiTool", "set_instance_setting 写入失败", ex);
                return "写入失败：" + ex.Message;
            }
            return $"已更新「{target!.DisplayName}」（{target.Id}）的实例设置：{describe}。"
                + (meta.IndependentSettings ? "（独立设置已开启，改动仅对该版本生效）" : "");
        }

        /// <summary>解析目标版本并定位其实例目录；失败时返回 false 并给出 err 文本。</summary>
        private static bool TryResolveInstance(string? versionArg, out InstalledVersion? target, out string instanceDir, out string? err)
        {
            target = null;
            instanceDir = "";
            var versionId = string.IsNullOrWhiteSpace(versionArg) ? LauncherSettings.Instance.SelectedVersion : versionArg;
            if (string.IsNullOrWhiteSpace(versionId))
            {
                err = "无法确定目标版本：请先在首页选中一个版本，或在参数里给出 version。";
                return false;
            }
            target = InstalledVersionService.Find(versionId!);
            if (target is null)
            {
                err = $"没有找到版本「{versionId}」，请先用 list_versions 查看已安装版本。";
                return false;
            }
            instanceDir = GameInstanceLayout.GetDirectory(LauncherSettings.Instance.GameDirectory, target.Id);
            err = null;
            return true;
        }

        private static bool? ParseBool(string? v) => v?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "yes" or "是" or "开" => true,
            "false" or "0" or "off" or "no" or "否" or "关" => false,
            _ => null
        };

        /// <summary>按文件名 / 显示名精确匹配资源库条目；无精确匹配时退化为包含匹配。</summary>
        private static List<InstanceModItem> FindLibraryMatches(string kind, string name)
        {
            var items = ModLibraryService.Scan(kind);
            var exact = items.Where(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            return items.Where(m =>
                m.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(m.DisplayName)
                    && m.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>
        /// 版本目录里的条目匹配：精确文件名 / 显示名 → 包含匹配 → mod id。
        /// mod id 要开 jar 读元数据，所以只在前几种都没命中时才逐个试（一个版本的模组通常几十个）。
        /// </summary>
        private static List<InstanceModItem> FindInstanceModMatches(
            IReadOnlyList<InstanceModItem> items, string name)
        {
            var query = name.Trim();
            var exact = items.Where(m =>
                string.Equals(m.Name, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.DisplayName, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;

            var partial = items.Where(m =>
                m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(m.DisplayName)
                    && m.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            if (partial.Count > 0) return partial;

            var byId = new List<InstanceModItem>();
            foreach (var item in items)
            {
                try
                {
                    var (id, _, _, _) = ModNameTranslationService.DescribeFileFull(item.FilePath, item.Name);
                    if (string.Equals(id, query, StringComparison.OrdinalIgnoreCase)) byId.Add(item);
                }
                catch (Exception)
                {
                    // 单个文件读不出元数据不影响其余匹配
                }
            }
            return byId;
        }

        private static string FormatCandidates(IReadOnlyList<InstanceModItem> matches)
        {
            var sb = new StringBuilder();
            foreach (var m in matches.Take(20))
            {
                sb.Append($"- {m.Name}（{m.ShownName}");
                if (!string.IsNullOrWhiteSpace(m.GameVersions)) sb.Append($"，适配 MC {m.GameVersions}");
                sb.AppendLine("）");
            }
            if (matches.Count > 20) sb.AppendLine($"…等共 {matches.Count} 个");
            return sb.ToString().TrimEnd();
        }

        private static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
        {
            ContentKind.Shader => ContentKind.Shader,
            ContentKind.ResourcePack => ContentKind.ResourcePack,
            ContentKind.Modpack => ContentKind.Modpack,
            _ => ContentKind.Mod
        };

        private static string NormalizeLoader(string? loader) => loader?.Trim().ToLowerInvariant() switch
        {
            "fabric" or "forge" or "neoforge" or "quilt" => loader!.Trim().ToLowerInvariant(),
            _ => ""
        };

        private static void Put(Dictionary<string, string> opts, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) opts[key] = value!;
        }

        private static JsonDocument Parse(string argsJson)
            => JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);

        /// <summary>读取字符串属性；数字也转成字符串；缺失或空返回 null。</summary>
        private static string? Str(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(name, out var el)) return null;
            var s = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l.ToString() : el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}
