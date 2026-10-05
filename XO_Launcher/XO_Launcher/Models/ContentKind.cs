namespace XO_Launcher.Models
{
    /// <summary>模组、光影、资源包等资源类型的显示名称与目录约定。</summary>
    public static class ContentKind
    {
        public const string Mod = "mod";
        public const string Shader = "shader";
        public const string ResourcePack = "resourcepack";
        public const string Modpack = "modpack";

        public static string DisplayName(string? projectType) => projectType switch
        {
            Shader => "光影",
            ResourcePack => "资源包",
            Modpack => "整合包",
            _ => "模组"
        };

        public static string ManagementTitle(string? projectType) => DisplayName(projectType) + "管理";

        public static string LibraryFolder(string? projectType) => projectType switch
        {
            Shader => "shaderpacks",
            ResourcePack => "resourcepacks",
            Modpack => "modpacks",
            _ => "mods"
        };

        public static bool IsInstallable(string? projectType)
            => projectType is Mod or Shader or ResourcePack;
    }
}
