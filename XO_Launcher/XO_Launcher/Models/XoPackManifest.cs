using System.Collections.Generic;

namespace XO_Launcher.Models
{
    /// <summary>
    /// XO 自制整合包清单。既用于导出到「分享链接」（gzip + base64url 编码后塞进
    /// <c>xolauncher://pack/…</c>），也用于导入侧还原：接收方拿到清单后按
    /// <see cref="XoPackMod.Url"/> 在线下载模组、按 <see cref="XoPackManifest.Config"/> 还原配置文件。
    ///
    /// 与 Modrinth 的 <c>modrinth.index.json</c> 是两套东西：那份清单要打包成 .mrpack 文件，
    /// 本清单只走链接、不落盘，因此把 config 直接内联成 base64（链接体积可控时）。
    /// </summary>
    public sealed class XoPackManifest
    {
        /// <summary>清单格式版本，便于将来演进时做兼容判断。</summary>
        public int Format { get; set; } = 1;

        public string Name { get; set; } = "";
        public string GameVersion { get; set; } = "";

        /// <summary>vanilla / fabric / quilt / forge / neoforge。</summary>
        public string Loader { get; set; } = "vanilla";

        public string LoaderVersion { get; set; } = "";

        public List<XoPackMod> Mods { get; set; } = new();

        /// <summary>配置文件：相对内容根目录的路径（正斜杠）→ 文件内容的 base64。</summary>
        public Dictionary<string, string> Config { get; set; } = new();
    }

    /// <summary>清单里的单个模组（已在线解析出下载地址与校验值）。</summary>
    public sealed class XoPackMod
    {
        /// <summary>下载后写入 mods 目录用的文件名。</summary>
        public string FileName { get; set; } = "";

        public string Url { get; set; } = "";

        /// <summary>本地 jar 的 sha1（与 Modrinth 主文件一致），导入侧可用于校验。</summary>
        public string Sha1 { get; set; } = "";

        public long Size { get; set; }

        /// <summary>Modrinth 项目 id（便于展示 / 跳转，非必需）。</summary>
        public string? ProjectId { get; set; }

        public string? ProjectTitle { get; set; }

        public string? VersionNumber { get; set; }

        /// <summary>client / server / both，默认按客户端处理。</summary>
        public string Env { get; set; } = "client";
    }
}
