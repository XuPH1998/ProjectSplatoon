#if UNITY_EDITOR
namespace Splatoon.Editor
{
    // Keep existing automation entry points, with the current character installer.
    public static class InkMigrationBuilder
    {
        public static void Install() => CombatGirlsBuilder.Install();
        public static void InstallAndBuild() { Install(); PrototypeBuilder.BuildWindows(); }
        public static void ValidateInstalled() => CombatGirlsBuilder.ValidateInstalled();
    }
}
#endif
