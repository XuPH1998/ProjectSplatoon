using System;
using System.Collections.Generic;
using UnityEngine;
using Splatoon.Config;

namespace Splatoon.Painting
{
    [Serializable]
    public sealed class PaintRegion
    {
        public int Id;
        public Vector3 Origin;
        public Quaternion Rotation = Quaternion.identity;
        public Vector2 Size;
        public int[] BlockedCells = Array.Empty<int>();
        public bool Climbable;
        [NonSerialized] public SurfaceOwnershipGrid Grid;
        public Matrix4x4 Matrix(PaintSurface surface) => surface.transform.localToWorldMatrix * Matrix4x4.TRS(Origin, Rotation, Vector3.one);
        public int Key(PaintSurface surface) => checked(surface.SurfaceId * 256 + Id);
    }

    public readonly struct InkContact
    {
        public readonly PaintSurface Surface;
        public readonly PaintRegion Region;
        public readonly Vector3 Point, Normal;
        public readonly byte Owner;
        public bool Climbable => Region != null && Region.Climbable;
        public InkContact(PaintSurface surface, PaintRegion region, Vector3 point, Vector3 normal, byte owner)
        { Surface = surface; Region = region; Point = point; Normal = normal; Owner = owner; }
    }

    public sealed partial class PaintSurface
    {
        [Tooltip("按平面烘焙的非计分墨色查询区域；不增加显示纹理")]
        public PaintRegion[] WallRegions = Array.Empty<PaintRegion>();
        private PaintRegion _groundRegion;
        public IEnumerable<PaintRegion> GameplayRegions
        {
            get
            {
                if (_groundRegion != null) yield return _groundRegion;
                foreach (var r in WallRegions) if (r.Grid != null) yield return r;
            }
        }
        private void InitializeRegions(float cellSize)
        {
            Ownership = Scores ? new SurfaceOwnershipGrid(WalkableSize, cellSize, BlockedCells) : null;
            _groundRegion = Scores ? new PaintRegion { Id = 0, Size = WalkableSize, Grid = Ownership } : null;
            foreach (var r in WallRegions) r.Grid = new SurfaceOwnershipGrid(r.Size, cellSize, r.BlockedCells);
        }
        public bool QueryRegion(Vector3 point, Vector3 normal, out InkContact contact)
        {
            foreach (var r in GameplayRegions)
            {
                var m = r.Matrix(this); var n = m.MultiplyVector(Vector3.up).normalized;
                if (Vector3.Dot(n, normal) < .95f) continue;
                var local = m.inverse.MultiplyPoint3x4(point);
                if (Mathf.Abs(local.y) > .06f) continue;
                byte owner = r.Grid.At(local);
                if (owner == 255) continue;
                contact = new InkContact(this, r, point, n, owner); return true;
            }
            contact = default; return false;
        }
        public void ApplyRegions(PaintStamp stamp)
        {
            foreach (var r in GameplayRegions)
            {
                var m = r.Matrix(this);
                // Never paint the reverse face of a thin wall.
                if (Vector3.Dot(m.MultiplyVector(Vector3.up).normalized, stamp.Normal) < .95f) continue;
                if (Mathf.Abs(m.inverse.MultiplyPoint3x4(stamp.Position).y) > .06f) continue;
                r.Grid.Apply(stamp, m, GameplayConfig.Global.PaintThreshold,
                    GameplayConfig.Global.PaintWorldUvScale, GameplayConfig.Global.PaintShapeNoiseScale);
            }
        }
    }
}
