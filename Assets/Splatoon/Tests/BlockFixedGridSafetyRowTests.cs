#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid"), Category("BlockFixedGridSafetyRows")]
    public sealed class BlockFixedGridSafetyRowTests
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo Candidate = typeof(BlockTerrain).GetField("_fixedSafetyCandidate", PrivateInstance);
        static readonly PropertyInfo FixedBlocks = typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.FixedBlocks));
        static readonly PropertyInfo Current = typeof(BlockTerrain).GetProperty(nameof(BlockTerrain.Current));
        static readonly MethodInfo CapsuleQuery = typeof(BlockTerrain).GetMethod("FixedSafetyCapsuleOverlaps", PrivateInstance);
        static readonly MethodInfo BoxQuery = typeof(BlockTerrain).GetMethod("FixedSafetyBoxOverlaps", PrivateInstance);
        GameObject _root;
        BlockTerrain _terrain, _previousCurrent;
        Func<Vector3, float, float, bool> _capsule;
        Func<Vector3, Vector3, Quaternion, bool> _box;

        [SetUp] public void Setup()
        {
            Assert.That(Application.isPlaying, Is.False, "These pure occupancy fixtures run in EditMode.");
            Assert.That(Candidate, Is.Not.Null); Assert.That(FixedBlocks, Is.Not.Null); Assert.That(Current, Is.Not.Null);
            Assert.That(CapsuleQuery, Is.Not.Null); Assert.That(BoxQuery, Is.Not.Null);
            _previousCurrent = BlockTerrain.Current;
            _root = new GameObject("Fixed safety occupied-row fixture");
            _terrain = _root.AddComponent<BlockTerrain>();
            _capsule = (Func<Vector3, float, float, bool>)CapsuleQuery.CreateDelegate(typeof(Func<Vector3, float, float, bool>), _terrain);
            _box = (Func<Vector3, Vector3, Quaternion, bool>)BoxQuery.CreateDelegate(typeof(Func<Vector3, Vector3, Quaternion, bool>), _terrain);
        }

        [TearDown] public void Cleanup()
        {
            try
            {
                if (_terrain != null)
                {
                    Candidate.SetValue(_terrain, null);
                    BlockFixedGridEditFixtureCleanup.ReleaseOwnedRoot(_root);
                    // No EnableFixedGridMode/native resources are needed by these queries.
                    FixedBlocks.SetValue(_terrain, null);
                    if (BlockTerrain.Current == _terrain) Current.SetValue(null, _previousCurrent);
                }
            }
            finally
            {
                if (_root != null) Object.DestroyImmediate(_root);
                _capsule = null; _box = null;
            }
        }

        static BlockWorld NewWorld() => new(new Vector2(80, 80), 7, -12);
        void UseCandidate(BlockWorld world) => Candidate.SetValue(_terrain, world);
        static void Add(BlockWorld world, Vector3Int cell, byte owner = 1)
        {
            Assert.That(world.InBounds(cell), Is.True, "Fixture cell must fit: " + cell);
            world.Set(cell, BlockCell.Create(owner));
        }

        [Test] public void NullAndEmptyOccupancyNeverReportAContact()
        {
            Assert.That(_capsule(Vector3.zero, .6f, .35f), Is.False);
            Assert.That(_box(Vector3.zero, Vector3.one, Quaternion.Euler(17, 43, 29)), Is.False);
            var empty = NewWorld(); UseCandidate(empty);
            foreach (var center in new[] { Vector3.zero, new Vector3(-4.8f, -4.8f, -4.8f), new Vector3(4.8f, 4.8f, 4.8f) })
            {
                AssertCapsule(empty, center, 1.1f, .5f, "empty cross-chunk capsule");
                AssertBox(empty, center, new Vector3(5, .7f, .4f), Quaternion.Euler(0, 37, 0), "empty cross-chunk box");
            }
            Assert.That(empty.Chunks.Count, Is.Zero, "Queries cannot create empty chunks.");
        }

        [Test] public void RandomCapsulesAndObbsMatchLegacyGridAndIndependentOccupiedCells()
        {
            var world = NewWorld(); var random = new System.Random(690019);
            for (int i = 0; i < 700; i++)
                Add(world, new Vector3Int(random.Next(-24, 25), random.Next(-20, 23), random.Next(-24, 25)), (byte)(1 + i % 2));
            UseCandidate(world); var cells = OccupiedCells(world);
            for (int i = 0; i < 600; i++)
            {
                var center = i % 3 == 0 ? BlockWorld.Center(cells[i % cells.Count]) : Next(random, new Vector3(-8, -7, -8), new Vector3(8, 7, 8));
                float segment = i % 7 == 0 ? 0 : (float)random.NextDouble() * 1.2f;
                float radius = i % 11 == 0 ? 0 : .01f + (float)random.NextDouble() * .8f;
                var half = Next(random, Vector3.one * .001f, new Vector3(1.2f, 1, 1.2f));
                if (i % 13 == 0) half.x = 3;
                var rotation = i % 5 == 0 ? Quaternion.identity : Quaternion.Euler(Next(random, -Vector3.one * 180, Vector3.one * 180));
                AssertCapsule(world, center, segment, radius, "random capsule " + i, cells);
                AssertBox(world, center, half, rotation, "random box " + i, cells);
            }
        }

        [TestCase(-32)] [TestCase(-16)] [TestCase(0)] [TestCase(16)]
        public void FullRowsAndBothClippedEndsKeepNegativeAndSixteenCellBoundaries(int originX)
        {
            var world = NewWorld();
            for (int x = originX - 1; x <= originX + 16; x++) Add(world, new Vector3Int(x, -17, -16));
            UseCandidate(world);
            foreach (int x in new[] { originX - 1, originX, originX + 1, originX + 7, originX + 14, originX + 15, originX + 16 })
            {
                var center = BlockWorld.Center(new Vector3Int(x, -17, -16));
                AssertCapsule(world, center, 0, .02f, "row bit " + x);
                AssertBox(world, center, Vector3.one * .02f, Quaternion.identity, "row box " + x);
                Assert.That(_capsule(center, 0, .02f), Is.True);
            }
            var middle = BlockWorld.Center(new Vector3Int(originX + 7, -17, -16));
            AssertBox(world, middle, new Vector3(3, .02f, .02f), Quaternion.identity, "entire row and neighboring chunks");
            for (int x = originX - 1; x <= originX + 16; x++) world.Set(new Vector3Int(x, -17, -16), default);
            AssertBox(world, middle, new Vector3(3, .02f, .02f), Quaternion.identity, "whole row removed");
            Assert.That(_box(middle, new Vector3(3, .02f, .02f), Quaternion.identity), Is.False);
            foreach (int bit in new[] { 0, 15 })
            {
                var cell = new Vector3Int(originX + bit, -17, -16); Add(world, cell);
                AssertBox(world, middle, new Vector3(3, .02f, .02f), Quaternion.identity, "full mask retains only bit " + bit);
                Assert.That(_box(middle, new Vector3(3, .02f, .02f), Quaternion.identity), Is.True);
                world.Set(cell, default);
            }
        }

        [Test] public void ExactContactsAndSmallPenetrationsKeepCapsuleThresholdAndBoxSatTolerance()
        {
            var world = NewWorld(); Add(world, Vector3Int.zero); UseCandidate(world);
            foreach (float delta in new[] { -.00002f, -.000001f, 0, .000001f, .00002f })
            {
                var center = new Vector3(-.1f + delta, .15f, .15f);
                AssertCapsule(world, center, 0, .1f, "capsule tangency " + delta);
                AssertBox(world, center, Vector3.one * .1f, Quaternion.identity, "box tangency " + delta);
            }
            Assert.That(_capsule(new Vector3(-.1f, .15f, .15f), 0, .1f), Is.False, "The original strict squared-distance tolerance rejects tangency.");
            Assert.That(_box(new Vector3(-.1f, .15f, .15f), Vector3.one * .1f, Quaternion.identity), Is.True, "The original OBB predicate includes this face contact.");
            Assert.That(_capsule(BlockWorld.Center(Vector3Int.zero), .6f, 0), Is.False, "Zero radius retains the original negative squared threshold.");
        }

        [Test] public void HolesAndOutOfRangeRowBitsDoNotBecomeSolid()
        {
            var world = NewWorld(); UseCandidate(world);
            foreach (int holeX in new[] { -17, -16, -1, 0, 15, 16 })
            {
                for (int z = -1; z <= 1; z++) for (int x = holeX - 1; x <= holeX + 1; x++)
                    if (x != holeX || z != 0) Add(world, new Vector3Int(x, 16, z));
                var center = BlockWorld.Center(new Vector3Int(holeX, 16, 0));
                AssertCapsule(world, center, .04f, .05f, "hole capsule " + holeX);
                AssertBox(world, center, Vector3.one * .05f, Quaternion.Euler(17, 45, 11), "hole box " + holeX);
                Assert.That(_capsule(center, .04f, .05f), Is.False);
                Assert.That(_box(center, Vector3.one * .05f, Quaternion.Euler(17, 45, 11)), Is.False);
                AssertBox(world, center, new Vector3(.3f, .05f, .05f), Quaternion.identity, "hole side " + holeX);
                Assert.That(_box(center, new Vector3(.3f, .05f, .05f), Quaternion.identity), Is.True);
                for (int z = -1; z <= 1; z++) for (int x = holeX - 1; x <= holeX + 1; x++) world.Set(new Vector3Int(x, 16, z), default);
            }
            // Only the row bit immediately below the AABB's lower x is occupied.
            // It touches the box within SAT tolerance but is outside the original grid range.
            var tangentCenter = new Vector3(.1f, -.15f, -.15f); var tangentHalf = new Vector3(.1f, .05f, .05f);
            var excluded = new Vector3Int(-1, -1, -1); Add(world, excluded);
            Assert.That(BlockWorld.CellAt(tangentCenter - tangentHalf).x, Is.Zero);
            Assert.That(ReferenceBox(excluded, tangentCenter, tangentHalf, Quaternion.identity), Is.True);
            AssertBox(world, tangentCenter, tangentHalf, Quaternion.identity, "lower range mask excludes tangent neighbor");
            Assert.That(_box(tangentCenter, tangentHalf, Quaternion.identity), Is.False);
        }

        [Test] public void SameWorldEditsAndDraftSwitchesAlwaysUseTheirOwnCurrentRows()
        {
            var published = NewWorld(); var cell = new Vector3Int(-17, -1, 16); var center = BlockWorld.Center(cell);
            UseCandidate(published); AssertNearCell(published, cell, false, "initial empty");
            Add(published, cell); AssertNearCell(published, cell, true, "same world add");
            Add(published, cell, 2); AssertNearCell(published, cell, true, "same occupied bit owner change");
            var draft = published.Clone(); draft.Set(cell, default); Add(draft, cell + Vector3Int.right);
            UseCandidate(draft); AssertNearCell(draft, cell, false, "draft removes published cell");
            AssertNearCell(draft, cell + Vector3Int.right, true, "draft adds adjacent chunk cell");
            UseCandidate(published); AssertNearCell(published, cell, true, "published COW row survives draft mutation");
            AssertNearCell(published, cell + Vector3Int.right, false, "published cannot see draft addition");
            published.Set(cell, default); AssertNearCell(published, cell, false, "same world remove");
            published.Seal(1, new[] { BlockWorld.ChunkAt(cell) });
            AssertNearCell(published, cell, false, "removed final row after chunk tombstone");
            Add(published, cell); AssertNearCell(published, cell, true, "same chunk recreated after tombstone");
            UseCandidate(draft); AssertNearCell(draft, cell, false, "source rebuild cannot alter draft COW row");
            AssertNearCell(draft, cell + Vector3Int.right, true, "source rebuild cannot remove draft neighbor");
            Add(draft, cell); UseCandidate(draft); AssertNearCell(draft, cell, true, "draft mutated after prior query");
            Candidate.SetValue(_terrain, null);
            Assert.That(_capsule(center, 0, .02f), Is.False, "Removing the temporary candidate cannot retain its row.");
        }

        sealed class Environment : IBlockFixedGridEnvironment, IBlockFixedGridCommitGuard, IBlockFixedGridRecoveryRollback
        {
            internal bool ThrowRecovery;
            internal int Rollbacks;
            internal Action<BlockFixedGridCommitPreview> Prepare;
            public bool TryGetMapSupport(Vector3Int cell, out BlockFixedGridMapSupport support)
            { support = new BlockFixedGridMapSupport(11, .08f, new BlockSurfaceKey(11, cell.x, cell.z)); return true; }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embedded, bool relocating, out string reason)
            { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
            public bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason)
            { Prepare?.Invoke(preview); reason = null; return true; }
            public void CommitPreparedRecovery() { if (ThrowRecovery) throw new InvalidOperationException("Safety-row rollback fixture"); }
            public void RollbackPreparedRecovery() { Rollbacks++; }
        }

        [Test] public void FailedCommitRestoresPublishedRowsAndNeverRetainsThePreparedCandidate()
        {
            var environment = new Environment(); var surfaces = new BlockSurfaceWorld(7);
            surfaces.RegisterFace(11, Vector3.up * .08f, Vector3.right, Vector3.forward, new[] { Vector2Int.zero });
            surfaces.TrySet(new BlockSurfaceKey(11, 0, 0), 1, out _);
            var fixedWorld = new BlockFixedGridWorld(NewWorld(), surfaces, environment);
            Assert.That(fixedWorld.Build(new[] { Vector3Int.zero }, 1, 1).Added, Is.EqualTo(1));
            Assert.That(fixedWorld.Build(new[] { Vector3Int.up }, 1, 1).Added, Is.EqualTo(1));
            Assert.That(fixedWorld.TryGet(Vector3Int.zero, out var bottom), Is.True);
            FixedBlocks.SetValue(_terrain, fixedWorld); var published = fixedWorld.Storage;
            AssertNearCell(published, Vector3Int.up, true, "published before failed commit");
            bool prepared = false, draftCapsule = true, draftBox = true, draftOraclesAgree = false;
            environment.Prepare = preview =>
            {
                prepared = true; UseCandidate(preview.Storage);
                try
                {
                    var center = BlockWorld.Center(Vector3Int.up);
                    draftCapsule = _capsule(center, 0, .02f); draftBox = _box(center, Vector3.one * .02f, Quaternion.identity);
                    draftOraclesAgree = draftCapsule == LegacyCapsule(preview.Storage, center, 0, .02f) &&
                        draftBox == LegacyBox(preview.Storage, center, Vector3.one * .02f, Quaternion.identity);
                }
                finally { Candidate.SetValue(_terrain, null); }
            };
            uint revision = fixedWorld.Revision; environment.ThrowRecovery = true;
            Assert.That(fixedWorld.Destroy(new[] { bottom.Handle }, 2, 1, 0).RemovedEnemy, Is.Zero);
            Assert.That(prepared, Is.True); Assert.That(draftOraclesAgree, Is.True);
            Assert.That(draftCapsule, Is.False); Assert.That(draftBox, Is.False, "The prepared upper cell has already landed in the lower cell.");
            Assert.That(environment.Rollbacks, Is.EqualTo(1)); Assert.That(fixedWorld.Revision, Is.EqualTo(revision));
            Assert.That(fixedWorld.Storage, Is.SameAs(published)); Assert.That(Candidate.GetValue(_terrain), Is.Null);
            AssertNearCell(fixedWorld.Storage, Vector3Int.up, true, "failed commit restores old upper row");
            AssertNearCell(fixedWorld.Storage, Vector3Int.zero, true, "failed commit restores old lower row");
            var emptyDraft = NewWorld(); UseCandidate(emptyDraft);
            AssertNearCell(emptyDraft, Vector3Int.up, false, "candidate takes precedence over live fixed rows");
            Candidate.SetValue(_terrain, null); environment.ThrowRecovery = false; environment.Prepare = null;
            Assert.That(fixedWorld.Destroy(new[] { bottom.Handle }, 2, 1, 0).RemovedEnemy, Is.EqualTo(1));
            AssertNearCell(fixedWorld.Storage, Vector3Int.up, false, "successful replacement retires old upper row");
            AssertNearCell(fixedWorld.Storage, Vector3Int.zero, true, "successful replacement exposes landed row");
        }

        void AssertNearCell(BlockWorld world, Vector3Int cell, bool expected, string context)
        {
            var center = BlockWorld.Center(cell);
            AssertCapsule(world, center, 0, .02f, context); AssertBox(world, center, Vector3.one * .02f, Quaternion.identity, context);
            Assert.That(_capsule(center, 0, .02f), Is.EqualTo(expected), context + " capsule witness");
            Assert.That(_box(center, Vector3.one * .02f, Quaternion.identity), Is.EqualTo(expected), context + " box witness");
        }

        void AssertCapsule(BlockWorld world, Vector3 center, float segment, float radius, string context, IReadOnlyList<Vector3Int> cells = null)
        {
            bool expected = LegacyCapsule(world, center, segment, radius);
            Vector3 extent = new(radius, radius + segment, radius);
            bool enumerated = EveryOccupied(cells ?? OccupiedCells(world), BlockWorld.CellAt(center - extent), BlockWorld.CellAt(center + extent),
                cell => ReferenceCapsule(cell, center, segment, radius));
            string diagnostic = $"{context}: center={center:R}, segment={segment:R}, radius={radius:R}";
            Assert.That(enumerated, Is.EqualTo(expected), "Independent occupied-cell oracle " + diagnostic);
            Assert.That(_capsule(center, segment, radius), Is.EqualTo(expected), "Original z/y/x grid oracle " + diagnostic);
        }

        void AssertBox(BlockWorld world, Vector3 center, Vector3 half, Quaternion rotation, string context, IReadOnlyList<Vector3Int> cells = null)
        {
            bool expected = LegacyBox(world, center, half, rotation);
            Vector3 extent = Abs(rotation * Vector3.right) * half.x + Abs(rotation * Vector3.up) * half.y + Abs(rotation * Vector3.forward) * half.z;
            bool enumerated = EveryOccupied(cells ?? OccupiedCells(world), BlockWorld.CellAt(center - extent), BlockWorld.CellAt(center + extent),
                cell => ReferenceBox(cell, center, half, rotation));
            string diagnostic = $"{context}: center={center:R}, half={half:R}, rotation={rotation:R}";
            Assert.That(enumerated, Is.EqualTo(expected), "Independent occupied-cell oracle " + diagnostic);
            Assert.That(_box(center, half, rotation), Is.EqualTo(expected), "Original z/y/x grid oracle " + diagnostic);
        }

        // Original broadphase and narrowphase, with no row lookup or production query helper.
        static bool LegacyCapsule(BlockWorld world, Vector3 center, float segment, float radius)
        {
            Vector3 extent = new(radius, radius + segment, radius);
            return LegacyGrid(world, BlockWorld.CellAt(center - extent), BlockWorld.CellAt(center + extent),
                cell => ReferenceCapsule(cell, center, segment, radius));
        }
        static bool LegacyBox(BlockWorld world, Vector3 center, Vector3 half, Quaternion rotation)
        {
            Vector3 extent = Abs(rotation * Vector3.right) * half.x + Abs(rotation * Vector3.up) * half.y + Abs(rotation * Vector3.forward) * half.z;
            return LegacyGrid(world, BlockWorld.CellAt(center - extent), BlockWorld.CellAt(center + extent),
                cell => ReferenceBox(cell, center, half, rotation));
        }
        static bool LegacyGrid(BlockWorld world, Vector3Int low, Vector3Int high, Func<Vector3Int, bool> intersects)
        {
            for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
            {
                var cell = new Vector3Int(x, y, z);
                if (world.TryGet(cell, out var block) && block.Occupied && intersects(cell)) return true;
            }
            return false;
        }
        static List<Vector3Int> OccupiedCells(BlockWorld world)
        {
            var result = new List<Vector3Int>();
            foreach (var pair in world.Chunks) for (int i = 0; i < pair.Value.Cells.Length; i++)
                if (pair.Value.Cells[i].Occupied) result.Add(BlockWorld.CellFromIndex(pair.Key, i));
            return result;
        }
        static bool EveryOccupied(IReadOnlyList<Vector3Int> cells, Vector3Int low, Vector3Int high, Func<Vector3Int, bool> intersects)
        {
            // Preserve the original inclusive broadphase membership; SAT's epsilon
            // alone may accept a tangent cube immediately outside that cell range.
            foreach (var cell in cells)
                if (cell.x >= low.x && cell.x <= high.x && cell.y >= low.y && cell.y <= high.y && cell.z >= low.z && cell.z <= high.z && intersects(cell)) return true;
            return false;
        }
        static bool ReferenceCapsule(Vector3Int cell, Vector3 center, float segment, float radius)
        {
            var bounds = new Bounds(BlockWorld.Center(cell), Vector3.one * BlockWorld.CellSize);
            bounds.Expand(Vector3.up * (segment * 2));
            return (center - bounds.ClosestPoint(center)).sqrMagnitude < radius * radius - .000001f;
        }
        static bool ReferenceBox(Vector3Int cell, Vector3 center, Vector3 extent, Quaternion rotation)
        {
            Vector3 half = Vector3.one * (BlockWorld.CellSize * .5f);
            Vector3 x = rotation * Vector3.right, y = rotation * Vector3.up, z = rotation * Vector3.forward;
            Vector3 delta = center - BlockWorld.Center(cell);
            Vector3 broad = Abs(x) * extent.x + Abs(y) * extent.y + Abs(z) * extent.z + half;
            if (Mathf.Abs(delta.x) > broad.x || Mathf.Abs(delta.y) > broad.y || Mathf.Abs(delta.z) > broad.z) return false;
            if (Separates(Vector3.right) || Separates(Vector3.up) || Separates(Vector3.forward) || Separates(x) || Separates(y) || Separates(z)) return false;
            if (Separates(Vector3.Cross(Vector3.right, x)) || Separates(Vector3.Cross(Vector3.right, y)) || Separates(Vector3.Cross(Vector3.right, z)) ||
                Separates(Vector3.Cross(Vector3.up, x)) || Separates(Vector3.Cross(Vector3.up, y)) || Separates(Vector3.Cross(Vector3.up, z)) ||
                Separates(Vector3.Cross(Vector3.forward, x)) || Separates(Vector3.Cross(Vector3.forward, y)) || Separates(Vector3.Cross(Vector3.forward, z))) return false;
            return true;
            bool Separates(Vector3 axis)
            {
                if (axis.sqrMagnitude < .00000001f) return false;
                float a = Vector3.Dot(half, Abs(axis));
                float b = extent.x * Mathf.Abs(Vector3.Dot(x, axis)) + extent.y * Mathf.Abs(Vector3.Dot(y, axis)) + extent.z * Mathf.Abs(Vector3.Dot(z, axis));
                return Mathf.Abs(Vector3.Dot(delta, axis)) > a + b + .000001f;
            }
        }
        static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        static Vector3 Next(System.Random random, Vector3 low, Vector3 high)
            => new(Mathf.Lerp(low.x, high.x, (float)random.NextDouble()), Mathf.Lerp(low.y, high.y, (float)random.NextDouble()), Mathf.Lerp(low.z, high.z, (float)random.NextDouble()));
    }
}
#endif
