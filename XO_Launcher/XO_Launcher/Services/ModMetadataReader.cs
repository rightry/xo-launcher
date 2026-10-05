using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XO_Launcher.Services
{
    /// <summary>从模组 jar 中读取模组 ID、显示名称、模组版本与适配的 Minecraft 版本。</summary>
    public static class ModMetadataReader
    {
        /// <summary>拆版本约束用的分隔符：逗号、空白与各家写法里的括号。</summary>
        private static readonly char[] VersionSeparators =
            { ',', ' ', '\t', '[', ']', '(', ')', '{', '}', '|', ';' };

        /// <returns>(模组 ID, 显示名称, 模组版本, 适配的 Minecraft 版本)；读不到时为空串。</returns>
        public static (string Id, string Name, string Version, string GameVersions) Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ("", "", "", "");
            // 被禁用的内容以 .jar.disabled 落盘，剥掉后缀再判类型，
            // 否则禁用状态的模组永远读不出名称与版本。
            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^".disabled".Length];
            var ext = Path.GetExtension(fileName);
            if (!ext.Equals(".jar", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".litemod", StringComparison.OrdinalIgnoreCase))
                return ("", "", "", "");

            try
            {
                using var zip = ZipFile.OpenRead(path);

                var fabric = ReadFabric(zip);
                if (fabric is not null)
                    return Finish(zip, fabric.Value);

                // quilt.mod.json 的 id / version 在 quilt_loader 段内，根级读不到。
                // 读空就继续往后试，别拿空结果短路掉后面真正有元数据的文件。
                var quilt = ReadQuilt(zip);
                if (quilt is not null
                    && (!string.IsNullOrWhiteSpace(quilt.Value.Id) || !string.IsNullOrWhiteSpace(quilt.Value.Name)))
                    return Finish(zip, quilt.Value);

                var toml = ReadToml(zip, "META-INF/neoforge.mods.toml")
                           ?? ReadToml(zip, "META-INF/mods.toml");
                if (toml is not null)
                    return Finish(zip, toml.Value);

                var legacy = ReadMcmodInfo(zip);
                if (legacy is not null)
                    return Finish(zip, legacy.Value);

                // 认不出任何元数据时，至少给出 MANIFEST 里的版本号。
                return ("", "", Clean(ReadManifestVersion(zip)), "");
            }
            catch (Exception)
            {
                // 损坏或不完整的压缩包忽略
            }

            return ("", "", "", "");
        }

        /// <summary>
        /// 读资源包 / 光影包 zip 里的 <c>pack.mcmeta</c>，给出格式号文本。
        ///
        /// 为什么单独一个方法：这两类内容不是 jar，<see cref="Read"/> 直接返回空，
        /// 于是资源包在资源库里只能落到「未标注版本」。这里读 Mojang 的 pack 段，
        /// 三种写法都认：<c>pack_format</c>（老写法，可为数字或数组）、
        /// <c>supported_formats</c>（{min,max}）、以及新出现直接挂在 pack 上的
        /// <c>min_format</c> / <c>max_format</c>（实测 FreshAnimations 用的就是它）。
        /// 只给格式号不给对应的 Minecraft 版本：格式号→版本的对照表会随版本更新过期，
        /// 猜错比留白更糟；下载来的包由 Modrinth 的 game_versions 提供版本。
        /// </summary>
        /// <returns>格式号文本（如 "15"、"84 ~ 999"）；读不到时为空串。</returns>
        public static string ReadPackFormats(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^".disabled".Length];
            if (!Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return "";

            try
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    // 只认根目录或一层目录下的 pack.mcmeta（光影包常把内容套一层文件夹）
                    if (!name.EndsWith("pack.mcmeta", StringComparison.OrdinalIgnoreCase)) continue;
                    var depth = name.Count(c => c == '/');
                    if (depth > 1) continue;

                    using var stream = entry.Open();
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("pack", out var pack)
                        || pack.ValueKind != JsonValueKind.Object) continue;

                    var formats = new List<string>();
                    if (pack.TryGetProperty("pack_format", out var single)) AddNumber(single, formats);
                    if (pack.TryGetProperty("supported_formats", out var supported))
                    {
                        if (supported.ValueKind == JsonValueKind.Array)
                            foreach (var item in supported.EnumerateArray()) AddNumber(item, formats);
                        else AddRange(supported, formats);
                    }
                    if (formats.Count == 0 && AddRange(pack, formats, "min_format", "max_format"))
                        return formats[0];
                    if (formats.Count == 0) continue;

                    // 按数值取最小/最大：格式号是数字，按字符串比会得出 "9" > "15" 的错结论
                    var numbers = formats
                        .Select(f => int.TryParse(f, out var n) ? n : (int?)null)
                        .OfType<int>()
                        .ToList();
                    if (numbers.Count == 0) continue;
                    var lo = numbers.Min();
                    var hi = numbers.Max();
                    return lo == hi ? lo.ToString() : $"{lo} ~ {hi}";
                }
            }
            catch (Exception)
            {
                // 损坏压缩包或非标准 pack.mcmeta：当成没有元数据
            }
            return "";

            static void AddNumber(JsonElement node, List<string> into)
            {
                if (node.ValueKind == JsonValueKind.Number) into.Add(node.GetRawText());
                else if (node.ValueKind == JsonValueKind.Array)
                    foreach (var item in node.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Number) into.Add(item.GetRawText());
            }

            static bool AddRange(JsonElement node, List<string> into,
                string minKey = "min", string maxKey = "max")
            {
                if (node.ValueKind != JsonValueKind.Object) return false;
                if (!node.TryGetProperty(minKey, out var min) || !node.TryGetProperty(maxKey, out var max)) return false;
                if (min.ValueKind != JsonValueKind.Number || max.ValueKind != JsonValueKind.Number) return false;
                into.Add(min.GetRawText() + " ~ " + max.GetRawText());
                return true;
            }
        }

        /// <summary>
        /// 把各家加载器的 Minecraft 版本约束归一成可读文本：
        /// <c>"1.20.1"</c> → <c>"1.20.1"</c>；<c>"[1.20.1,1.21)"</c> 与 <c>">=1.20.1 &lt;1.21"</c> → <c>"1.20.1 ~ 1.21"</c>。
        /// 只保留以数字开头的版本片段并去重；通配符（<c>*</c>）与未替换的构建占位符按读不出处理。
        /// </summary>
        public static string NormalizeGameVersions(string? raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0 || value.Contains("${", StringComparison.Ordinal))
                return "";

            var tokens = new List<string>();
            foreach (var part in value.Split(VersionSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                // 去掉 >=、<=、~、^ 之类比较前缀，剩下的以数字开头才算版本号
                var token = part.TrimStart('>', '<', '=', '~', '^', '!', '+').Trim();
                if (token.Length == 0 || !char.IsDigit(token[0])) continue;
                if (tokens.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase))) continue;
                tokens.Add(token);
            }

            return tokens.Count switch
            {
                0 => "",
                1 => tokens[0],
                _ => tokens[0] + " ~ " + tokens[^1]
            };
        }

        /// <summary>版本号缺失时回落到 jar 清单里的 Implementation-Version。</summary>
        private static (string Id, string Name, string Version, string GameVersions) Finish(
            ZipArchive zip, JarInfo info)
        {
            var version = Clean(info.Version);
            if (version.Length == 0)
                version = Clean(ReadManifestVersion(zip));
            return (info.Id, info.Name, version, info.GameVersions);
        }

        /// <summary>未替换的构建占位符（如 ${file.jarVersion}）不是真实版本，按空处理。</summary>
        private static string Clean(string? version)
        {
            var value = (version ?? "").Trim();
            if (value.Length == 0 || value.Contains("${", StringComparison.Ordinal))
                return "";
            return value;
        }

        private static string ReadManifestVersion(ZipArchive zip)
        {
            var entry = zip.GetEntry("META-INF/MANIFEST.MF");
            if (entry is null) return "";
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                    return line["Implementation-Version:".Length..].Trim();
            }
            return "";
        }

        /// <summary>fabric.mod.json：版本在根级，Minecraft 约束在 depends.minecraft（字符串或数组）。</summary>
        private static JarInfo? ReadFabric(ZipArchive zip)
        {
            using var doc = ReadJson(zip, "fabric.mod.json");
            if (doc is null) return null;
            var root = doc.RootElement;
            return new JarInfo(
                GetString(root, "id"),
                GetString(root, "name"),
                GetString(root, "version"),
                NormalizeGameVersions(FabricMinecraft(root)));
        }

        /// <summary>quilt.mod.json：id / name / version 在 quilt_loader 段内，depends 是对象数组。</summary>
        private static JarInfo? ReadQuilt(ZipArchive zip)
        {
            using var doc = ReadJson(zip, "quilt.mod.json");
            if (doc is null) return null;
            var root = doc.RootElement;
            var loader = root.ValueKind == JsonValueKind.Object
                         && root.TryGetProperty("quilt_loader", out var inner)
                ? inner
                : root;
            var version = GetString(loader, "version");
            if (version.Length == 0) version = GetString(root, "version");
            return new JarInfo(
                GetString(loader, "id"),
                GetString(loader, "name"),
                version,
                NormalizeGameVersions(QuiltMinecraft(root, loader)));
        }

        private static string FabricMinecraft(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("depends", out var depends)
                || depends.ValueKind != JsonValueKind.Object
                || !depends.TryGetProperty("minecraft", out var minecraft))
                return "";
            return minecraft.ValueKind switch
            {
                JsonValueKind.String => minecraft.GetString() ?? "",
                JsonValueKind.Array => JoinStrings(minecraft),
                _ => ""
            };
        }

        private static string QuiltMinecraft(JsonElement root, JsonElement loader)
        {
            if (loader.ValueKind == JsonValueKind.Object
                && loader.TryGetProperty("depends", out var deps)
                && DependencyArrayMinecraft(deps) is { } fromLoader)
                return fromLoader;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("depends", out var rootDeps)
                && DependencyArrayMinecraft(rootDeps) is { } fromRoot)
                return fromRoot;
            return "";
        }

        /// <summary>quilt 依赖形如 [{ "id": "minecraft", "versions": "&gt;=1.19.2" }]。</summary>
        private static string? DependencyArrayMinecraft(JsonElement deps)
        {
            if (deps.ValueKind != JsonValueKind.Array) return null;
            foreach (var dep in deps.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.Object) continue;
                if (!string.Equals(GetString(dep, "id"), "minecraft", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!dep.TryGetProperty("versions", out var versions)) return "";
                return versions.ValueKind switch
                {
                    JsonValueKind.String => versions.GetString() ?? "",
                    JsonValueKind.Array => JoinStrings(versions),
                    _ => ""
                };
            }
            return null;
        }

        /// <summary>Forge / NeoForge 的 mods.toml：modId / displayName / version 与依赖段同在一个文件里。</summary>
        private static JarInfo? ReadToml(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();
            var id = MatchToml(text, "modId");
            var name = MatchToml(text, "displayName");
            var version = MatchToml(text, "version");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                return null;
            return new JarInfo(id, name, version, NormalizeGameVersions(ForgeMinecraft(text)));
        }

        /// <summary>
        /// 在 <c>[[dependencies.&lt;modid&gt;]]</c> 各段中找 modId 为 minecraft 的那段，取它的 versionRange。
        /// 注意不能用 MatchToml 取第一个 modId —— 那是模组自己的 id。
        /// </summary>
        private static string ForgeMinecraft(string text)
        {
            foreach (var block in Regex.Split(text, @"^\s*\[\[", RegexOptions.Multiline))
            {
                if (!Regex.IsMatch(block, @"modId\s*=\s*""minecraft""", RegexOptions.IgnoreCase))
                    continue;
                var match = Regex.Match(block, @"versionRange\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;
            }
            return "";
        }

        private static JarInfo? ReadMcmodInfo(ZipArchive zip)
        {
            using var doc = ReadJson(zip, "mcmod.info");
            if (doc is null) return null;
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            var id = GetString(root, "modid");
            var name = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                return null;
            var minecraft = root.ValueKind == JsonValueKind.Object
                            && root.TryGetProperty("mcversion", out var mc)
                ? mc.ValueKind switch
                {
                    JsonValueKind.String => mc.GetString() ?? "",
                    JsonValueKind.Array => JoinStrings(mc),
                    _ => ""
                }
                : "";
            return new JarInfo(id, name, GetString(root, "version"), NormalizeGameVersions(minecraft));
        }

        /// <summary>读取 jar 内的 JSON 文件；不存在时返回 null（调用方用 using 释放）。</summary>
        private static JsonDocument? ReadJson(ZipArchive zip, string entryName)
        {
            var entry = zip.GetEntry(entryName);
            if (entry is null) return null;
            using var stream = entry.Open();
            return JsonDocument.Parse(stream);
        }

        private static string GetString(JsonElement obj, string name)
            => obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? ""
                : "";

        private static string JoinStrings(JsonElement array)
        {
            var parts = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                    parts.Add(text);
            }
            return string.Join(",", parts);
        }

        private static string MatchToml(string text, string key)
        {
            var match = Regex.Match(text, $@"^\s*{key}\s*=\s*""([^""]*)""", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        /// <summary>一个 jar 里读出的全部元数据（GameVersions 已归一化）。</summary>
        private readonly record struct JarInfo(string Id, string Name, string Version, string GameVersions);
    }
}
