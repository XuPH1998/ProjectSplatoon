using System;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public static class PrototypeSettings
    {
        public static float Value(string key) => LubanConfigService.Current.Tables.TbPrototype.Get(key).Value;
        public static void Validate()
        {
            foreach (var row in LubanConfigService.Current.Tables.TbPrototype.DataList)
                if (!float.IsFinite(row.Value) || row.Value <= 0) throw new InvalidOperationException($"原型配置值无效：{row.Key}");
            if (Value("MaxPlayers") != 4 || Value("ArenaSize") != 32 || Value("CellSize") != 0.5f)
                throw new InvalidOperationException("当前场地要求最大人数 MaxPlayers=4、场地边长 ArenaSize=32、网格边长 CellSize=0.5。修改这些值需要同步调整场地构建器。");
        }
    }
    public static class PrototypeRules
    {
        public static byte ChooseTeam(int orange, int blue) => (byte)(orange <= blue ? 1 : 2);
        public static bool CanStart(int players, MatchPhase phase) => players >= 2 && phase != MatchPhase.Playing;
        public static bool HasEnded(MatchPhase phase, double now, double end) => phase == MatchPhase.Playing && now >= end;
        public static int Winner(int orange, int blue) => orange == blue ? 0 : orange > blue ? 1 : 2;
        public static float Recover(float ink, float maximum, float rate, float dt) => Mathf.Min(maximum, ink + rate * dt);
        public static bool Spend(ref float ink, float amount)
        { if (ink < amount) return false; ink -= amount; return true; }
        public static float Damage(float health, float amount, bool friendly, double protectedUntil, double now) => friendly || now < protectedUntil ? health : Mathf.Max(0, health - amount);
        public static bool CanRespawn(float health, double now, double due) => health <= 0 && now >= due;
    }
    // 255 marks unpaintable footprints. Cells are the single source of score truth.
    public sealed class PaintGrid
    {
        public readonly byte[] Cells;
        public readonly int Width;
        public readonly float CellSize;
        public int Orange { get; private set; }
        public int Blue { get; private set; }
        public int Total { get; private set; }
        public PaintGrid(int width, float cellSize, Func<Vector3, bool> blocked = null)
        {
            Width = width; CellSize = cellSize; Cells = new byte[width * width];
            for (int i = 0; i < Cells.Length; i++)
                if (blocked != null && blocked(Center(i))) Cells[i] = 255; else Total++;
        }
        public Vector3 Center(int i) => new((i % Width + .5f - Width / 2f) * CellSize, .018f, (i / Width + .5f - Width / 2f) * CellSize);
        public int Index(Vector3 position)
        {
            int x = Mathf.FloorToInt(position.x / CellSize + Width / 2f), z = Mathf.FloorToInt(position.z / CellSize + Width / 2f);
            return x < 0 || z < 0 || x >= Width || z >= Width ? -1 : z * Width + x;
        }
        public byte At(Vector3 p) { int i = Index(p); return i < 0 ? (byte)255 : Cells[i]; }
        public bool Set(int index, byte team)
        {
            byte before = Cells[index];
            if (before == 255 || before == team || team > 2) return false;
            if (before == 1) Orange--; if (before == 2) Blue--;
            Cells[index] = team;
            if (team == 1) Orange++; if (team == 2) Blue++;
            return true;
        }
        public void Paint(Vector3 p, float radius, byte team, Action<int, byte> changed)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt((p.x - radius) / CellSize + Width / 2f));
            int maxX = Mathf.Min(Width - 1, Mathf.FloorToInt((p.x + radius) / CellSize + Width / 2f));
            int minZ = Mathf.Max(0, Mathf.FloorToInt((p.z - radius) / CellSize + Width / 2f));
            int maxZ = Mathf.Min(Width - 1, Mathf.FloorToInt((p.z + radius) / CellSize + Width / 2f));
            for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
            {
                int i = z * Width + x; var d = Center(i) - p; d.y = 0;
                if (d.sqrMagnitude <= radius * radius && Set(i, team)) changed?.Invoke(i, team);
            }
        }
        public void Clear(Action<int, byte> changed)
        { for (int i = 0; i < Cells.Length; i++) if (Set(i, 0)) changed?.Invoke(i, 0); }
        public uint Hash()
        { uint hash = 2166136261; foreach (byte c in Cells) hash = unchecked((hash ^ c) * 16777619); return hash; }
    }
}
