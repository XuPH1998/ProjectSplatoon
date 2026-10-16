namespace Splatoon.Config
{
    /// <summary>Shared, signed tuning from the generated Luban source table.</summary>
    public static class BlockGameplaySettings
    {
        public static int BlocksPerMaterial => GameplayConfig.Global.BlockBlocksPerMaterial;
        public static int MainAttackMaxBlocks => GameplayConfig.Global.BlockMainAttackMaxBlocks;
        public static float AuxiliaryBudgetFraction => GameplayConfig.Global.BlockAuxiliaryBudgetFraction;
        public static float FriendlyBlastRadiusFraction => GameplayConfig.Global.BlockFriendlyBlastRadiusFraction;
        public static int BlocksPerSpecialPoint => GameplayConfig.Global.BlockBlocksPerSpecialPoint;
        public static int MainSpecialPointCap => GameplayConfig.Global.BlockMainSpecialPointCap;
        public static int SubSpecialPointCap => GameplayConfig.Global.BlockSubSpecialPointCap;
        public static int NetworkBytesPerSecond => GameplayConfig.Global.BlockNetworkBytesPerSecond;
    }
}
