using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XO_Launcher.Models
{
    /// <summary>
    /// 资源库页面（<see cref="Views.LibraryPage"/>）中的一行：一条已保存到本机资源库的内容。
    /// 与 <see cref="LibraryPickItem"/> 的区别是这个页面没有「多选安装」的概念，
    /// 而是「安装到右上角选定的版本」，因此只暴露是否可以安装。
    /// </summary>
    public sealed class LibraryEntry
    {
        public InstanceModItem Item { get; init; } = new();

        /// <summary>该文件在资源库索引里的记录（可能为 null：老文件还没被扫到过）。</summary>
        public Services.LibraryFileRecord? Record { get; init; }

        public string Name => Item.ShownName;

        /// <summary>文件名，版本卡片里用它区分同名模组的不同版本。</summary>
        public string FileName => Item.Name;

        /// <summary>已装到多少个实例（来自索引记录）。</summary>
        public int InstalledCount => Record?.Installs.Count ?? 0;

        public string InstalledText => InstalledCount > 0 ? $"已装到 {InstalledCount} 个版本" : "未安装";

        /// <summary>有 Modrinth 溯源信息才能查新版本（本地导入、CurseForge 包都没有）。</summary>
        public bool CanCheckUpdate => CanInstall && Record?.CanCheckUpdate == true;

        /// <summary>
        /// 模组 / 资源版本。优先级：jar 元数据 → 索引记录（下载时 Modrinth 给的）→
        /// 文件名里的版本号 → 资源包 / 光影包 zip 的 pack 格式号。
        /// </summary>
        public string ModVersionText
            => First(Item.Version, Record?.ModVersion, VersionInName(FileName),
                string.IsNullOrWhiteSpace(Item.PackFormats) ? "" : "格式 " + Item.PackFormats) ?? "—";

        /// <summary>适配的 Minecraft 版本：索引记录优先（下载时按游戏版本存），其次 jar 元数据。</summary>
        public string GameVersionText
            => First(Record?.GameVersionText, Item.GameVersions) ?? "—";

        /// <summary>光影包需要的加载器（Iris / OptiFine），来自 Modrinth 的 loaders；没有则为空。</summary>
        public string LoaderText
            => Record is { Loaders.Count: > 0 } rec
                ? string.Join(", ", rec.Loaders)
                : Record?.Loader ?? "";

        /// <summary>行尾的补充信息：适配版本 + 已装到几个版本（光影卡片里靠它看清游戏版本）。</summary>
        public string RowMetaText
        {
            get
            {
                var version = GameVersionText == "—" ? "" : "适配 " + GameVersionText;
                var installed = InstalledText == "未安装" ? "" : InstalledText;
                return string.Join("  ·  ", new[] { version, installed }.Where(v => v.Length > 0));
            }
        }

        private static string? First(params string?[] values)
        {
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }

        /// <summary>
        /// 从文件名里抓版本号（<c>ComplementaryReimagined_r5.8.1.zip</c> → 5.8.1）。
        /// 只用于显示：写进索引会污染去重键，所以不入库。
        /// </summary>
        private static string VersionInName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            var match = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileNameWithoutExtension(fileName), @"\d+(\.\d+)+");
            return match.Success ? match.Value : "";
        }

        /// <summary>整合包是整包归档，不能直接装进版本目录，此值为 false 时隐藏「安装到版本」。</summary>
        public bool CanInstall { get; init; }
    }

    /// <summary>
    /// 资源库页面里的一张卡片：一个 Minecraft 版本 + 该版本可用的资源文件。
    /// 一个模组适配多个版本时，它会同时出现在这几张卡片里（这是按版本分类的必然结果，
    /// 也让「装到 26.2 的那份」和「装到 26.3 的那份」各自看得见）。
    /// </summary>
    public sealed class LibraryVersionGroup
    {
        /// <summary>卡片标题，如「26.3」；读不出版本的文件归到「未标注版本」。</summary>
        public string VersionLabel { get; init; } = "";

        /// <summary>标题前的维度标签：模组 / 资源包是「游戏版本」，光影是「光影加载器」。</summary>
        public string KindLabel { get; init; } = "游戏版本";

        public string SummaryText { get; init; } = "";

        public IReadOnlyList<LibraryEntry> Entries { get; init; } = Array.Empty<LibraryEntry>();
    }
}
