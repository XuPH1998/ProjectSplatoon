namespace Splatoon.Config
{
    /// <summary>Shared, signed tuning from the generated Luban source table.</summary>
    public static class BlockGameplaySettings
    {
        // Lattice helpers are also used by isolated editor geometry tests before
        // the content service is initialized. These are the shipped defaults.
        public static float WorldTopHeight => LubanConfigService.Current.IsReady ? GameplayConfig.Global.BlockWorldTopHeight : 8f;
        public static float SpreadInterval => LubanConfigService.Current.IsReady ? GameplayConfig.Global.BlockSpreadInterval : .25f;
        public static int SpreadMaxStepCells => LubanConfigService.Current.IsReady ? GameplayConfig.Global.BlockSpreadMaxStepCells : 1;
        public static int SpreadMinStackCells => LubanConfigService.Current.IsReady ? GameplayConfig.Global.BlockSpreadMinStackCells : 2;
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
