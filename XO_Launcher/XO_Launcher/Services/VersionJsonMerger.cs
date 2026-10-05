using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XO_Launcher.Services
{
    /// <summary>
    /// 将原版 version JSON 与加载器 profile 合并成一份完整配置（去掉 inheritsFrom）。
    /// 做法与 HMCL / Prism 的版本拼合一致，这样本体和加载器可以放进同一个版本文件夹。
    /// </summary>
    public static class VersionJsonMerger
    {
        public static string Merge(string parentJson, string? childJson, string newId)
        {
            var parent = JsonNode.Parse(parentJson)?.AsObject()
                         ?? throw new System.InvalidOperationException("无法解析原版版本 JSON");
            var result = parent.DeepClone()!.AsObject();
            result["id"] = newId;
            result.Remove("inheritsFrom");
            result.Remove("jar");

            if (!string.IsNullOrWhiteSpace(childJson))
            {
                var child = JsonNode.Parse(childJson)?.AsObject();
                if (child is not null)
                    ApplyChild(result, child);
            }

            result["id"] = newId;
            result.Remove("inheritsFrom");
            return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private static void ApplyChild(JsonObject result, JsonObject child)
        {
            if (child["mainClass"] is not null)
                result["mainClass"] = child["mainClass"]!.DeepClone();
            if (child["type"] is not null)
                result["type"] = child["type"]!.DeepClone();
            if (child["minecraftArguments"] is not null)
                result["minecraftArguments"] = child["minecraftArguments"]!.DeepClone();
            if (child["releaseTime"] is not null)
                result["releaseTime"] = child["releaseTime"]!.DeepClone();
            if (child["time"] is not null)
                result["time"] = child["time"]!.DeepClone();

            MergeLibraryList(result, child);
            MergeArgumentSection(result, child, "jvm");
            MergeArgumentSection(result, child, "game");
        }

        private static void MergeLibraryList(JsonObject result, JsonObject child)
        {
            var list = EnumerateLibs(result["libraries"]).Select(n => n.DeepClone()!).ToList();
            foreach (var lib in EnumerateLibs(child["libraries"]))
            {
                var name = LibraryName(lib);
                var clone = lib.DeepClone()!;
                if (string.IsNullOrEmpty(name))
                {
                    list.Add(clone);
                    continue;
                }
                var index = list.FindIndex(existing => string.Equals(LibraryName(existing), name, System.StringComparison.OrdinalIgnoreCase));
                if (index >= 0) list[index] = clone;
                else list.Add(clone);
            }

            var merged = new JsonArray();
            foreach (var lib in list) merged.Add(lib);
            result["libraries"] = merged;
        }

        private static string LibraryName(JsonNode lib)
            => lib is JsonObject obj && obj["name"] is JsonValue value ? value.GetValue<string>() ?? "" : "";

        private static IEnumerable<JsonNode> EnumerateLibs(JsonNode? node)
        {
            if (node is not JsonArray array) yield break;
            foreach (var item in array)
            {
                if (item is not null) yield return item;
            }
        }

        private static void MergeArgumentSection(JsonObject result, JsonObject child, string section)
        {
            var childArgs = child["arguments"]?[section]?.AsArray();
            if (childArgs is null || childArgs.Count == 0) return;

            result["arguments"] ??= new JsonObject();
            var args = result["arguments"]!.AsObject();
            var combined = new JsonArray();
            foreach (var item in args[section]?.AsArray() ?? new JsonArray())
            {
                if (item is not null) combined.Add(item.DeepClone());
            }
            foreach (var item in childArgs)
            {
                if (item is not null) combined.Add(item.DeepClone());
            }
            args[section] = combined;
        }
    }
}
