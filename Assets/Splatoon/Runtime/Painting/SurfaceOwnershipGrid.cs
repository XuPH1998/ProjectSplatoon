using System;
using UnityEngine;
namespace Splatoon.Painting
{
    // One authored planar face, in metres along local X/Z axes.
    public sealed class SurfaceOwnershipGrid
    {
        public readonly byte[] Cells;
        public readonly Vector2 Size;
        public readonly float CellSize;
        public readonly int Columns, Rows;
        public double OrangeArea { get; private set; }
        public double BlueArea { get; private set; }
        public double TotalArea { get; private set; }
        public SurfaceOwnershipGrid(Vector2 size, float cellSize, int[] blocked)
        {
            if (size.x <= 0 || size.y <= 0 || cellSize <= 0) throw new ArgumentException("表面尺寸无效");
            Size = size; CellSize = cellSize;
            Columns = Mathf.CeilToInt(size.x / cellSize); Rows = Mathf.CeilToInt(size.y / cellSize);
            Cells = new byte[checked(Columns * Rows)];
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
        public void Set(int index, byte team)
        {
            if (team > 2) throw new ArgumentException("归属值无效");
            byte before = Cells[index]; if (before == 255 || before == team) return;
            double area = Area(index);
            if (before == 1) OrangeArea -= area; else if (before == 2) BlueArea -= area;
            Cells[index] = team;
            if (team == 1) OrangeArea += area; else if (team == 2) BlueArea += area;
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
            OrangeArea = BlueArea = 0;
        }
        public void ValidateSnapshot(byte[] data)
        {
            if (data == null || data.Length != Cells.Length) throw new InvalidOperationException("表面归属尺寸不一致");
            for (int i = 0; i < data.Length; i++)
                if ((data[i] == 255) != (Cells[i] == 255) || (data[i] > 2 && data[i] != 255)) throw new InvalidOperationException("表面归属拓扑不一致");
        }
        public void Restore(byte[] data)
        {
            ValidateSnapshot(data); Clear();
            for (int i = 0; i < data.Length; i++) if (data[i] != 255) Set(i, data[i]);
        }
    }
}
