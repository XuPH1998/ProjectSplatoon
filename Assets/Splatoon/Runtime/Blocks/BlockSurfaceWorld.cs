using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Splatoon.Blocks
{
    public readonly struct BlockSurfaceKey : IEquatable<BlockSurfaceKey>
    {
        public readonly int FaceId, U, V;
        public BlockSurfaceKey(int faceId, int u, int v) { FaceId = faceId; U = u; V = v; }
        public bool Equals(BlockSurfaceKey other) => FaceId == other.FaceId && U == other.U && V == other.V;
        public override bool Equals(object obj) => obj is BlockSurfaceKey other && Equals(other);
        public override int GetHashCode() { unchecked { return (FaceId * 397 ^ U) * 397 ^ V; } }
        public static bool operator ==(BlockSurfaceKey a, BlockSurfaceKey b) => a.Equals(b);
        public static bool operator !=(BlockSurfaceKey a, BlockSurfaceKey b) => !a.Equals(b);
        public override string ToString() => $"{FaceId}:{U},{V}";
    }

    public readonly struct BlockSurfaceCell
    {
        public readonly BlockSurfaceKey Key;
        public readonly byte Owner;
        public readonly uint Revision;
        public BlockSurfaceCell(BlockSurfaceKey key, byte owner, uint revision)
        { Key = key; Owner = owner; Revision = revision; }
    }

    /// <summary>An oriented world-metre chart. Only explicitly registered complete squares are legal.</summary>
    public readonly struct BlockSurfaceFace
    {
        public readonly int Id;
        public readonly Vector3 Origin, U, V, Normal;
        public readonly int MinU, MaxU, MinV, MaxV;
        public readonly IReadOnlyList<Vector2Int> ValidCells;
        readonly HashSet<Vector2Int> _valid;

        internal BlockSurfaceFace(int id, Vector3 origin, Vector3 u, Vector3 v, Vector2Int[] cells)
        {
            Id = id; Origin = origin; U = u; V = v; Normal = Vector3.Cross(u, v).normalized;
            ValidCells = Array.AsReadOnly(cells); _valid = new HashSet<Vector2Int>(cells);
            MinU = MinV = 0; MaxU = MaxV = -1;
            if (cells.Length == 0) return;
            int minU = cells[0].x, maxU = minU, minV = cells[0].y, maxV = minV;
            foreach (var cell in cells)
            {
                minU = Math.Min(minU, cell.x); maxU = Math.Max(maxU, cell.x);
                minV = Math.Min(minV, cell.y); maxV = Math.Max(maxV, cell.y);
            }
            MinU = minU; MaxU = maxU; MinV = minV; MaxV = maxV;
        }

        public bool Contains(int u, int v) => _valid != null && _valid.Contains(new Vector2Int(u, v));
        public Vector3 Center(int u, int v)
            => Origin + U * ((u + .5f) * BlockSurfaceWorld.CellSize) + V * ((v + .5f) * BlockSurfaceWorld.CellSize);
        public Vector3 Center(BlockSurfaceKey key) => Center(key.U, key.V);
    }

    /// <summary>
    /// Zero-thickness surface ownership, independent of solid voxel occupancy and dynamic bodies.
    /// Mutation revisions include deletions so a delta cannot resurrect a cleared square.
    /// </summary>
    public sealed class BlockSurfaceWorld
    {
        public const float CellSize = .30f;
        public const byte WireVersion = 1;
        const uint WireMagic = 0x31575342; // BSW1, little endian.
        const byte FullKind = 1, DeltaKind = 2;
        const int HeaderBytes = 30, EntryBytes = 17;
        readonly Dictionary<int, BlockSurfaceFace> _faces = new();
        Dictionary<BlockSurfaceKey, BlockSurfaceCell> _cells = new();
        int _validCellCount;
        ulong _topologyHash;
        bool _topologyDirty = true;

        public uint Round { get; private set; }
        public uint Revision { get; private set; }
        public int PinkCount { get; private set; }
        public int BlueCount { get; private set; }
        public int FaceCount => _faces.Count;
        public ulong TopologyHash { get { if (_topologyDirty) RebuildTopologyHash(); return _topologyHash; } }

        public BlockSurfaceWorld(uint round) { Round = round; }

        /// <summary>Copies mutable ownership for transaction validation; registered face charts are immutable.</summary>
        public BlockSurfaceWorld Clone()
        {
            var copy = new BlockSurfaceWorld(Round)
            {
                Revision = Revision, PinkCount = PinkCount, BlueCount = BlueCount,
                _validCellCount = _validCellCount, _topologyHash = _topologyHash, _topologyDirty = _topologyDirty,
                _cells = new Dictionary<BlockSurfaceKey, BlockSurfaceCell>(_cells)
            };
            foreach (var face in _faces) copy._faces.Add(face.Key, face.Value);
            return copy;
        }

        public void RegisterFace(int id, Vector3 origin, Vector3 u, Vector3 v, IEnumerable<Vector2Int> validCells)
        {
            if (_faces.ContainsKey(id)) throw new ArgumentException("A surface face ID must be unique.", nameof(id));
            if (!Finite(origin) || !Finite(u) || !Finite(v) || Math.Abs(u.sqrMagnitude - 1f) > .0001f ||
                Math.Abs(v.sqrMagnitude - 1f) > .0001f || Math.Abs(Vector3.Dot(u, v)) > .0001f)
                throw new ArgumentException("Surface axes must be finite, orthogonal world-metre unit vectors.");
            if (validCells == null) throw new ArgumentNullException(nameof(validCells));
            var unique = new HashSet<Vector2Int>(validCells);
            var cells = new Vector2Int[unique.Count]; unique.CopyTo(cells);
            Array.Sort(cells, (a, b) => { int c = a.x.CompareTo(b.x); return c != 0 ? c : a.y.CompareTo(b.y); });
            int nextCount = checked(_validCellCount + cells.Length);
            _faces.Add(id, new BlockSurfaceFace(id, origin, u, v, cells));
            _validCellCount = nextCount; _topologyDirty = true;
        }

        public bool TryGetFace(int faceId, out BlockSurfaceFace face) => _faces.TryGetValue(faceId, out face);

        public bool TryGetCenter(BlockSurfaceKey key, out Vector3 center)
        {
            center = default;
            if (!_faces.TryGetValue(key.FaceId, out var face) || !face.Contains(key.U, key.V)) return false;
            center = face.Center(key.U, key.V); return true;
        }

        public bool TryProject(int faceId, Vector3 point, out BlockSurfaceKey key)
        {
            key = default;
            if (!Finite(point) || !_faces.TryGetValue(faceId, out var face)) return false;
            var delta = point - face.Origin;
            if (!Finite(delta) || Math.Abs(Vector3.Dot(delta, face.Normal)) > .01f) return false;
            double u = Math.Floor((double)Vector3.Dot(delta, face.U) / CellSize);
            double v = Math.Floor((double)Vector3.Dot(delta, face.V) / CellSize);
            if (u < int.MinValue || u > int.MaxValue || v < int.MinValue || v > int.MaxValue ||
                !face.Contains((int)u, (int)v)) return false;
            key = new BlockSurfaceKey(faceId, (int)u, (int)v); return true;
        }

        public bool TryGet(BlockSurfaceKey key, out BlockSurfaceCell cell)
        {
            cell = default;
            if (!_faces.TryGetValue(key.FaceId, out var face) || !face.Contains(key.U, key.V)) return false;
            if (!_cells.TryGetValue(key, out cell)) cell = new BlockSurfaceCell(key, 0, 0);
            return true;
        }

        public bool TrySet(BlockSurfaceKey key, byte owner, out byte oldOwner)
        {
            oldOwner = 0;
            if (owner > 2 || !TryGet(key, out var old)) return false;
            oldOwner = old.Owner;
            if (oldOwner == owner) return true;
            // Never wrap a contact/lifecycle identity within one round.
            if (Revision == uint.MaxValue) return false;
            uint next = Revision + 1;
            _cells[key] = new BlockSurfaceCell(key, owner, next);
            if (oldOwner == 1) PinkCount--; else if (oldOwner == 2) BlueCount--;
            if (owner == 1) PinkCount++; else if (owner == 2) BlueCount++;
            Revision = next; return true;
        }

        // The fixed-grid authority preallocates and journals only keys in one atomic
        // transaction. Ownership changes must not copy every painted cell per shot.
        internal void EnsureMutationCapacity(int additional)
        { _cells.EnsureCapacity(checked(_cells.Count + Math.Max(0, additional))); }

        internal void RestoreMutation(uint revision, int pink, int blue,
            IReadOnlyDictionary<BlockSurfaceKey, BlockSurfaceCell> before)
        {
            foreach (var pair in before)
            {
                if (pair.Value.Revision == 0 && pair.Value.Owner == 0) _cells.Remove(pair.Key);
                else _cells[pair.Key] = pair.Value;
            }
            Revision = revision; PinkCount = pink; BlueCount = blue;
        }

        public bool IsCurrent(BlockSurfaceKey key, byte owner, uint revision)
            => TryGet(key, out var cell) && cell.Owner == owner && cell.Revision == revision;
        public bool IsCurrent(uint round, BlockSurfaceKey key, byte owner, uint revision)
            => round == Round && IsCurrent(key, owner, revision);

        /// <summary>Preserves the immutable geometry registration while starting a new ownership epoch.</summary>
        public void Clear(uint round)
        { _cells.Clear(); Round = round; Revision = 0; PinkCount = BlueCount = 0; }

        public IEnumerable<BlockSurfaceCell> EnumeratePainted()
        {
            // Stable order makes snapshots and downstream batched rendering reproducible.
            foreach (var cell in SortedCells(_cells.Values)) if (cell.Owner != 0) yield return cell;
        }

        /// <summary>Uses the original build disk's centre, rings, and interleaved opposite sides.</summary>
        public IEnumerable<BlockSurfaceKey> EnumerateDisk(int faceId, BlockSurfaceKey centerKey, float radius)
        {
            if (centerKey.FaceId != faceId || !Finite(radius) || radius < 0 ||
                !_faces.TryGetValue(faceId, out var face) || !face.Contains(centerKey.U, centerKey.V)) yield break;
            double extent = Math.Ceiling((double)radius / CellSize);
            float squared = radius * radius;
            yield return centerKey;
            if (extent <= 128)
            {
                for (int ring = 1; ring <= (int)extent; ring++)
                {
                    for (int x = -ring; x <= ring; x++) for (int side = -1; side <= 1; side += 2)
                    {
                        if ((x * x + ring * ring) * CellSize * CellSize > squared + .00001f) continue;
                        if (TryOffset(face, centerKey, x, ring * side, out var key)) yield return key;
                    }
                    for (int y = -ring + 1; y < ring; y++) for (int side = -1; side <= 1; side += 2)
                    {
                        if ((y * y + ring * ring) * CellSize * CellSize > squared + .00001f) continue;
                        if (TryOffset(face, centerKey, ring * side, y, out var key)) yield return key;
                    }
                }
            }
            else
            {
                // A sparse chart or an unbounded caller radius must not enumerate billions of empty rings.
                var candidates = new List<BlockSurfaceKey>();
                foreach (var cell in face.ValidCells)
                {
                    long x = (long)cell.x - centerKey.U, y = (long)cell.y - centerKey.V;
                    if (x == 0 && y == 0) continue;
                    if ((float)((double)x * x + (double)y * y) * CellSize * CellSize <= squared + .00001f)
                        candidates.Add(new BlockSurfaceKey(faceId, cell.x, cell.y));
                }
                candidates.Sort((a, b) => CompareDisk(a, b, centerKey));
                foreach (var key in candidates) yield return key;
            }
        }

        static bool TryOffset(BlockSurfaceFace face, BlockSurfaceKey center, int x, int y, out BlockSurfaceKey key)
        {
            key = default;
            long u = (long)center.U + x, v = (long)center.V + y;
            if (u < int.MinValue || u > int.MaxValue || v < int.MinValue || v > int.MaxValue ||
                !face.Contains((int)u, (int)v)) return false;
            key = new BlockSurfaceKey(face.Id, (int)u, (int)v); return true;
        }

        static int CompareDisk(BlockSurfaceKey a, BlockSurfaceKey b, BlockSurfaceKey center)
        {
            long ax = (long)a.U - center.U, ay = (long)a.V - center.V;
            long bx = (long)b.U - center.U, by = (long)b.V - center.V;
            long ar = Math.Max(Math.Abs(ax), Math.Abs(ay)), br = Math.Max(Math.Abs(bx), Math.Abs(by));
            int c = ar.CompareTo(br);
            return c != 0 ? c : DiskIndex(ax, ay, ar).CompareTo(DiskIndex(bx, by, br));
        }

        static long DiskIndex(long x, long y, long ring)
            => Math.Abs(y) == ring ? (x + ring) * 2 + (y < 0 ? 0 : 1)
                : 4 * ring + 2 + (y + ring - 1) * 2 + (x < 0 ? 0 : 1);

        public byte[] ExportFull() => Encode(FullKind, 0);
        public byte[] ExportDelta(uint baseline)
        {
            if (baseline > Revision) throw new ArgumentOutOfRangeException(nameof(baseline));
            return Encode(DeltaKind, baseline);
        }

        byte[] Encode(byte kind, uint baseline)
        {
            var cells = new List<BlockSurfaceCell>();
            foreach (var cell in _cells.Values) if (kind == FullKind || cell.Revision > baseline) cells.Add(cell);
            cells.Sort(CompareCells);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(WireMagic); writer.Write(WireVersion); writer.Write(kind); writer.Write(Round);
            writer.Write(TopologyHash); writer.Write(baseline); writer.Write(Revision); writer.Write(cells.Count);
            foreach (var cell in cells)
            {
                writer.Write(cell.Key.FaceId); writer.Write(cell.Key.U); writer.Write(cell.Key.V);
                writer.Write(cell.Owner); writer.Write(cell.Revision);
            }
            writer.Flush(); return stream.ToArray();
        }

        public bool TryImportFull(byte[] data) => TryImport(data, FullKind);
        public bool TryImportDelta(byte[] data) => TryImport(data, DeltaKind);

        /// <summary>Builds detached ownership for paired publication without changing this world.</summary>
        public bool TryPrepareImport(byte[] data, bool full, uint round, out BlockSurfaceWorld prepared)
        {
            prepared = null;
            if (!full && round != Round) return false;
            byte kind = full ? FullKind : DeltaKind;
            uint minimumRevision = round == Round ? Revision : 0;
            if (!TryReadImport(data, kind, round, minimumRevision, out var incoming, out uint revision)) return false;
            var replacement = ImportedCells(incoming, full, out int pink, out int blue);
            var copy = new BlockSurfaceWorld(round)
            {
                Revision = revision, PinkCount = pink, BlueCount = blue,
                _validCellCount = _validCellCount, _topologyHash = _topologyHash, _topologyDirty = _topologyDirty,
                _cells = replacement
            };
            foreach (var face in _faces) copy._faces.Add(face.Key, face.Value);
            prepared = copy; return true;
        }

        bool TryImport(byte[] data, byte expectedKind)
        {
            if (!TryReadImport(data, expectedKind, Round, Revision, out var incoming, out uint revision)) return false;
            var replacement = ImportedCells(incoming, expectedKind == FullKind, out int pink, out int blue);
            _cells = replacement; Revision = revision; PinkCount = pink; BlueCount = blue;
            return true;
        }

        Dictionary<BlockSurfaceKey, BlockSurfaceCell> ImportedCells(
            Dictionary<BlockSurfaceKey, BlockSurfaceCell> incoming, bool full, out int pink, out int blue)
        {
            // A delta needs one independent ownership copy while a prepared pair waits.
            // Counts change only for incoming keys; owner-zero entries retain deletion revisions.
            var replacement = full ? incoming : new Dictionary<BlockSurfaceKey, BlockSurfaceCell>(_cells);
            pink = full ? 0 : PinkCount; blue = full ? 0 : BlueCount;
            foreach (var pair in incoming)
            {
                if (!full && _cells.TryGetValue(pair.Key, out var old))
                {
                    if (old.Owner == 1) pink--; else if (old.Owner == 2) blue--;
                }
                if (pair.Value.Owner == 1) pink++; else if (pair.Value.Owner == 2) blue++;
                if (!full) replacement[pair.Key] = pair.Value;
            }
            return replacement;
        }

        bool TryReadImport(byte[] data, byte expectedKind, uint round, uint minimumRevision,
            out Dictionary<BlockSurfaceKey, BlockSurfaceCell> incoming, out uint revision)
        {
            incoming = null; revision = 0;
            if (data == null || data.Length < HeaderBytes) return false;
            using var stream = new MemoryStream(data, false); using var reader = new BinaryReader(stream);
            try
            {
                if (reader.ReadUInt32() != WireMagic || reader.ReadByte() != WireVersion || reader.ReadByte() != expectedKind ||
                    reader.ReadUInt32() != round || reader.ReadUInt64() != TopologyHash) return false;
                uint baseline = reader.ReadUInt32(); revision = reader.ReadUInt32(); int count = reader.ReadInt32();
                if (count < 0 || count > _validCellCount || (long)count * EntryBytes != stream.Length - stream.Position ||
                    revision < minimumRevision || baseline > revision ||
                    (expectedKind == FullKind ? baseline != 0 : baseline != minimumRevision)) return false;
                var entries = new Dictionary<BlockSurfaceKey, BlockSurfaceCell>(count);
                var revisions = new HashSet<uint>(); uint maximum = 0;
                for (int i = 0; i < count; i++)
                {
                    var key = new BlockSurfaceKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    byte owner = reader.ReadByte(); uint cellRevision = reader.ReadUInt32();
                    if (owner > 2 || cellRevision == 0 || cellRevision > revision ||
                        (expectedKind == DeltaKind && cellRevision <= baseline) ||
                        !TryGet(key, out _) || entries.ContainsKey(key) || !revisions.Add(cellRevision)) return false;
                    entries.Add(key, new BlockSurfaceCell(key, owner, cellRevision)); maximum = Math.Max(maximum, cellRevision);
                }
                if (revision != (expectedKind == FullKind ? 0 : baseline) && maximum != revision) return false;
                incoming = entries; return true;
            }
            catch (EndOfStreamException) { return false; }
            catch (IOException) { return false; }
        }


        void RebuildTopologyHash()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(WireVersion); writer.Write(CellSize); writer.Write(_faces.Count);
            var ids = new List<int>(_faces.Keys); ids.Sort();
            foreach (int id in ids)
            {
                var face = _faces[id]; writer.Write(id); WriteVector(writer, face.Origin);
                WriteVector(writer, face.U); WriteVector(writer, face.V); writer.Write(face.ValidCells.Count);
                foreach (var cell in face.ValidCells) { writer.Write(cell.x); writer.Write(cell.y); }
            }
            writer.Flush(); ulong hash = 14695981039346656037UL;
            byte[] bytes = stream.GetBuffer(); int length = checked((int)stream.Length);
            for (int i = 0; i < length; i++)
            { hash ^= bytes[i]; unchecked { hash *= 1099511628211UL; } }
            _topologyHash = hash; _topologyDirty = false;
        }

        static void WriteVector(BinaryWriter writer, Vector3 vector)
        { writer.Write(vector.x); writer.Write(vector.y); writer.Write(vector.z); }
        static List<BlockSurfaceCell> SortedCells(IEnumerable<BlockSurfaceCell> cells)
        { var result = new List<BlockSurfaceCell>(cells); result.Sort(CompareCells); return result; }
        static int CompareCells(BlockSurfaceCell a, BlockSurfaceCell b)
        {
            int c = a.Key.FaceId.CompareTo(b.Key.FaceId); if (c != 0) return c;
            c = a.Key.U.CompareTo(b.Key.U); return c != 0 ? c : a.Key.V.CompareTo(b.Key.V);
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
