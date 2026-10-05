using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XO_Launcher.Models;

namespace XO_Launcher.Services
{
    public static class InstanceModService
    {
        public static IReadOnlyList<InstanceModItem> Scan(string instanceDir, string? projectType = ContentKind.Mod)
        {
            var folder = Path.Combine(instanceDir, ContentKind.LibraryFolder(projectType));
            Directory.CreateDirectory(folder);
            var items = new List<InstanceModItem>();

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var core = disabled ? name[..^".disabled".Length] : name;
                if (!IsContentFile(core, projectType)) continue;

                var info = new FileInfo(file);
                var (display, version, _) = ModNameTranslationService.DescribeFile(file, core);
                items.Add(new InstanceModItem
                {
                    FilePath = file,
                    Name = core,
                    DisplayName = display,
                    Version = version,
                    IsEnabled = !disabled,
                    SizeText = FormatSize(info.Length)
                });
            }

            if (projectType is ContentKind.Shader or ContentKind.ResourcePack)
            {
                foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var core = disabled ? name[..^".disabled".Length] : name;
                    items.Add(new InstanceModItem
                    {
                        FilePath = dir,
                        Name = core,
                        DisplayName = ModNameTranslationService.Translate(core, core),
                        IsEnabled = !disabled,
                        IsDirectory = true,
                        SizeText = "文件夹"
                    });
                }
            }

            return items
                .OrderByDescending(m => m.IsEnabled)
                .ThenBy(m => m.ShownName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void SetEnabled(InstanceModItem item, bool enabled)
        {
            if (item.IsEnabled == enabled) return;
            var dest = enabled ? StripDisabled(item.FilePath) : item.FilePath + ".disabled";
            if (string.Equals(item.FilePath, dest, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(dest) || Directory.Exists(dest))
                throw new IOException("目标已存在：" + Path.GetFileName(dest));
            if (item.IsDirectory)
                Directory.Move(item.FilePath, dest);
            else
                File.Move(item.FilePath, dest);
        }

        public static void Delete(InstanceModItem item)
        {
            if (item.IsDirectory)
            {
                if (Directory.Exists(item.FilePath))
                    Directory.Delete(item.FilePath, recursive: true);
                return;
            }
            if (File.Exists(item.FilePath))
                File.Delete(item.FilePath);
        }

        private static bool IsContentFile(string name, string? projectType)
        {
            var core = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? name[..^".disabled".Length]
                : name;
            if (core.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || core.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || core.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase))
                return true;
            return projectType is ContentKind.Shader or ContentKind.ResourcePack;
        }

        private static string StripDisabled(string path)
            => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? path[..^".disabled".Length]
                : path;

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        }
    }
}
