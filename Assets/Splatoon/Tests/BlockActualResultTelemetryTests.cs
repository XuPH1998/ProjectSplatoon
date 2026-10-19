#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Tests
{
    [Category("BlockFixedGrid")]
    public sealed class BlockActualResultTelemetryTests
    {
        sealed class Environment : IBlockFixedGridEnvironment, IBlockFixedGridCommitGuard, IBlockFixedGridRecoveryRollback
        {
            internal bool Reject, ThrowRecovery;
            internal int Recoveries;
            public bool TryGetMapSupport(Vector3Int from, out BlockFixedGridMapSupport support)
            { support = new BlockFixedGridMapSupport(-7, 0, Key(from.x, from.z)); return true; }
            public bool PlacementAllowed(Vector3Int cell, BlockFixedGridMapSupport support, bool embeddedBase, bool relocating, out string reason)
            { reason = null; return true; }
            public bool Visible(Vector3 from, Vector3 to) => true;
            public bool TryPrepareCommit(BlockFixedGridCommitPreview preview, out string reason)
            { reason = "test guard rejection"; return !Reject; }
            public void CommitPreparedRecovery()
            { Recoveries++; if (ThrowRecovery) throw new InvalidOperationException("test recovery rollback"); }
            public void RollbackPreparedRecovery() { Recoveries--; }
        }
        readonly List<BlockAttackObservation.ActualResult> _results = new();
        readonly List<(uint revision, uint surfaceRevision)> _observedVersions = new();
        readonly List<BlockAttackObservation.Payment> _payments = new();
        BlockFixedGridWorld _world;
        Environment _environment;
        Action<BlockAttackObservation.ActualResult> _throwing;
        Action<BlockAttackObservation.Collision> _collision;
        cfg.Tables _previousTables;
        static BlockSurfaceKey Key(int x = 0, int z = 0) => new(11, x, z);

        [OneTimeSetUp] public void LoadConfiguration()
        {
            _previousTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }
        [OneTimeTearDown] public void RestoreConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _previousTables);
        [SetUp] public void Setup()
        {
            BlockAttackService.Clear(); _results.Clear(); _observedVersions.Clear(); _payments.Clear();
            _environment = new Environment();
            var surface = new BlockSurfaceWorld(7); var squares = new List<Vector2Int>();
            for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) squares.Add(new Vector2Int(x, z));
            surface.RegisterFace(11, Vector3.zero, Vector3.right, Vector3.forward, squares);
            _world = new BlockFixedGridWorld(new BlockWorld(new Vector2(30, 30), 7, -8), surface, _environment);
            BlockAttackObservation.ActualResultCommitted += Capture;
            BlockAttackObservation.PaymentAccepted += Paid;
        }
        [TearDown] public void Cleanup()
        {
            BlockAttackObservation.ActualResultCommitted -= Capture;
            BlockAttackObservation.PaymentAccepted -= Paid;
            if (_throwing != null) BlockAttackObservation.ActualResultCommitted -= _throwing;
            if (_collision != null) BlockAttackObservation.CollisionEffective -= _collision;
            _throwing = null; _collision = null; BlockAttackService.Clear();
        }
        void Capture(BlockAttackObservation.ActualResult result)
        {
            _observedVersions.Add((_world.Revision, _world.Surfaces.Revision));
            _results.Add(result);
        }
        void Paid(BlockAttackObservation.Payment payment) => _payments.Add(payment);
        static BlockAttackToken Token(byte team = 1) => new(new PaintCredit(91, team, 7, 3, PaintAttackKind.Main, true), 0, 512, 16, 2);
        BlockFixedGridState Add(int y, byte team = 1, int x = 0)
        {
            _world.Surfaces.TrySet(Key(x), team, out _);
            Assert.That(_world.Build(new[] { new Vector3Int(x, y, 0) }, team, 1).Added, Is.EqualTo(1), _world.LastFailure);
            Assert.That(_world.TryGet(new Vector3Int(x, y, 0), out var state), Is.True); return state;
        }
        BlockAttackObservation.ActualResult Last
        {
            get
            {
                Assert.That(_observedVersions[^1].revision, Is.EqualTo(_results[^1].Revision));
                Assert.That(_observedVersions[^1].surfaceRevision, Is.EqualTo(_results[^1].SurfaceRevision));
                return _results[^1];
            }
        }

        [Test] public void BlankPaintAndEnemyReplacementEachPublishOneFinalEffect()
        {
            using var scope = BlockAttackObservation.BeginActualResult(Token(), 17, 2, BlockAttackObservation.ActualOperation.Build);
            _world.Paint(new[] { Key() }, 1, 1);
            Assert.That(Last.Counts.NewPaint, Is.EqualTo(1)); Assert.That(Last.Counts.EnemyPaintReplaced, Is.Zero);
            Assert.That(Last.Entity, Is.EqualTo(17)); Assert.That(Last.Ordinal, Is.EqualTo(2));
            _world.Paint(new[] { Key() }, 2, 1);
            Assert.That(Last.Counts.NewPaint, Is.Zero); Assert.That(Last.Counts.EnemyPaintReplaced, Is.EqualTo(1));
            Assert.That(Last.Counts.EnemyPaintRemoved, Is.Zero); Assert.That(Last.Counts.NewFixed, Is.Zero);
            Assert.That(_world.BlueCount, Is.EqualTo(1)); Assert.That(_world.PinkCount, Is.Zero);
        }
        [Test] public void FixedCreationAndEnemyDeletionUseDifferentActualCounters()
        {
            var state = Add(0, 2); Assert.That(Last.Counts.NewFixed, Is.EqualTo(1));
            using var scope = BlockAttackObservation.BeginActualResult(Token(), 22, 1, BlockAttackObservation.ActualOperation.Direct);
            _world.Destroy(new[] { state.Handle }, 1);
            Assert.That(Last.Operation, Is.EqualTo(BlockAttackObservation.ActualOperation.Direct));
            Assert.That(Last.Counts.EnemyFixedRemoved, Is.EqualTo(1)); Assert.That(Last.Counts.NewFixed, Is.Zero);
            Assert.That(_world.Count, Is.Zero);
        }
        [Test] public void BlastClassifiesBothTeamsAndBothRepresentations()
        {
            var friendly = Add(0, 1, -1); var enemy = Add(0, 2, 1);
            using var scope = BlockAttackObservation.BeginActualResult(Token(), 23, 3, BlockAttackObservation.ActualOperation.Blast);
            using var tx = _world.BeginMutation();
            tx.Destroy(new[] { friendly.Handle, enemy.Handle }, 1);
            tx.DestroyPaint(new[] { Key(-1), Key(1) }, 1);
            Assert.That(tx.TryCommit(out _, out var error), Is.True, error);
            var counts = Last.Counts;
            Assert.That(counts.EnemyFixedRemoved, Is.EqualTo(1)); Assert.That(counts.FriendlyFixedRemoved, Is.EqualTo(1));
            Assert.That(counts.EnemyPaintRemoved, Is.EqualTo(1)); Assert.That(counts.FriendlyPaintRemoved, Is.EqualTo(1));
            Assert.That(counts.NewFixed + counts.NewPaint + counts.EnemyPaintReplaced, Is.Zero);
        }
        [Test] public void IntermediatePaintWritesCannotInflateCommittedWork()
        {
            using var tx = _world.BeginMutation();
            tx.Paint(new[] { Key() }, 1, 1); tx.Paint(new[] { Key() }, 2, 1);
            Assert.That(tx.TryCommit(out _, out var error), Is.True, error);
            Assert.That(Last.Counts.NewPaint, Is.EqualTo(1)); Assert.That(Last.Counts.EnemyPaintReplaced, Is.Zero);
            Assert.That(_world.BlueCount, Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)] public void GuardRejectionAndRecoveryRollbackDoNotPublishActualResults(bool recoveryFailure)
        {
            _environment.Reject = !recoveryFailure; _environment.ThrowRecovery = recoveryFailure;
            uint revision = _world.Revision, surfaceRevision = _world.Surfaces.Revision;
            using var tx = _world.BeginMutation(); tx.Paint(new[] { Key() }, 1, 1);
            Assert.That(tx.TryCommit(out _, out _), Is.False);
            Assert.That(_results, Is.Empty); Assert.That(_world.Revision, Is.EqualTo(revision));
            Assert.That(_world.Surfaces.Revision, Is.EqualTo(surfaceRevision)); Assert.That(_world.PinkCount, Is.Zero);
            Assert.That(_environment.Recoveries, Is.Zero);
        }
        [Test] public void ObserverExceptionCannotInvalidateSuccessfulGameplayCommit()
        {
            int faults = BlockAttackObservation.ObserverFaultCount;
            _throwing = _ => throw new InvalidOperationException("test observer failure");
            BlockAttackObservation.ActualResultCommitted += _throwing;
            var result = _world.Paint(new[] { Key() }, 1, 1);
            Assert.That(result.Added, Is.EqualTo(1)); Assert.That(_world.PinkCount, Is.EqualTo(1));
            Assert.That(_world.LastFailure, Is.Null); Assert.That(_results.Count, Is.EqualTo(1));
            Assert.That(BlockAttackObservation.ObserverFaultCount, Is.EqualTo(faults + 1));
        }
        [TestCase(false)] [TestCase(true)] public void RestackingConversionAndRelocationNeverCountAsAnotherPaidAddition(bool conversion)
        {
            var lower = Add(0); var upper = Add(1, conversion ? (byte)2 : (byte)1);
            _world.Surfaces.TrySet(Key(), 1, out _); _results.Clear();
            using var scope = BlockAttackObservation.BeginActualResult(Token(2), 24, 4, BlockAttackObservation.ActualOperation.Direct);
            _world.Destroy(new[] { lower.Handle }, 2);
            var counts = Last.Counts;
            Assert.That(counts.NewFixed + counts.NewPaint + counts.EnemyPaintReplaced, Is.Zero);
            Assert.That(counts.EnemyFixedRemoved, Is.EqualTo(1)); Assert.That(counts.FriendlyFixedRemoved, Is.Zero);
            Assert.That(counts.Converted, Is.EqualTo(conversion ? 1 : 0)); Assert.That(counts.Relocated, Is.EqualTo(conversion ? 0 : 1));
            Assert.That(_world.TryGet(upper.Handle, out var relocated), Is.EqualTo(!conversion));
            if (!conversion) Assert.That(relocated.Cell.y, Is.Zero);
        }
        [Test] public void SuccessfulNoChangeIsExplicitAndKeepsTheCommittedVersion()
        {
            _world.Paint(new[] { Key() }, 1, 1); uint revision = _world.Revision;
            _world.Paint(new[] { Key() }, 1, 1);
            Assert.That(Last.HadChanges, Is.False); Assert.That(Last.Revision, Is.EqualTo(revision));
            Assert.That(Last.Counts.NewPaint + Last.Counts.NewFixed + Last.Counts.EnemyPaintReplaced, Is.Zero);
        }
        [Test] public void PaymentDeduplicationAndDelayedResultRetainOriginalLifeAndAction()
        {
            var state = new PlayerSnapshot { HeroId = 1, SubWeaponId = 1, Team = 1, Revision = 9, HeroRevision = 3,
                ShotSequence = 71, FireBurstSequence = 71, LastShotCharge = 1 };
            var weapon = GameplayConfig.GetWeapon(1);
            var token = BlockAttackService.Main(91, state, 7, weapon);
            Assert.That(BlockAttackService.Main(91, state, 7, weapon), Is.SameAs(token));
            Assert.That(_payments.Count, Is.EqualTo(1)); Assert.That(_payments[0].Material, Is.GreaterThan(0));
            state.Revision = 10; state.HeroRevision = 4; state.ShotSequence = state.FireBurstSequence = 72;
            BlockAttackService.Main(91, state, 7, weapon);
            using var scope = BlockAttackObservation.BeginActualResult(token, 25, 6, BlockAttackObservation.ActualOperation.Build);
            _world.Paint(new[] { Key() }, 1, 1);
            Assert.That(Last.Source.Round, Is.EqualTo(7)); Assert.That(Last.Source.Owner, Is.EqualTo(91));
            Assert.That(Last.Source.Life, Is.EqualTo(9)); Assert.That(Last.Source.Equipment, Is.EqualTo(3));
            Assert.That(Last.Source.Action, Is.EqualTo(71)); Assert.That(Last.Source.Kind, Is.EqualTo(PaintAttackKind.Main));
        }
        [Test] public void ContactIsConsumedOnceAndEachAuxiliaryStartsAtItsOwnEntry()
        {
            _collision = _ => { }; BlockAttackObservation.CollisionEffective += _collision;
            var token = Token();
            using (BlockAttackObservation.BeginContact(token, 31))
            { Assert.That(BlockAttackObservation.ContactStamp(token, 31), Is.GreaterThan(0)); Assert.That(BlockAttackObservation.ContactStamp(token, 31), Is.Zero); }
            using (BlockAttackObservation.BeginContact(token, 32)) { }
            Assert.That(BlockAttackObservation.ContactStamp(token, 32), Is.Zero, "A non-block/stale resolve must end its unused contact.");
            long firstAux = BlockAttackObservation.StartEffect(token, 31), nextAux = BlockAttackObservation.StartEffect(token, 31);
            Assert.That(firstAux, Is.GreaterThan(0)); Assert.That(nextAux, Is.GreaterThanOrEqualTo(firstAux));
            using (BlockAttackObservation.BeginContact(token, 33)) { BlockAttackObservation.Clear(); Assert.That(BlockAttackObservation.ContactStamp(token, 33), Is.Zero); }
        }
        [Test] public void ActualAndCollisionRetainTheRealContactAndAuxiliaryClassification()
        {
            var token = BlockAttackService.Main(91, new PlayerSnapshot { HeroId = 1, SubWeaponId = 1, Team = 1,
                Revision = 9, HeroRevision = 3, ShotSequence = 90, FireBurstSequence = 90 }, 7, GameplayConfig.GetWeapon(1));
            BlockAttackObservation.Collision? collision = null;
            _collision = sample => collision = sample; BlockAttackObservation.CollisionEffective += _collision;
            using (BlockAttackObservation.BeginContact(token, 44))
            {
                long stamp = BlockAttackObservation.StartEffect(token, 44, out bool hadContact);
                using var scope = BlockAttackObservation.BeginActualResult(token, 44, 8, BlockAttackObservation.ActualOperation.Build,
                    false, stamp, hadContact);
                _world.Paint(new[] { Key() }, 1, 1);
                BlockAttackObservation.Effective(token, 44, "fixed", 1, stamp, 8, false, hadContact);
            }
            Assert.That(Last.HadContact, Is.True); Assert.That(Last.Auxiliary, Is.False);
            Assert.That(Last.Source.Kind, Is.EqualTo(PaintAttackKind.Main)); Assert.That(Last.Milliseconds, Is.GreaterThanOrEqualTo(0));
            Assert.That(collision.HasValue, Is.True); Assert.That(collision.Value.Source.Life, Is.EqualTo(9));
            Assert.That(collision.Value.HadContact, Is.True); Assert.That(collision.Value.Ordinal, Is.EqualTo(8));
            long auxiliary = BlockAttackObservation.StartEffect(token, 44, out bool auxiliaryContact);
            using (BlockAttackObservation.BeginActualResult(token, 44, 9, BlockAttackObservation.ActualOperation.Build,
                true, auxiliary, auxiliaryContact)) _world.Paint(new[] { Key(1) }, 1, 1);
            Assert.That(Last.HadContact, Is.False); Assert.That(Last.Auxiliary, Is.True); Assert.That(Last.Ordinal, Is.EqualTo(9));
        }
    }
}
#endif
