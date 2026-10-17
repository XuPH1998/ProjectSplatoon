#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Prepared source only: neither this file nor the standalone policy is in
    // an Assets test assembly. These tests are not Player timing evidence.
    public sealed class BlockPublicationPolicyContractTests
    {
        HistoricalBlockWorldHeightScope _historicalHeight;
        [SetUp] public void UseHistoricalWorldHeight() => _historicalHeight = new HistoricalBlockWorldHeightScope();
        [TearDown] public void RestoreWorldHeight() { _historicalHeight?.Dispose(); _historicalHeight = null; }

        [Test] public void CompatibilityKeepsEightKeysWhileApprovedGravityWorkLimitsIncrease()
        {
            var policy = BlockPublicationPolicy.CompatibilityEight();
            foreach (var operation in new[] { BlockPublicationOperation.OrdinaryAttack,
                BlockPublicationOperation.Destruction, BlockPublicationOperation.Gravity, BlockPublicationOperation.Metadata })
                Assert.That(policy.Capture(operation).MaximumChangedKeys, Is.EqualTo(8));
            Assert.That(policy.FrameKeys, Is.EqualTo(8));
            Assert.That(BlockPublicationPolicy.MaximumChecks, Is.EqualTo(4096));
            Assert.That(BlockPublicationPolicy.CommandChecksWithGravity, Is.EqualTo(3072));
            Assert.That(BlockPublicationPolicy.GravityChecksWithCommands, Is.EqualTo(4096));
            Assert.That(BlockPublicationPolicy.GravityChecksAlone, Is.EqualTo(8192));
            Assert.That(BlockPublicationPolicy.CommandMillisecondsAlone, Is.EqualTo(.75));
            Assert.That(BlockPublicationPolicy.CommandMillisecondsWithGravity, Is.EqualTo(.5));
            Assert.That(BlockPublicationPolicy.GravityMillisecondsAlone, Is.EqualTo(1.5));
            Assert.That(BlockPublicationPolicy.GravityMillisecondsWithCommands, Is.EqualTo(1.0));
            Assert.That(BlockPublicationPolicy.UploadMilliseconds, Is.EqualTo(1));
        }

        [Test] public void ExpandedAndMixedProfilesUseTheirCompleteMinimumEnvelope()
        {
            var policy = BlockPublicationPolicy.ExpandedUncalibrated();
            Assert.That(policy.Capture(BlockPublicationOperation.Destruction).MaximumChangedKeys, Is.EqualTo(32));
            Assert.That(policy.Capture(BlockPublicationOperation.Gravity).MaximumChangedKeys, Is.EqualTo(32));
            Assert.That(policy.Capture(BlockPublicationOperation.Destruction | BlockPublicationOperation.Gravity).MaximumChangedKeys, Is.EqualTo(32));
            Assert.That(policy.Capture(BlockPublicationOperation.OrdinaryAttack | BlockPublicationOperation.Destruction
                | BlockPublicationOperation.Gravity).MaximumChangedKeys, Is.EqualTo(8));
            Assert.That(policy.Capture(BlockPublicationOperation.Metadata | BlockPublicationOperation.Gravity).MaximumChangedKeys, Is.EqualTo(8));
            // Ordinary enemy removal includes OrdinaryAttack as well as Destruction.
            Assert.That(policy.Capture(BlockPublicationOperation.OrdinaryAttack | BlockPublicationOperation.Destruction).MaximumChangedKeys, Is.EqualTo(8));
        }

        [Test] public void EveryNonemptyKnownMaskHasAWindowThatFitsItsFreshFrame()
        {
            var policy = new BlockPublicationPolicy(3, 8, 24, 32, 16, 32, 3);
            for (int mask = 1; mask < 16; mask++)
            {
                int oracle = 32;
                if ((mask & 1) != 0) oracle = Math.Min(oracle, 8);
                if ((mask & 2) != 0) oracle = Math.Min(oracle, 24);
                if ((mask & 4) != 0) oracle = Math.Min(oracle, 32);
                if ((mask & 8) != 0) oracle = Math.Min(oracle, 16);
                var window = policy.Capture((BlockPublicationOperation)mask);
                Assert.That(window.MaximumChangedKeys, Is.EqualTo(oracle), "mask " + mask);
                Assert.That(window.MaximumChangedKeys, Is.LessThanOrEqualTo(window.Policy.FrameKeys));
            }
        }

        [Test] public void PolicyReplacementDoesNotChangeAnExistingCapturedWindow()
        {
            var selected = BlockPublicationPolicy.ExpandedUncalibrated(10);
            var captured = selected.Capture(BlockPublicationOperation.Gravity);
            selected = BlockPublicationPolicy.CompatibilityEight(11);
            Assert.That(captured.Policy.Version, Is.EqualTo(10));
            Assert.That(captured.MaximumChangedKeys, Is.EqualTo(32));
            Assert.That(captured.Policy.FrameKeys, Is.EqualTo(32));
            Assert.That(selected.Capture(BlockPublicationOperation.Gravity).MaximumChangedKeys, Is.EqualTo(8));
            foreach (var property in typeof(BlockPublicationPolicy).GetProperties()) Assert.That(property.SetMethod, Is.Null, property.Name);
            foreach (var property in typeof(BlockPublicationWindow).GetProperties()) Assert.That(property.SetMethod, Is.Null, property.Name);
        }

        [Test] public void InvalidPoliciesCannotCreatePermanentlyOversizedWindows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BlockPublicationPolicy(0, 8, 8, 8, 8, 8, 3));
            for (int operation = 0; operation < 4; operation++)
            {
                var caps = new[] { 8, 8, 8, 8 }; caps[operation] = 7;
                Assert.Throws<ArgumentOutOfRangeException>(() => new BlockPublicationPolicy(1, caps[0], caps[1], caps[2], caps[3], 8, 3));
                caps[operation] = 32;
                Assert.Throws<ArgumentOutOfRangeException>(() => new BlockPublicationPolicy(1, caps[0], caps[1], caps[2], caps[3], 31, 3));
            }
            foreach (double budget in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(() => new BlockPublicationPolicy(1, 8, 8, 8, 8, 8, budget));
            var policy = BlockPublicationPolicy.CompatibilityEight();
            foreach (int mask in new[] { 0, 16, 17, -1 })
                Assert.Throws<ArgumentOutOfRangeException>(() => policy.Capture((BlockPublicationOperation)mask));
        }

        [TestCase(-2, -3, -4)]
        [TestCase(2, 3, 4)]
        public void All4096LocalCellsUseAnIndependentSixNeighbourHaloOracle(int cx, int cy, int cz)
        {
            var window = BlockPublicationPolicy.CompatibilityEight().Capture(BlockPublicationOperation.OrdinaryAttack);
            for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            {
                var cell = new Vector3Int(cx * 16 + x, cy * 16 + y, cz * 16 + z);
                var changed = FarKeys(6);
                if ((x & 1) == 0) changed.Add(new Vector3Int(cx - 1, cy, cz));
                if ((y & 1) == 0) changed.Add(new Vector3Int(cx, cy, cz));
                var before = changed.ToArray();
                var union = new HashSet<Vector3Int>(changed); union.UnionWith(HaloOracle(cell));
                bool expected = union.Count <= 8;
                Assert.That(window.CanIncludeCell(changed, cell), Is.EqualTo(expected), "cell " + cell);
                CollectionAssert.AreEquivalent(before, changed, "admission mutated the live key set at " + cell);
            }
        }

        [Test] public void ExactFullUnionAdmissionDoesNotModifyEitherCapturedCollection()
        {
            var window = BlockPublicationPolicy.CompatibilityEight().Capture(BlockPublicationOperation.Destruction);
            var changed = FarKeys(7); var before = changed.ToArray();
            var ninth = new Vector3Int(300, 400, 500);
            var eighth = new Vector3Int(301, 400, 500);
            var footprint = new[] { eighth, eighth, ninth, eighth }; var captured = footprint.ToArray();
            Assert.That(window.CanIncludeFootprint(changed, new[] { eighth, eighth }), Is.True);
            Assert.That(window.CanIncludeFootprint(changed, footprint), Is.False);
            CollectionAssert.AreEquivalent(before, changed); CollectionAssert.AreEqual(captured, footprint);
            // An already oversized stage is rejected even if this cell adds no keys.
            changed.UnionWith(new[] { eighth, ninth });
            Assert.That(window.CanIncludeCell(changed, new Vector3Int(0, 0, 0)), Is.False);
            Assert.That(window.CanIncludeFootprint(changed, Array.Empty<Vector3Int>()), Is.False);
        }

        [Test] public void CrossChunkNegativeColumnKeepsEverySourceAndFaceHalo()
        {
            var footprint = new HashSet<Vector3Int>();
            for (int y = -16; y <= 31; y++) footprint.UnionWith(HaloOracle(new Vector3Int(-1, y, -1)));
            Assert.That(footprint.Count, Is.EqualTo(11));
            Assert.That(footprint, Does.Contain(new Vector3Int(-1, -2, -1)));
            Assert.That(footprint, Does.Contain(new Vector3Int(-1, 2, -1)));
            Assert.That(footprint, Does.Contain(new Vector3Int(0, 0, -1)));
            Assert.That(footprint, Does.Contain(new Vector3Int(-1, 0, 0)));
            var before = footprint.ToArray();
            Assert.That(BlockPublicationPolicy.CompatibilityEight().Capture(BlockPublicationOperation.Gravity)
                .CanIncludeFootprint(new HashSet<Vector3Int>(), footprint), Is.False);
            Assert.That(BlockPublicationPolicy.ExpandedUncalibrated().Capture(BlockPublicationOperation.Gravity)
                .CanIncludeFootprint(new HashSet<Vector3Int>(), footprint), Is.True);
            CollectionAssert.AreEquivalent(before, footprint);
        }

        [TestCase(320, 28900, 80)]
        [TestCase(231, 20000, 56)]
        public void OriginalHundredColumnFootprintsRemainCompleteAndNeedMultipleWindows(int topExclusive, int expectedCells, int expectedKeys)
        {
            var footprint = new HashSet<Vector3Int>(); int cells = 0;
            for (int z = -5; z < 5; z++) for (int x = -5; x < 5; x++) for (int y = 31; y < topExclusive; y++)
            { footprint.UnionWith(HaloOracle(new Vector3Int(x, y, z))); cells++; }
            Assert.That(cells, Is.EqualTo(expectedCells)); Assert.That(footprint.Count, Is.EqualTo(expectedKeys));
            foreach (var policy in new[] { BlockPublicationPolicy.CompatibilityEight(), BlockPublicationPolicy.ExpandedUncalibrated() })
                Assert.That(policy.Capture(BlockPublicationOperation.Gravity).CanIncludeFootprint(new HashSet<Vector3Int>(), footprint), Is.False);
            // This is exact integer geometry, not PhysX support sampling or a 500ms measurement.
            Assert.That(footprint.Count, Is.EqualTo(expectedKeys));
        }

        [Test] public void NullAdmissionInputsAreRejected()
        {
            var window = BlockPublicationPolicy.CompatibilityEight().Capture(BlockPublicationOperation.Gravity);
            Assert.Throws<ArgumentNullException>(() => window.CanIncludeCell(null, Vector3Int.zero));
            Assert.Throws<ArgumentNullException>(() => window.CanIncludeFootprint(null, Array.Empty<Vector3Int>()));
            Assert.Throws<ArgumentNullException>(() => window.CanIncludeFootprint(new HashSet<Vector3Int>(), null));
        }

        static HashSet<Vector3Int> FarKeys(int count)
        {
            var keys = new HashSet<Vector3Int>();
            for (int i = 0; i < count; i++) keys.Add(new Vector3Int(100 + i, 100, 100));
            return keys;
        }
        // Independent oracle: floor division and neighbouring world cells.
        // Deliberately does not call BlockWorld.ChunkAt/ChunkOrigin or policy helpers.
        static Vector3Int OracleKey(Vector3Int cell) => new((int)Math.Floor(cell.x / 16d),
            (int)Math.Floor(cell.y / 16d), (int)Math.Floor(cell.z / 16d));
        static HashSet<Vector3Int> HaloOracle(Vector3Int cell)
        {
            var keys = new HashSet<Vector3Int> { OracleKey(cell) };
            foreach (var offset in new[] { Vector3Int.left, Vector3Int.right, Vector3Int.down, Vector3Int.up,
                new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1) }) keys.Add(OracleKey(cell + offset));
            return keys;
        }
    }
}
#endif
