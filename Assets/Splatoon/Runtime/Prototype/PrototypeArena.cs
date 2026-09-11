using System;
using System.Collections.Generic;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Painting;
namespace Splatoon.Prototype
{
    public sealed class PrototypeArena : MonoBehaviour
    {
        public static PrototypeArena Current { get; private set; }
        [Tooltip("掩体和边界投影，从地面比分中排除")] public BoxCollider[] Unpaintable;
        [Tooltip("两队各两个正式出生点")] public Transform[] SpawnPoints;
        public int LayoutVersion = 2;
        public PaintGrid Grid { get; private set; }
        public readonly Dictionary<int, PaintSurface> Surfaces = new();
        public static readonly Color Orange = new(1f, .30f, .055f);
        public static readonly Color Blue = new(.08f, .42f, 1f);
        public static Color TeamColor(byte team) => team == 1 ? Orange : Blue;
        private void Awake()
        {
            Current = this; var config = GameplayConfig.Arena;
            if (LayoutVersion != config.LayoutVersion) throw new InvalidOperationException("场景布局版本与配置不一致");
            Physics.SyncTransforms(); Grid = new PaintGrid(Mathf.RoundToInt(config.Size / config.CellSize), config.CellSize, IsBlocked);
            foreach (var surface in GetComponentsInChildren<PaintSurface>(true))
                if (surface.SurfaceId <= 0 || !Surfaces.TryAdd(surface.SurfaceId, surface)) throw new InvalidOperationException("表面 ID 重复或无效");
        }
        private bool IsBlocked(Vector3 p)
        {
            foreach (var box in Unpaintable)
            { var b = box.bounds; if (p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z) return true; }
            return false;
        }
        public static Vector3 Spawn(byte team, int slot)
        {
            if (Current != null && Current.SpawnPoints != null && Current.SpawnPoints.Length == 4) return Current.SpawnPoints[(team - 1) * 2 + slot].position;
            return new Vector3(slot == 0 ? -4 : 4, .1f, team == 1 ? -13 : 13);
        }
        public void Apply(PaintStamp stamp, bool updateGrid)
        {
            if (!Surfaces.TryGetValue(stamp.SurfaceId, out var surface)) throw new InvalidOperationException("未知涂色表面：" + stamp.SurfaceId);
            surface.Apply(stamp);
            if (updateGrid && surface.Scores && stamp.Normal.y > .9f)
                Grid.Paint(stamp.Position, InkBrush.OwnershipRadius(stamp.Radius, stamp.Hardness, stamp.Strength, GameplayConfig.Global.PaintThreshold), stamp.Team, null);
        }
        public void ClearPaint() { Grid.Clear(null); foreach (var surface in Surfaces.Values) surface.Clear(); }
        private void OnDestroy() { if (Current == this) Current = null; }
    }
}
