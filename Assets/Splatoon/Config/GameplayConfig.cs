using System;
using System.Linq;

namespace Splatoon.Config
{
    /// <summary>Resolved strongly typed defaults; Hero tables and room-scoped weapon asset snapshots.</summary>
    public static class GameplayConfig
    {
        public static cfg.GlobalConfig Global => LubanConfigService.Current.Tables.TbGlobal.Get(1);
        public static cfg.RoomModeConfig Mode => LubanConfigService.Current.Tables.TbRoomMode.Get(Global.DefaultModeId);
        public static cfg.HeroConfig DefaultHero => GetHero(Mode.HeroId);
        public static cfg.HeroConfig GetHero(int id) => LubanConfigService.Current.Tables.TbHero.Get(id == 0 ? Mode.HeroId : id);
        public static WeaponRuntimeConfig GetWeapon(int heroId) => WeaponConfigService.Current.Get(GetHero(heroId));
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
                Require(!string.IsNullOrWhiteSpace(c.WeaponConfigPath) && c.WeaponConfigPath.StartsWith("Assets/", StringComparison.Ordinal) && c.WeaponConfigPath.EndsWith(".asset", StringComparison.Ordinal), "武器配置路径必须为 Assets/.../*.asset");
                Require(!string.IsNullOrWhiteSpace(c.CharacterPrefabAddress), "角色缺少外观地址");
                Require(!string.IsNullOrWhiteSpace(c.DisplayName) && !string.IsNullOrWhiteSpace(c.WeaponTypeName) && !string.IsNullOrWhiteSpace(c.PortraitAddress), "英雄缺少名称、主武器类型或头像地址");
                Require(c.NeutralSwimSpeed > 0 && c.NeutralSwimSpeed < c.MoveSpeed, "无色地面潜墨速度必须大于零且小于普通移动速度");
                Require(c.AirSwimSpeed > 0 && c.AirSwimGravity > 0 && c.AirSwimGravity <= c.CharacterGravity &&
                    c.AirSwimFallSpeed > 0 && c.AirSwimBraking > 0, "空中弦化参数必须为正，缓降重力不得超过普通重力");
                Require(c.MoveAcceleration > 0 && c.SwimAcceleration > 0 && c.WallSwimSpeed > 0 && c.WallProbeDistance > 0 && c.WallGraceSeconds <= .1f && c.MantleSeconds > 0 && c.EnemyInkHealthFloor <= c.MaxHealth, "移动与恢复配置无效");
            }
            foreach (var hero in tables.TbHero.DataList) WeaponConfigValidation.Validate(WeaponConfigService.Current.Get(hero));
            foreach (var m in tables.TbRoomMode.DataList)
            {
                Require(tables.TbHero.GetOrDefault(m.HeroId) != null && tables.TbMap.GetOrDefault(m.MapId) != null, "模式表存在无效引用");
                Require(m.MaxPlayers >= 2 && m.MaxPlayers <= 8 && m.MinPlayers >= 2 && m.MinPlayers <= m.MaxPlayers && m.MatchSeconds > 0, "当前模式要求 2–8 人，每队最多 4 人");
            }
            foreach (var a in tables.TbMap.DataList)
                Require(a.CellSize >= .0625f && a.CellSize <= .5f && !string.IsNullOrWhiteSpace(a.SceneAddress), "地图网格或场景地址配置无效");
            Require(global.NetworkTickRate >= 10 && global.NetworkTickRate <= 120 && global.ProjectileStepRate >= global.NetworkTickRate && global.ProjectileStepRate % global.NetworkTickRate == 0 && global.ProjectileStepRate <= 480, "网络频率与子步频率必须整除");
            Require(global.SimulationRate == 60 && global.SimulationRate % global.NetworkTickRate == 0 && global.ProjectileStepRate % global.SimulationRate == 0, "玩法固定 60Hz，输入发送与弹道子步必须整除");
            Require(global.AimCorrectionDistance > 0, "瞄准近端收敛距离必须为有限正数（米）");
            Require(global.AimFarCorrectionDistance > 0 && global.AimFarCorrectionDistance >= global.AimCorrectionDistance,
                "瞄准远端收敛距离必须为有限正数且不小于近端距离（米）");
            Require(global.DefaultPort > 0 && global.DefaultPort <= 65535 && global.ConnectionTimeout > 0 && global.InputTimeout > 0, "网络超时或端口无效");
            Require(global.SnapshotChunkBytes >= 512 && global.SnapshotChunkBytes <= 8192 && global.ChunksPerFrame > 0 && global.ChunksPerFrame <= 32 && global.CheckpointStamps >= 32, "同步预算无效");
            Require(global.SnapshotBytesPerSecond > 0 && global.SnapshotMaxInFlightRecords >= 1 && global.SnapshotMaxInFlightRecords <= 32, "补同步速率或在途窗口无效");
            Require(global.PaintThreshold > 0 && global.PaintThreshold <= 1 && global.MaxPaintMemoryMiB > 0 && global.PaintWorldUvScale > 0 && global.PaintWorldUvScale <= 1 && global.PaintShapeNoiseScale > 0 && global.PaintShapeNoiseScale <= 512, "涂色阈值或内存预算无效");
        }
        private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
