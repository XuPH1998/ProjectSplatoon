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
        Matrix4x4 _surfaceMatrix, _matrix, _inverse;
        Vector3 _origin, _normal;
        Quaternion _rotation;
        bool _geometryReady;
        public Matrix4x4 Matrix(PaintSurface surface) { RefreshGeometry(surface); return _matrix; }
        public Matrix4x4 Inverse(PaintSurface surface) { RefreshGeometry(surface); return _inverse; }
        public Vector3 Normal(PaintSurface surface) { RefreshGeometry(surface); return _normal; }
        void RefreshGeometry(PaintSurface surface)
        {
            var world = surface.transform.localToWorldMatrix;
            if (_geometryReady && world.Equals(_surfaceMatrix) && Origin.Equals(_origin) && Rotation.Equals(_rotation)) return;
            _geometryReady = true; _surfaceMatrix = world; _origin = Origin; _rotation = Rotation;
            _matrix = world * Matrix4x4.TRS(Origin, Rotation, Vector3.one);
            _inverse = _matrix.inverse; _normal = _matrix.MultiplyVector(Vector3.up).normalized;
        }
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
        PaintRegion[] _regions = Array.Empty<PaintRegion>();
        public IEnumerable<PaintRegion> GameplayRegions => _regions;
        internal PaintRegion[] CachedRegions => _regions;
        private void InitializeRegions(float cellSize)
        {
            Ownership = Scores ? new SurfaceOwnershipGrid(WalkableSize, cellSize, BlockedCells) : null;
            _groundRegion = Scores ? new PaintRegion { Id = 0, Size = WalkableSize, Grid = Ownership } : null;
            foreach (var r in WallRegions) r.Grid = new SurfaceOwnershipGrid(r.Size, cellSize, r.BlockedCells);
            _regions = new PaintRegion[WallRegions.Length + (_groundRegion != null ? 1 : 0)];
            int index = 0; if (_groundRegion != null) _regions[index++] = _groundRegion;
            foreach (var region in WallRegions) _regions[index++] = region;
        }
        public bool QueryRegion(Vector3 point, Vector3 normal, out InkContact contact)
        {
            foreach (var r in _regions)
            {
                var n = r.Normal(this);
                if (Vector3.Dot(n, normal) < .95f) continue;
                var local = r.Inverse(this).MultiplyPoint3x4(point);
                if (Mathf.Abs(local.y) > .06f) continue;
                byte owner = r.Grid.At(local);
                if (owner == 255) continue;
                contact = new InkContact(this, r, point, n, owner); return true;
            }
            contact = default; return false;
        }
        public void ApplyRegions(PaintStamp stamp)
            => ApplyRegions(stamp, new InkShapeAtlas.Brush(stamp));
        internal void ApplyRegions(PaintStamp stamp, InkShapeAtlas.Brush brush)
        {
            foreach (var r in _regions)
            {
                var m = r.Matrix(this);
                // Never paint the reverse face of a thin wall.
                if (Vector3.Dot(r.Normal(this), stamp.Normal) < .95f) continue;
                var inverse = r.Inverse(this);
                if (Mathf.Abs(inverse.MultiplyPoint3x4(stamp.Position).y) > .06f) continue;
                r.Grid.Apply(stamp, m, inverse, brush, GameplayConfig.Global.PaintThreshold,
                    GameplayConfig.Global.PaintWorldUvScale, GameplayConfig.Global.PaintShapeNoiseScale);
            }
        }
    }
}
