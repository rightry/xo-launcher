using System.Collections.Generic;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 启动遮罩的内容：新手引导 + 版本更新说明。
    /// 引导页不是纯图文——Account / Folder / Install / Personalize 这四类会在页面里
    /// 直接放对应的操作控件，让用户在引导过程中就把账号、目录、首个版本和外观配好。
    /// 图标码点取自 Assets/Fonts/FluentSystemIcons-Filled.ttf（已逐个核对）。
    /// </summary>
    public static class WelcomePages
    {
        /// <param name="Kind">account / folder / install / personalize / info，决定页面下方挂哪块操作区。</param>
        public sealed record Page(string Kind, string Eyebrow, string Glyph, string Title, IReadOnlyList<string> Bullets);

        public static IReadOnlyList<Page> Onboarding { get; } = new Page[]
        {
            new("account", "第 1 步 · 账号", "\uE93A", "先有一个能进游戏的身份", new[]
            {
                "微软正版登录会打开系统浏览器，在微软页面里完成，本软件不碰你的密码。",
                "没有正版账号也能玩：填个名字点「添加离线账号」，单机不受影响。",
                "这一步可以先跳过，之后在设置页的账号卡里随时补。",
            }),
            new("folder", "第 2 步 · 游戏目录", "\uF150", "决定游戏装在哪、资源存哪", new[]
            {
                "版本、模组、资源包、皮肤与日志都写在这个目录下，换目录等于换一整套游戏。",
                "已有别的启动器的文件夹（含 versions / assets）可以直接指过来，启动器会认。",
                "默认位置在当前用户的 .minecraft；想集中管理多个目录，进主界面后在设置页还能加。",
            }),
            new("install", "第 3 步 · 第一个版本", "\uF681", "装一个马上能启动的版本", new[]
            {
                "默认列出 Mojang 官方清单里最新的正式版，选一个点「安装」即可。",
                "下载走多源竞速（官方 + CDN 镜像同时发请求，谁快用谁）。",
                "想玩模组的版本可以稍后在「下载」页用 4 步向导带上加载器一起装。",
            }),
            new("personalize", "第 4 步 · 外观", "\uF59C", "顺手把界面调成你喜欢的样子", new[]
            {
                "亮暗主题会立刻应用到整个启动器，也可以跟随系统。",
                "打开「全景图背景」会把所选版本的标题全景当背景，卡片是毛玻璃、文字随背景明暗自动切换。",
                "字体、主题色、卡片浓度这些细节都留在设置页的个性化里。",
            }),
        };

        /// <summary>
        /// 版本更新要点：按声明顺序排列，升级跨多个版本时一起展示。
        /// 新版本发布时往**末尾**追加一条，老条目留着不动。
        /// </summary>
        private static readonly (string Version, Page Page)[] Notes =
        {
            ("1.0.0", new Page("info", "本次更新", "\uF541", "这一版改了什么", new[]
            {
                "原版安装改成 4 步向导：选加载器 → 选加载器版本 → 命名 → 确认安装，安装结果当场给出，不再只有系统通知。",
                "加载器版本列表改为多源竞速拉取（官方 + CDN 镜像同时发请求取最快的），失败结果不再被缓存住。",
                "全景背景升级为 360° 环带滚动；卡片改用 WinUI 原生亚克力，文字与卡片底色会跟着背景明暗实时切换。",
                "皮肤页重排为三栏工作台，右栏可整栏竖向折叠；皮肤来源移到了设置页的账号卡里。",
                "所有输入框在全景模式下也带毛玻璃；弹窗卡片同样换成原生亚克力。",
            })),
        };

        /// <summary>比 seenVersion 新的更新说明页；seen 为空或认不出来时返回全部。</summary>
        public static IReadOnlyList<Page> ReleaseNotesNewerThan(string? seenVersion)
        {
            var seenIndex = -1;
            for (var i = 0; i < Notes.Length; i++)
            {
                if (string.Equals(Notes[i].Version, seenVersion, System.StringComparison.Ordinal))
                {
                    seenIndex = i;
                    break;
                }
            }

            var pages = new List<Page>();
            for (var i = seenIndex + 1; i < Notes.Length; i++)
                pages.Add(Notes[i].Page);
            return pages;
        }
    }
}
