using System;
using System.Linq;

namespace Splatoon.Config
{
    /// <summary>Resolved strongly typed defaults; Excel is the sole authority for gameplay values.</summary>
    public static class GameplayConfig
    {
        public static cfg.GlobalConfig Global => LubanConfigService.Current.Tables.TbGlobal.Get(1);
        public static cfg.RoomModeConfig Mode => LubanConfigService.Current.Tables.TbRoomMode.Get(Global.DefaultModeId);
        public static cfg.CharacterConfig Character => LubanConfigService.Current.Tables.TbCharacter.Get(Mode.CharacterId);
        public static cfg.WeaponConfig Weapon => LubanConfigService.Current.Tables.TbWeapon.Get(Mode.WeaponId);
        public static cfg.ArenaConfig Arena => LubanConfigService.Current.Tables.TbArena.Get(Mode.ArenaId);
        public static void Validate(cfg.Tables supplied = null)
        {
            var tables = supplied ?? LubanConfigService.Current.Tables;
            var global = tables.TbGlobal.GetOrDefault(1);
            var mode = global == null ? null : tables.TbRoomMode.GetOrDefault(global.DefaultModeId);
            Require(tables.TbGlobal.DataList.Count == 1 && global != null, "全局表必须且只能包含 ID=1 的记录");
            Require(mode != null && tables.TbCharacter.GetOrDefault(mode.CharacterId) != null && tables.TbWeapon.GetOrDefault(mode.WeaponId) != null && tables.TbArena.GetOrDefault(mode.ArenaId) != null, "默认模式引用不存在");
            foreach (var table in new System.Collections.IEnumerable[] { tables.TbGlobal.DataList, tables.TbCharacter.DataList, tables.TbWeapon.DataList, tables.TbRoomMode.DataList, tables.TbArena.DataList })
                foreach (var row in table)
                    foreach (var field in row.GetType().GetFields())
                        if (field.FieldType == typeof(float))
                            Require(float.IsFinite((float)field.GetValue(row)) && (float)field.GetValue(row) >= 0, row.GetType().Name + "." + field.Name + " 必须为有限非负数");
            foreach (var c in tables.TbCharacter.DataList)
            {
                Require(c.Id > 0 && c.MaxHealth > 0 && c.MaxInk > 0 && c.MoveSpeed > 0 && c.SwimSpeed > 0 && c.Gravity > 0 && c.JumpSpeed > 0, "角色数值无效");
                Require(!string.IsNullOrWhiteSpace(c.VisualAddress), "角色缺少外观地址");
            }
            foreach (var w in tables.TbWeapon.DataList)
            {
                Require(w.Id > 0 && w.FireRate > 0 && w.FireRate <= 240 && w.Lifetime > 0 && w.Lifetime <= 10 && w.ShotInk > 0 && w.Damage > 0, "武器射击配置无效");
                Require(w.SpeedMin > 0 && w.SpeedMax >= w.SpeedMin && w.CollisionRadius > 0 && w.Gravity > 0, "弹道配置无效");
                Require(w.PaintRadiusMin > 0 && w.PaintRadiusMax >= w.PaintRadiusMin && w.PaintHardness <= 1 && w.PaintStrength <= 1 && w.PaintStrength > 0 && w.SpreadDegrees <= 45, "笔刷或散布配置无效");
                Require(!string.IsNullOrWhiteSpace(w.PrefabAddress), "武器缺少资源地址");
            }
            foreach (var m in tables.TbRoomMode.DataList)
            {
                Require(tables.TbCharacter.GetOrDefault(m.CharacterId) != null && tables.TbWeapon.GetOrDefault(m.WeaponId) != null && tables.TbArena.GetOrDefault(m.ArenaId) != null, "模式表存在无效引用");
                Require(m.MaxPlayers >= 2 && m.MaxPlayers <= 4 && m.MinPlayers >= 2 && m.MinPlayers <= m.MaxPlayers && m.MatchSeconds > 0 && m.GroundOnlyScore, "当前模式要求 2–4 人且仅地面计分");
            }
            foreach (var a in tables.TbArena.DataList)
                Require(a.Width > 0 && a.Length > 0 && a.Width <= 256 && a.Length <= 256 && a.CellSize >= .0625f && a.CellSize <= .5f && Math.Abs(a.Width / a.CellSize - Math.Round(a.Width / a.CellSize)) < .0001 && Math.Abs(a.Length / a.CellSize - Math.Round(a.Length / a.CellSize)) < .0001 && a.LayoutVersion > 0 && !string.IsNullOrWhiteSpace(a.SceneAddress), "场地尺寸或网格配置无效");
            Require(global.NetworkTickRate >= 10 && global.NetworkTickRate <= 120 && global.ProjectileStepRate >= global.NetworkTickRate && global.ProjectileStepRate % global.NetworkTickRate == 0 && global.ProjectileStepRate <= 480, "网络频率与子步频率必须整除");
            Require(global.DefaultPort > 0 && global.DefaultPort <= 65535 && global.ConnectionTimeout > 0 && global.InputTimeout > 0, "网络超时或端口无效");
            Require(global.SnapshotChunkBytes >= 512 && global.SnapshotChunkBytes <= 8192 && global.ChunksPerFrame > 0 && global.ChunksPerFrame <= 32 && global.CheckpointStamps >= 32, "同步预算无效");
            Require(global.PaintThreshold > 0 && global.PaintThreshold <= 1 && global.MaxPaintMemoryMiB > 0 && global.PaintWorldUvScale > 0 && global.PaintWorldUvScale <= 1 && global.PaintShapeNoiseScale > 0 && global.PaintShapeNoiseScale <= 512, "涂色阈值或内存预算无效");
        }
        private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    }
}
