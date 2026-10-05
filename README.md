# XO Launcher

面向 Windows 的 Minecraft 启动器，WinUI 3（Windows App SDK）桌面应用。支持 Java 版与基岩版的安装、启动与实例管理，内置模组资源库、整合包分享、皮肤编辑器、崩溃分析、AI 助手与房间制快捷联机，并以自包含（self-contained）方式发布，用户无需另装 .NET 或 Windows App Runtime。

> 本项目不是 Mojang 或 Microsoft 的官方产品，也不分发任何 Minecraft 游戏文件；正版登录通过微软 OAuth 完成，游戏资源由官方接口按需下载。

---

## 功能概览

| 模块 | 说明 |
| --- | --- |
| 版本与实例 | 安装原版 / Fabric / Forge / NeoForge / Quilt，多实例独立配置（显示名、图标、Java、内存、JVM 与游戏参数、窗口模式） |
| 启动与运行时 | 自动扫描已安装 Java，也可按游戏版本自动下载官方运行时；启动前补齐资产、库与主清单 |
| 模组资源库 | 从 Modrinth / CurseForge 检索与下载模组、材质包、地图、整合包；自动解析模组前置依赖，一键补齐安装；本地资源库统一管理 |
| 整合包分享 | 导出版本 + 模组 + 配置为 `xolauncher://pack/…` 分享链接（清单 gzip + base64url 内联，不依赖第三方上传），可从链接导入还原 |
| 皮肤 | 皮肤查看与逐像素编辑（笔刷、缩放、取色、加深/变亮、历史撤销），支持默认 / Mojang 官方 / 本地 PNG / 远端 URL（自托管、LittleSkin 等）四种来源 |
| AI 助手 | 流式对话，可调用启动器工具（查版本、搜模组、装模组、改设置、启动游戏等）；所有写操作先弹确认；崩溃日志可交给 AI 深度分析 |
| 崩溃分析 | 本地规则识别 Crash Report / JVM / GLFW / Mixin 等常见故障并给出中文解释，可一键转交 AI |
| 联机 | 房间制快捷联机：房主创建房间、把房间码发给好友，好友粘贴加入后一起进同一个世界。内核为 [Terracotta](https://github.com/glavo/terracotta)（作为官方二进制通过本地 HTTP API 调用，未修改其源码） |
| 账号 | 微软正版账号（loopback 回调 + 设备代码双路径）与离线账号，支持多账号切换与皮肤头像 |
| 下载源 | 智能多源（官方 + 镜像）/ 镜像优先（BMCLAPI）/ 仅镜像 / 仅官方 四档，附带下载队列与进度通知 |
| 外观 | 亮 / 暗 / 跟随系统主题、强调色、界面字体、Minecraft 全景背景与卡片毛玻璃 |
| 命令行 | 无窗口完成安装、导入、下载、列出版本、切账号、改设置（见下） |
| 自更新 | 从固定更新域名读取 `update.json`，支持强制更新与最低版本约束，退出后由脚本覆盖并重启 |
| 消息中心 | 游戏状态、下载完成与 AI 请求确认等通知的集中查看 |

---

## 系统要求

- **运行**：Windows 10 版本 1809（build 17763）及以上，x64 / x86 / ARM64。
  发布产物已 `SelfContained=true` + `WindowsAppSDKSelfContained=true`，无需安装 .NET 8 桌面运行时或 Windows App Runtime。
- **开发**：Visual Studio 2022（.NET 8 需 17.8 及以上，勾选「WinUI 桌面开发」工作负载），或 .NET 8 SDK + Windows SDK 命令行构建。
- 依赖包：`Microsoft.WindowsAppSDK` 2.4.0、`Microsoft.Graphics.Win2D` 1.3.2（全景毛玻璃）、`Microsoft.Windows.SDK.BuildTools`。

---

## 构建与运行

解决方案为根目录的 `XO_Launcher.slnx`（`.slnx` 格式需要较新的 Visual Studio / .NET SDK 才能打开；也可以绕过解决方案，直接对下面的 csproj 执行 `dotnet` 命令）。它包含两个工程：

| 工程 | 路径 | 作用 |
| --- | --- | --- |
| 主程序 | `XO_Launcher/XO_Launcher/XO_Launcher.csproj` | 免安装（unpackaged）WinUI 3 桌面应用，实际开发入口 |
| 打包 | `XO_Launcher/XO_Launcher (Package)/XO_Launcher (Package).wapproj` | MSIX / Desktop Bridge 打包（未签名，需自行签名或侧载） |

> ⚠️ 仓库里还残留一份 `XO_Launcher/XO_Launcher.csproj` 以及同级的 `App.xaml` / `MainWindow.xaml`，它们是早期版本的游离副本，**不在解决方案中、不会被编译**。请以 `XO_Launcher/XO_Launcher/`（两层同名目录）下的工程为准。

命令行全量构建（推荐用它验证改动，增量构建可能因陈旧的 `obj` 生成文件给出假结果）：

```powershell
cd XO_Launcher/XO_Launcher
dotnet build XO_Launcher.csproj -t:Rebuild -p:Platform=x64
```

产物：`XO_Launcher/XO_Launcher/bin/x64/Debug/net8.0-windows10.0.19041.0/XO_Launcher.exe`，直接双击运行。

发布自包含版本（`Directory.Build.targets` 会强制 self-contained，并补齐 `.xbf` / `.pri` 资源）：

```powershell
cd XO_Launcher/XO_Launcher
dotnet publish XO_Launcher.csproj -c Release -p:Platform=x64 -r win-x64 `
  -p:PublishProfile=win-x64
# 输出目录：XO_Launcher/XO_Launcher/bin/win-x64/publish/win-x64/
```

MSIX 打包：在 Visual Studio 中对 `XO_Launcher (Package)` 执行「发布 → 生成 MSIX 包」；主工程的 `Properties/PublishProfiles/msix-win-*.pubxml` 是配套的发布配置（输出到 `bin\win-*\msixpublish`）。

---

## 命令行用法

主程序带参数时会附加控制台并执行无窗口命令（`launch` / `open` 除外，它们会打开界面）：

```text
XO_Launcher.exe launch [版本ID] [--user 名] [--memory MB] [--java 路径]   启动游戏（开 GUI）
XO_Launcher.exe open <页面>                                              打开页面（开 GUI）
        页面: home download library online ai skin settings messages
XO_Launcher.exe install <版本ID> [--loader fabric|forge|neoforge|quilt] [--loader-version X] [--name 名称]
XO_Launcher.exe import <xolauncher://分享链接>                            导入整合包
XO_Launcher.exe download <版本ID | http(s) 直链>                          下载版本资源或直链文件
XO_Launcher.exe list                                                     列出已安装版本
XO_Launcher.exe account <离线玩家名>                                      添加并选择离线账号
XO_Launcher.exe set <键> <值>                                             修改设置
        键: memory gamedir downloaddir java accent theme font mirror username version
XO_Launcher.exe help                                                     显示帮助
```

不带参数运行则正常打开界面。

---

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 设置文件 | `%LocalAppData%\XO_Launcher\`（JSON） |
| 诊断日志 | `%LocalAppData%\XO_Launcher\logs\xo-launcher.log`（单文件上限 2 MB，设置页可导出报告） |
| 更新下载 | `%LocalAppData%\XO_Launcher\updates\`（zip 与待应用目录 `pending`） |
| 游戏目录 | 由用户指定（识别 `versions` / `libraries` / `assets` / `mods` / `shaderpacks` / `resourcepacks`） |

---

## AI 助手说明

- 端点为 OpenRouter 的 OpenAI 兼容接口（`https://openrouter.ai/api/v1`），**API Key 由用户在「设置 → AI 助手」里自行填写**，仓库与发布产物里不含任何密钥。
- 可选三档免费模型（Nemotron 3 Ultra 550B / Laguna S 2.1 / Qwen3.8 27B），免费池偶发限流时就地换一档。
- 密钥只写进本机设置文件（`%LocalAppData%\XO_Launcher\`），设置页与调试页都只显示首尾各 4 位；未填密钥时 AI 页会弹窗引导去设置，不发起请求。
- 可调用工具：`list_versions`、`open_page`、`launch_game`、`install_version`、`import_modpack`、`download_resource`、`manage_account`、`update_setting`、`search_mods`、`install_mod`、`search_modpacks`、`install_modpack`、`list_library`、`install_library_item`、`remove_mod`、`delete_library_item`、`get_instance_settings`、`set_instance_setting`。只读查询不打扰用户，任何写入或删除操作都会先弹出确认（输入框旁的「权限」默认每次确认），未确认不会执行。
- 首次使用需阅读并同意条款：回答由第三方模型生成、可能不准确，提问内容会发送到 OpenRouter 及其上游服务。

---

## 自动更新

更新清单固定从 `update.xolauncher.dpdns.org` 根路径读取 `update.json`（仅 HTTPS，且只接受该主机及其子域，其余地址一律拒发请求），字段为 `version` / `releaseDate` / `forceUpdate` / `minLauncherVersion` / `downloadUrl`。下载完成后解压到 `updates\pending` 并写入 `pending.json`，由生成的 `apply-update.cmd` 在进程退出后覆盖文件并重启。

`update-site/` 是该更新站点的静态源（`public/update.json` 与 `vercel.json` 的 CORS / 缓存头）。当前实现**不含安装包哈希或签名校验，也没有回滚机制**，更新站点必须保持仅受信者可写。

---

## 项目结构

```text
XO_Launcher.slnx                      解决方案
XO_Launcher/
├─ XO_Launcher/                       ★ 主工程（实际开发入口）
│  ├─ Api/                            Mojang / Modrinth / CurseForge / 加载器元数据 / 基岩版接口
│  ├─ Cli/                            命令行解析、控制台附加与无窗口命令
│  ├─ Models/                         版本、实例、模组、皮肤、下载任务等数据模型
│  ├─ Services/                       安装、启动、账号、下载、Java 运行时、主题、全景、
│  │                                  AI 对话与工具层、崩溃分析、联机、自更新、日志
│  ├─ Views/                          页面与自绘控件（TextInput / SearchBox / AppDialog / SelectBox 等）
│  ├─ Assets/                         图标、图标字体、皮肤查看器、致谢资源
│  ├─ App.xaml / MainWindow.xaml      主题资源字典与主窗口
│  └─ Properties/PublishProfiles/     win-x64 / x86 / arm64 与对应 msix 发布配置
├─ XO_Launcher (Package)/             MSIX 打包工程
docs/                                 设计文档（微软正版登录）
scripts/                              美术资源生成脚本（图标、主视觉）
update-site/                          自动更新站点静态源
```

界面控件一律自绘：输入框、搜索框、弹窗、下拉列表（`Views/SelectBox`）都不使用 WinUI 默认外观与主题键，以便统一亮暗主题、全景背景与自定义列表项（例如模型图标）。新增交互控件时请沿用同一套做法。

---

## 开发约定

- 改 UI 后用 `dotnet build -t:Rebuild -p:Platform=x64` 全量验证；增量构建可能因陈旧 `obj` 报假错或假成功。
- 应用运行中 `bin` 会被锁定（`MSB3021` / `MSB3027`），这不是代码错误；要产出可运行版本需先退出实例。
- 注释只写「为什么」：本项目大量注释记录的是踩过的 WinUI 陷阱与不可见的框架约束，改动前请先读它们。

---

## 第三方与致谢

- [Mojang / Minecraft](https://www.minecraft.net/)、[Microsoft 登录](https://learn.microsoft.com/azure/active-directory/) — 版本清单、资产与正版验证
- [Modrinth](https://modrinth.com/)、[CurseForge](https://www.curseforge.com/) — 模组与整合包源
- BMCLAPI / MCIMIRROR — 国内下载镜像
- [Terracotta](https://github.com/glavo/terracotta)（AGPL-3.0）— 联机内核，作为独立可执行文件通过 HTTP API 调用
- [Windows App SDK / WinUI 3](https://learn.microsoft.com/windows/apps/)、[Win2D](https://github.com/microsoft/Win2D)、[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons)（MIT）

## 许可证

仓库当前未附带 `LICENSE` 文件，发布前请补充并明确许可证类型；上述第三方组件各自遵循其原始许可证，其中 Terracotta 为 AGPL-3.0（以独立进程调用、未修改源码）。
