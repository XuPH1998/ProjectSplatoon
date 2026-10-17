using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splatoon.Blocks
{
    public readonly struct BlockSpreadMove
    {
        public readonly Vector3Int From, To;
        public readonly BlockCell Value;
        internal readonly Vector3Int SourceFoundation, TargetFoundation;
        internal readonly Vector3Int? ViaFoundation;
        public BlockSpreadMove(Vector3Int from, Vector3Int to, BlockCell value)
            : this(from, to, value, from, to, null) { }
        internal BlockSpreadMove(Vector3Int from, Vector3Int to, BlockCell value,
            Vector3Int sourceFoundation, Vector3Int targetFoundation, Vector3Int? viaFoundation)
        { From = from; To = to; Value = value; SourceFoundation = sourceFoundation;
            TargetFoundation = targetFoundation; ViaFoundation = viaFoundation; }
    }

    /// <summary>A resumable, read-only search. Each yield charges a bounded search unit.</summary>
    public static class BlockSpreadRules
    {
        static readonly Vector2Int[] Directions =
        {
            new(1, 0), new(0, 1), new(-1, 0), new(0, -1),
            new(1, 1), new(-1, 1), new(-1, -1), new(1, -1)
        };
        sealed class Column
        {
            public bool Valid, Occupied;
            public int Top, Base, Count;
            public BlockCell Value;
        }
        static Vector3Int Cell(Vector2Int column, int y) => new(column.x, y, column.y);
        static IEnumerable<BlockSpreadMove?> Profile(BlockWorld world, Vector2Int column, Column result,
            int referenceBase, int maxStep, Func<Vector3Int, bool> support, bool source)
        {
            int minimum = Mathf.FloorToInt(world.Bottom / BlockWorld.CellSize);
            int maximum = Mathf.RoundToInt(BlockWorld.Top / BlockWorld.CellSize) - 1;
            int top = maximum;
            for (; top >= minimum; top--)
            {
                bool occupied = world.TryGet(Cell(column, top), out var value);
                yield return null;
                if (!occupied) continue;
                result.Occupied = true; result.Value = value; result.Top = top; break;
            }
            if (!result.Occupied)
            {
                if (source) yield break;
                // An empty neighbour must have the same continuous platform,
                // allowing only the configured small terrain step, never a cliff.
                for (int y = referenceBase + maxStep; y >= referenceBase - maxStep; y--)
                {
                    var cell = Cell(column, y);
                    bool valid = world.InBounds(cell) && support(cell);
                    yield return null;
                    if (!valid) continue;
                    result.Valid = true; result.Base = y; result.Top = y - 1; yield break;
                }
                yield break;
            }
            int bottom = top;
            for (int y = top; y >= minimum; y--)
            {
                bool occupied = world.TryGet(Cell(column, y), out var value);
                yield return null;
                if (!occupied) break;
                bottom = y; result.Count++;
                if (value.Anchored) { result.Valid = true; break; }
            }
            if (!result.Valid)
            {
                result.Valid = support(Cell(column, bottom));
                yield return null;
            }
            result.Base = bottom;
            if (!source && Math.Abs(bottom - referenceBase) > maxStep) result.Valid = false;
        }
        public static IEnumerable<BlockSpreadMove?> Search(BlockWorld world, Vector2Int column,
            int maxStep, int minStack, Func<Vector3Int, bool> staticSupport,
            Func<Vector3Int, bool> clear, Func<Vector3, Vector3, bool> route,
            Func<Vector2Int, bool> blocked, ISet<Vector3Int> arrivals, ISet<Vector2Int> receivedColumns = null)
        {
            // An arrival can settle at another height after its support is shot.
            // Protect its receiving column as a source for this round; targets remain eligible.
            if (blocked(column) || receivedColumns != null && receivedColumns.Contains(column)) yield break;
            var source = new Column();
            foreach (var step in Profile(world, column, source, 0, maxStep, staticSupport, true)) yield return step;
            var from = Cell(column, source.Top);
            if (!source.Valid || source.Count < minStack || source.Value.Anchored || arrivals.Contains(from)) yield break;
            // Rotate ties deterministically so a flat footprint does not always grow east.
            int first = ((column.x * 73856093) ^ (column.y * 19349663)) & 7;
            BlockSpreadMove? best = null;
            for (int index = 0; index < Directions.Length; index++)
            {
                var direction = Directions[(first + index) & 7]; var other = column + direction;
                if (blocked(other)) { yield return null; continue; }
                var target = new Column();
                foreach (var step in Profile(world, other, target, source.Base, maxStep, staticSupport, false)) yield return step;
                int y = target.Top + 1;
                if (!target.Valid || source.Top - y < maxStep || best.HasValue && y >= best.Value.To.y) continue;
                var to = Cell(other, y);
                bool available = world.InBounds(to) && !world.TryGet(to, out _) && clear(to);
                yield return null;
                if (!available) continue;
                Vector3 a = BlockWorld.Center(from), b = BlockWorld.Center(to);
                Vector3 high = new(b.x, a.y, b.z);
                bool continuous = false;
                Vector3Int? viaFoundation = null;
                if (direction.x != 0 && direction.y != 0)
                {
                    // Either complete orthogonal route is sufficient. Both its
                    // foundation and swept cube clearance must exist.
                    for (int axis = 0; axis < 2 && !continuous; axis++)
                    {
                        var viaColumn = column + (axis == 0 ? new Vector2Int(direction.x, 0) : new Vector2Int(0, direction.y));
                        if (blocked(viaColumn)) { yield return null; continue; }
                        var via = new Column();
                        foreach (var step in Profile(world, viaColumn, via, source.Base, maxStep, staticSupport, false)) yield return step;
                        if (!via.Valid || via.Top >= from.y || Math.Abs(via.Base - target.Base) > maxStep) continue;
                        Vector3 corner = BlockWorld.Center(Cell(viaColumn, from.y));
                        continuous = route(a, corner) && route(corner, high) && route(high, b);
                        if (continuous) viaFoundation = Cell(viaColumn, via.Base);
                        yield return null;
                    }
                }
                else { continuous = route(a, high) && route(high, b); yield return null; }
                if (continuous) best = new BlockSpreadMove(from, to, source.Value,
                    Cell(column, source.Base), Cell(other, target.Base), viaFoundation);
            }
            if (best.HasValue) yield return best;
        }
    }
}
