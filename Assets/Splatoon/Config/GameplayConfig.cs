using System;
using System.Linq;

namespace Splatoon.Config
{
    /// <summary>Resolved strongly typed defaults; Excel is the sole authority for gameplay values.</summary>
    public static class GameplayConfig
    {
        public static cfg.GlobalConfig Global => LubanConfigService.Current.Tables.TbGlobal.Get(1);
        public static cfg.RoomModeConfig Mode => LubanConfigService.Current.Tables.TbRoomMode.Get(Global.DefaultModeId);
        public static cfg.HeroConfig DefaultHero => GetHero(Mode.HeroId);
        public static cfg.HeroConfig GetHero(int id) => LubanConfigService.Current.Tables.TbHero.Get(id == 0 ? Mode.HeroId : id);
        public static cfg.MapConfig Map => LubanConfigService.Current.Tables.TbMap.Get(Mode.MapId);
        public static void Validate(cfg.Tables supplied = null)
        {
            var tables = supplied ?? LubanConfigService.Current.Tables;
            var global = tables.TbGlobal.GetOrDefault(1);
            var mode = global == null ? null : tables.TbRoomMode.GetOrDefault(global.DefaultModeId);
            Require(tables.TbGlobal.DataList.Count == 1 && global != null, "全局表必须且只能包含 ID=1 的记录");
            Require(mode != null && tables.TbHero.GetOrDefault(mode.HeroId) != null && tables.TbMap.GetOrDefault(mode.MapId) != null, "默认模式引用不存在");
            foreach (var table in new System.Collections.IEnumerable[] { tables.TbGlobal.DataList, tables.TbHero.DataList, tables.TbRoomMode.DataList, tables.TbMap.DataList })
                foreach (var row in table)
                    foreach (var field in row.GetType().GetFields())
                        if (field.FieldType == typeof(float))
                            Require(float.IsFinite((float)field.GetValue(row)) && (float)field.GetValue(row) >= 0, row.GetType().Name + "." + field.Name + " 必须为有限非负数");
            foreach (var c in tables.TbHero.DataList)
            {
                Require(c.Id > 0 && c.MaxHealth > 0 && c.MaxInk > 0 && c.MoveSpeed > 0 && c.SwimSpeed > 0 && c.CharacterGravity > 0 && c.JumpSpeed > 0, "英雄角色数值无效");
                Require(!string.IsNullOrWhiteSpace(c.CharacterPrefabAddress), "角色缺少外观地址");
                Require(c.ShootMoveSpeed > 0 && c.MoveAcceleration > 0 && c.SwimAcceleration > 0 && c.WallSwimSpeed > 0 && c.WallProbeDistance > 0 && c.WallGraceSeconds <= .1f && c.MantleSeconds > 0 && c.EnemyInkHealthFloor <= c.MaxHealth, "移动与恢复配置无效");
            }
            foreach (var w in tables.TbHero.DataList)
            {
                Require(!string.IsNullOrWhiteSpace(w.DisplayName) && w.FireMode >= 0 && w.FireMode <= 3 && w.ShootMoveSpeed > 0 && w.BurstCount > 0, "武器名称、机制或移动配置无效");
                Require(w.PelletCount >= 1 && w.PelletCount <= 8 && w.MuzzleMode >= 0 && w.MuzzleMode <= 1 && w.SemiBufferFrames >= 0 && w.SemiBufferFrames <= w.FireIntervalFrames, "齐射、枪口或点击缓存配置无效");
                Require(w.FireMode == 3 || (w.PelletCount == 1 && w.MuzzleMode == 0 && w.SemiBufferFrames == 0), "新增齐射和轮播仅用于半自动");
                Require(w.MuzzleMode != 1 || w.PelletCount == 1, "双枪每次只发射一颗墨弹");
                Require(w.FireMode == 1 || w.BurstCount == 1, "非三连发武器每次只发射一颗");
                if (w.FireMode == 1) Require(w.BurstCount == 3 && w.BurstRecoveryFrames >= w.FireIntervalFrames, "三连发组间冷却无效");
                if (w.FireMode == 2) Require(w.ChargeFrames > 0 && w.ChargeMinDamage > 0 && w.ChargePartialMaxDamage < w.Damage && w.ChargePartialMaxDamage >= w.ChargeMinDamage && w.ChargeMinInk > 0 && w.ChargeMinInk < w.ShotInk && w.ChargeMinRange > 0 && w.ChargeMinRange <= w.EffectiveRange && w.ChargeMinSpeed > 0 && w.ChargeMinSpeed <= w.SpeedMin && w.ChargeMinJumpSpread >= w.ChargeMinSpread && w.ChargeMinPaintRange >= w.ChargeMinRange, "蓄力端点配置无效");
                Require(w.Id > 0 && w.FireRate > 0 && w.FireRate <= 240 && w.Lifetime > 0 && w.Lifetime <= 10 && w.ShotInk > 0 && w.Damage > 0, "武器射击配置无效");
                Require(w.SpeedMin > 0 && w.SpeedMax >= w.SpeedMin && w.CollisionRadius > 0 && w.ProjectileGravity > 0, "弹道配置无效");
                Require(w.PaintRadiusMin > 0 && w.PaintRadiusMax >= w.PaintRadiusMin && w.PaintHardness <= 1 && w.PaintStrength <= 1 && w.PaintStrength > 0 && w.SpreadDegrees <= 45, "笔刷或散布配置无效");
                Require(!string.IsNullOrWhiteSpace(w.WeaponPrefabAddress), "武器缺少资源地址");
                Require(w.FireIntervalFrames > 0 && Math.Abs(w.FireRate * w.FireIntervalFrames - 60) < .001 && w.StartFrames >= 0 && w.EmergeStartFrames >= w.StartFrames && w.InkRecoverLockFrames >= 0, "武器时间参数以 60Hz 参考帧配置");
                Require(w.DamageMin > 0 && w.DamageMin <= w.Damage && w.DamageReduceStartFrames >= 0 && w.DamageReduceEndFrames > w.DamageReduceStartFrames && w.StraightFrames >= 0 && w.BrakeFrames > 0 && w.BrakeSpeedMultiplier > 0 && w.BrakeSpeedMultiplier <= 1 && w.SpreadRecoverFrames > 0 && w.JumpSpreadDegrees <= 45 && w.TrailSpacing > 0 && w.TrailRadiusMin > 0 && !float.IsInfinity(w.TrailRadiusMin) && w.TrailRadiusMax >= w.TrailRadiusMin && !float.IsInfinity(w.TrailRadiusMax) && w.EffectiveRange > 0 && w.PaintRange >= w.EffectiveRange, "武器弹道或落墨配置无效");
            }
            foreach (var m in tables.TbRoomMode.DataList)
            {
                Require(tables.TbHero.GetOrDefault(m.HeroId) != null && tables.TbMap.GetOrDefault(m.MapId) != null, "模式表存在无效引用");
                Require(m.MaxPlayers >= 2 && m.MaxPlayers <= 4 && m.MinPlayers >= 2 && m.MinPlayers <= m.MaxPlayers && m.MatchSeconds > 0 && m.GroundOnlyScore, "当前模式要求 2–4 人且仅地面计分");
            }
            foreach (var a in tables.TbMap.DataList)
                Require(a.Width > 0 && a.Length > 0 && a.Width <= 256 && a.Length <= 256 && a.CellSize >= .0625f && a.CellSize <= .5f && Math.Abs(a.Width / a.CellSize - Math.Round(a.Width / a.CellSize)) < .0001 && Math.Abs(a.Length / a.CellSize - Math.Round(a.Length / a.CellSize)) < .0001 && a.LayoutVersion > 0 && !string.IsNullOrWhiteSpace(a.SceneAddress), "场地尺寸或网格配置无效");
            Require(global.NetworkTickRate >= 10 && global.NetworkTickRate <= 120 && global.ProjectileStepRate >= global.NetworkTickRate && global.ProjectileStepRate % global.NetworkTickRate == 0 && global.ProjectileStepRate <= 480, "网络频率与子步频率必须整除");
            Require(global.SimulationRate == 60 && global.SimulationRate % global.NetworkTickRate == 0 && global.ProjectileStepRate % global.SimulationRate == 0, "玩法固定 60Hz，输入发送与弹道子步必须整除");
            Require(global.AimCorrectionDistance > 0, "瞄准修正距离必须为有限正数（米）");
            Require(global.DefaultPort > 0 && global.DefaultPort <= 65535 && global.ConnectionTimeout > 0 && global.InputTimeout > 0, "网络超时或端口无效");
            Require(global.SnapshotChunkBytes >= 512 && global.SnapshotChunkBytes <= 8192 && global.ChunksPerFrame > 0 && global.ChunksPerFrame <= 32 && global.CheckpointStamps >= 32, "同步预算无效");
            Require(global.PaintThreshold > 0 && global.PaintThreshold <= 1 && global.MaxPaintMemoryMiB > 0 && global.PaintWorldUvScale > 0 && global.PaintWorldUvScale <= 1 && global.PaintShapeNoiseScale > 0 && global.PaintShapeNoiseScale <= 512, "涂色阈值或内存预算无效");
        }
        private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
