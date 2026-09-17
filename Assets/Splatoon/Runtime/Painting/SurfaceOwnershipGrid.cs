using System;
using UnityEngine;
namespace Splatoon.Painting
{
    // One authored planar face, in metres along local X/Z axes.
    public sealed class SurfaceOwnershipGrid
    {
        public readonly byte[] Cells;
        public readonly byte[] State;
        public int SnapshotBytes => Cells.Length * 5;
        public readonly Vector2 Size;
        public readonly float CellSize;
        public readonly int Columns, Rows;
        public double PinkArea { get; private set; }
        public double BlueArea { get; private set; }
        public double TotalArea { get; private set; }
        Vector3[] _worldPoints;
        float[] _noiseFactors;
        bool[] _cachedCells;
        Matrix4x4 _cachedMatrix;
        float _cachedWorldScale, _cachedNoiseScale;
        void PrepareCache(Matrix4x4 matrix, float worldScale, float noiseScale)
        {
            if (_cachedCells == null)
            {
                _worldPoints = new Vector3[Cells.Length]; _noiseFactors = new float[Cells.Length]; _cachedCells = new bool[Cells.Length];
            }
            else if (matrix.Equals(_cachedMatrix) && worldScale == _cachedWorldScale && noiseScale == _cachedNoiseScale) return;
            Array.Clear(_cachedCells, 0, _cachedCells.Length);
            _cachedMatrix = matrix; _cachedWorldScale = worldScale; _cachedNoiseScale = noiseScale;
        }
        public SurfaceOwnershipGrid(Vector2 size, float cellSize, int[] blocked)
        {
            if (size.x <= 0 || size.y <= 0 || cellSize <= 0) throw new ArgumentException("表面尺寸无效");
            Size = size; CellSize = cellSize;
            Columns = Mathf.CeilToInt(size.x / cellSize); Rows = Mathf.CeilToInt(size.y / cellSize);
            Cells = new byte[checked(Columns * Rows)];
            State = new byte[Cells.Length * 4];
            foreach (int index in blocked ?? Array.Empty<int>())
            {
                if (index < 0 || index >= Cells.Length) throw new ArgumentException("不可达网格索引无效");
                Cells[index] = 255;
            }
            for (int i = 0; i < Cells.Length; i++) if (Cells[i] != 255) TotalArea += Area(i);
        }
        public double Area(int i) => Math.Min(CellSize, Size.x - (i % Columns) * (double)CellSize) * Math.Min(CellSize, Size.y - (i / Columns) * (double)CellSize);
        public Vector3 Center(int i)
        {
            int x = i % Columns, z = i / Columns;
            return new Vector3(-Size.x / 2 + x * CellSize + Mathf.Min(CellSize, Size.x - x * CellSize) / 2, 0,
                -Size.y / 2 + z * CellSize + Mathf.Min(CellSize, Size.y - z * CellSize) / 2);
        }
        public byte At(Vector3 p)
        {
            int x = Mathf.FloorToInt((p.x + Size.x / 2) / CellSize), z = Mathf.FloorToInt((p.z + Size.y / 2) / CellSize);
            return x < 0 || z < 0 || x >= Columns || z >= Rows || p.x >= Size.x / 2 || p.z >= Size.y / 2 ? (byte)255 : Cells[z * Columns + x];
        }
        private void SetOwnership(int index, byte team)
        {
            if (team > 2) throw new ArgumentException("归属值无效");
            byte before = Cells[index]; if (before == 255 || before == team) return;
            double area = Area(index);
            if (before == 1) PinkArea -= area; else if (before == 2) BlueArea -= area;
            Cells[index] = team;
            if (team == 1) PinkArea += area; else if (team == 2) BlueArea += area;
        }
        // Explicit full-coverage assignment for diagnostics and authored state.
        public void Set(int index, byte team)
        {
            if (Cells[index] == 255) return;
            SetOwnership(index, team); int o = index * 4;
            State[o] = team == 1 ? (byte)255 : (byte)0; State[o+1] = team == 2 ? (byte)255 : (byte)0;
            State[o+2] = team; State[o+3] = team > 0 ? (byte)255 : (byte)0;
        }
        public void Paint(Vector3 p, float radius, byte team)
        {
            if (radius <= 0 || radius * radius <= p.y * p.y) return;
            int minX = Mathf.Max(0, Mathf.FloorToInt((p.x - radius + Size.x / 2) / CellSize));
            int maxX = Mathf.Min(Columns - 1, Mathf.FloorToInt((p.x + radius + Size.x / 2) / CellSize));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((p.z - radius + Size.y / 2) / CellSize));
            int maxZ = Mathf.Min(Rows - 1, Mathf.FloorToInt((p.z + radius + Size.y / 2) / CellSize));
            for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
            { int i = z * Columns + x; if ((Center(i) - p).sqrMagnitude <= radius * radius) Set(i, team); }
        }
        public void Clear()
        {
            for (int i = 0; i < Cells.Length; i++) if (Cells[i] != 255) Cells[i] = 0;
            PinkArea = BlueArea = 0;
            Array.Clear(State, 0, State.Length);
        }
        public void Apply(PaintStamp stamp, Matrix4x4 localToWorld, float threshold, float worldScale, float noiseScale)
            => Apply(stamp, localToWorld, localToWorld.inverse, new InkShapeAtlas.Brush(stamp), threshold, worldScale, noiseScale);
        internal void Apply(PaintStamp stamp, Matrix4x4 localToWorld, Matrix4x4 inverse, InkShapeAtlas.Brush brush, float threshold, float worldScale, float noiseScale)
        {
            PrepareCache(localToWorld, worldScale, noiseScale);
            Vector3 p = inverse.MultiplyPoint3x4(stamp.Position);
            Vector2 extent = brush.LocalExtents(inverse);
            Vector3 normal = localToWorld.MultiplyVector(Vector3.up).normalized;
            int minX = Mathf.Max(0, Mathf.FloorToInt((p.x - extent.x + Size.x / 2) / CellSize));
            int maxX = Mathf.Min(Columns - 1, Mathf.FloorToInt((p.x + extent.x + Size.x / 2) / CellSize));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((p.z - extent.y + Size.y / 2) / CellSize));
            int maxZ = Mathf.Min(Rows - 1, Mathf.FloorToInt((p.z + extent.y + Size.y / 2) / CellSize));
            for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
            {
                int i = z * Columns + x; if (Cells[i] == 255) continue;
                if (!_cachedCells[i])
                {
                    var point = localToWorld.MultiplyPoint3x4(Center(i));
                    _worldPoints[i] = point;
                    _noiseFactors[i] = 1 + .5f * InkCoverage.Noise(InkCoverage.DetailUV(point, normal, worldScale), noiseScale);
                    _cachedCells[i] = true;
                }
                float f = brush.Coverage(_worldPoints[i]);
                if (f <= 0) continue;
                int o = i * 4;
                var value = InkCoverage.Accumulate(new Color32(State[o], State[o+1], State[o+2], State[o+3]), stamp.Team, f);
                State[o] = value.r; State[o+1] = value.g; State[o+2] = value.b; State[o+3] = value.a;
                SetOwnership(i, value.a / 255f * _noiseFactors[i] >= threshold ? value.b : (byte)0);
            }
        }
        public byte[] Capture()
        {
            var data = new byte[SnapshotBytes];
            Buffer.BlockCopy(Cells, 0, data, 0, Cells.Length);
            Buffer.BlockCopy(State, 0, data, Cells.Length, State.Length);
            return data;
        }
        public void ValidateSnapshot(byte[] data)
        {
            if (data == null || data.Length != SnapshotBytes) throw new InvalidOperationException("表面归属尺寸不一致");
            for (int i = 0; i < Cells.Length; i++)
            {
                if ((data[i] == 255) != (Cells[i] == 255) || (data[i] > 2 && data[i] != 255)) throw new InvalidOperationException("表面归属拓扑不一致");
                int o = Cells.Length + i * 4;
                if (data[o+2] > 2 || data[o+3] != Math.Min(255, data[o]+data[o+1])) throw new InvalidOperationException("累计墨量状态无效");
                if (data[i] == 255 && (data[o] != 0 || data[o+1] != 0 || data[o+2] != 0 || data[o+3] != 0)) throw new InvalidOperationException("不可达格存在墨量");
            }
        }
        public void Restore(byte[] data)
        {
            ValidateSnapshot(data); Clear();
            for (int i = 0; i < Cells.Length; i++) if (data[i] != 255) SetOwnership(i, data[i]);
            Buffer.BlockCopy(data, Cells.Length, State, 0, State.Length);
        }
    }
}
