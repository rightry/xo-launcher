using System.Collections.Generic;
using System.Linq;

namespace XO_Launcher.Models
{
    public sealed class ResourceCategory
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? ModrinthSlug { get; init; }
        public int? CurseForgeId { get; init; }

        public override string ToString() => Name;
    }

    /// <summary>模组/光影/资源包/整合包的排序与分类（Modrinth slug + CurseForge categoryId）。</summary>
    public static class ResourceCatalog
    {
        public static IReadOnlyList<ResourceCategory> CategoriesFor(string projectType, string source)
        {
            var all = new ResourceCategory { Id = "", Name = "全部分类" };
            var list = projectType switch
            {
                ContentKind.Shader => ShaderCategories,
                ContentKind.ResourcePack => PackCategories,
                ContentKind.Modpack => PackModpackCategories,
                _ => ModCategories
            };
            IEnumerable<ResourceCategory> filtered = source == "curseforge"
                ? list.Where(c => c.CurseForgeId is > 0)
                : list.Where(c => !string.IsNullOrWhiteSpace(c.ModrinthSlug));
            return new[] { all }.Concat(filtered).ToList();
        }

        public static int CurseForgeClassId(string projectType) => projectType switch
        {
            ContentKind.Shader => 6552,
            ContentKind.ResourcePack => 12,
            ContentKind.Modpack => 4471,
            _ => 6
        };

        public static string TypeName(string projectType) => ContentKind.DisplayName(projectType);

        private static readonly ResourceCategory[] ModCategories =
        {
            new() { Id = "optimization", Name = "优化", ModrinthSlug = "optimization", CurseForgeId = 5212 },
            new() { Id = "technology", Name = "科技", ModrinthSlug = "technology", CurseForgeId = 412 },
            new() { Id = "magic", Name = "魔法", ModrinthSlug = "magic", CurseForgeId = 414 },
            new() { Id = "adventure", Name = "冒险", ModrinthSlug = "adventure", CurseForgeId = 422 },
            new() { Id = "decoration", Name = "装饰", ModrinthSlug = "decoration", CurseForgeId = 424 },
            new() { Id = "equipment", Name = "装备", ModrinthSlug = "equipment", CurseForgeId = 434 },
            new() { Id = "food", Name = "食物", ModrinthSlug = "food", CurseForgeId = 436 },
            new() { Id = "mobs", Name = "生物", ModrinthSlug = "mobs", CurseForgeId = 411 },
            new() { Id = "worldgen", Name = "世界生成", ModrinthSlug = "worldgen", CurseForgeId = 406 },
            new() { Id = "utility", Name = "实用", ModrinthSlug = "utility", CurseForgeId = 5191 },
            new() { Id = "transportation", Name = "运输", ModrinthSlug = "transportation", CurseForgeId = 416 },
            new() { Id = "storage", Name = "存储", ModrinthSlug = "storage", CurseForgeId = 409 },
            new() { Id = "library", Name = "前置 / API", ModrinthSlug = "library", CurseForgeId = 421 },
            new() { Id = "management", Name = "管理", ModrinthSlug = "management", CurseForgeId = 425 },
            new() { Id = "social", Name = "社交", ModrinthSlug = "social", CurseForgeId = 423 },
            new() { Id = "economy", Name = "经济", ModrinthSlug = "economy" },
            new() { Id = "game-mechanics", Name = "游戏机制", ModrinthSlug = "game-mechanics" },
            new() { Id = "minigame", Name = "小游戏", ModrinthSlug = "minigame", CurseForgeId = 415 },
            new() { Id = "redstone", Name = "红石", CurseForgeId = 455 },
            new() { Id = "farming", Name = "农业", CurseForgeId = 420 }
        };

        private static readonly ResourceCategory[] ShaderCategories =
        {
            new() { Id = "realistic", Name = "写实", ModrinthSlug = "realistic" },
            new() { Id = "fantasy", Name = "幻想", ModrinthSlug = "fantasy" },
            new() { Id = "vanilla-like", Name = "原版风格", ModrinthSlug = "vanilla-like" },
            new() { Id = "semi-realistic", Name = "半写实", ModrinthSlug = "semi-realistic" },
            new() { Id = "cartoon", Name = "卡通", ModrinthSlug = "cartoon" }
        };

        private static readonly ResourceCategory[] PackCategories =
        {
            new() { Id = "16x", Name = "16x", ModrinthSlug = "16x" },
            new() { Id = "32x", Name = "32x", ModrinthSlug = "32x" },
            new() { Id = "64x", Name = "64x", ModrinthSlug = "64x" },
            new() { Id = "128x", Name = "128x", ModrinthSlug = "128x" },
            new() { Id = "256x", Name = "256x+", ModrinthSlug = "256x" },
            new() { Id = "realistic", Name = "写实", ModrinthSlug = "realistic" },
            new() { Id = "vanilla-like", Name = "原版风格", ModrinthSlug = "vanilla-like" },
            new() { Id = "themed", Name = "主题", ModrinthSlug = "themed" },
            new() { Id = "modded", Name = "模组向", ModrinthSlug = "modded" }
        };

        private static readonly ResourceCategory[] PackModpackCategories =
        {
            new() { Id = "technology", Name = "科技", ModrinthSlug = "technology", CurseForgeId = 4485 },
            new() { Id = "magic", Name = "魔法", ModrinthSlug = "magic", CurseForgeId = 4479 },
            new() { Id = "adventure", Name = "冒险", ModrinthSlug = "adventure", CurseForgeId = 4481 },
            new() { Id = "lightweight", Name = "轻量", ModrinthSlug = "lightweight", CurseForgeId = 4484 },
            new() { Id = "multiplayer", Name = "多人", ModrinthSlug = "multiplayer", CurseForgeId = 4483 },
            new() { Id = "challenging", Name = "挑战", ModrinthSlug = "challenging", CurseForgeId = 4478 },
            new() { Id = "optimization", Name = "优化", ModrinthSlug = "optimization" },
            new() { Id = "quests", Name = "任务", ModrinthSlug = "quests" }
        };
    }
}
