#if UNITY_EDITOR
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class RescueBubbleTests
    {
        [Test] public void InitialOverlapWithNonConvexFloorMovesWholeSphereAboveMesh()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(1000, 10, 1000); Physics.SyncTransforms();
            try
            {
                Assert.That(floor.GetComponent<MeshCollider>().convex, Is.False);
                using var motor = new RescueBubbleMotor();
                var center = motor.ResolveOverlap(new Vector3(1000, 10.8f, 1000), 1.5f);
                Assert.That(center.y, Is.GreaterThanOrEqualTo(11.5f));
                Assert.That(Physics.CheckSphere(center, 1.5f, PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(floor); }
        }
        [Test] public void ZeroHealthBubbleCanMoveButIsNotDeadOrAlive()
        {
            var s = new PlayerSnapshot { LifeState = PlayerLifeState.Bubble, BubbleUntil = 10 };
            Assert.That(s.CanMove, Is.True); Assert.That(s.IsAlive, Is.False); Assert.That(s.IsDead, Is.False);
            Assert.That(RescueBubbleRules.Expired(s, 9.999), Is.False); Assert.That(RescueBubbleRules.Expired(s, 10), Is.True);
            Assert.That(RescueBubbleRules.Expired(s, 10.001), Is.True);
        }
        [Test] public void SphereContainsEveryCornerWithAtLeastPointTwoClearance()
        {
            var bounds = new Bounds(new Vector3(.3f, 1.2f, .6f), new Vector3(1.7f, 2.4f, 2.8f));
            float r = RescueBubbleRules.Radius(bounds, bounds.center.y, .01f);
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Assert.That(r - Vector3.Distance(corner, Vector3.up * bounds.center.y), Is.GreaterThanOrEqualTo(.19999f));
            }
        }
        [Test] public void InteractionRejectsExpiredStaleLifeDeadActorsAndDistantActors()
        {
            var actor = new PlayerSnapshot { Health = 100 };
            var target = new PlayerSnapshot { LifeState = PlayerLifeState.Bubble, Revision = 7, BubbleRadius = 2, BubbleUntil = 10 };
            Assert.That(RescueBubbleRules.Eligible(actor, target, 7, 9, 1, Vector3.right * 3), Is.True);
            Assert.That(RescueBubbleRules.Eligible(actor, target, 7, 10, 1, Vector3.zero), Is.False);
            Assert.That(RescueBubbleRules.Eligible(actor, target, 6, 9, 1, Vector3.zero), Is.False);
            Assert.That(RescueBubbleRules.Eligible(actor, target, 7, 9, 1, Vector3.right * 3.01f), Is.False);
            actor.Health = 0;
            Assert.That(RescueBubbleRules.Eligible(actor, target, 7, 9, 1, Vector3.zero), Is.False);
        }
        [Test] public void DownedContributionsSurviveTenSecondsAndOnlyOriginalKillerGetsCredit()
        {
            var ledger = new MatchCombatStats(); ledger.Reset(1);
            ledger.BeginLife(1, 1, 1); ledger.BeginLife(2, 1, 1); ledger.BeginLife(3, 2, 1);
            ledger.RecordDamage(3, 1, 2, 1, 20, false, 7, MatchPhase.Playing);
            ledger.RecordDamage(3, 1, 1, 1, 80, false, 10, MatchPhase.Playing); ledger.RecordDowned(3, 1, 10);
            Assert.That(ledger.Get(1).Kills + ledger.Get(3).Deaths, Is.Zero);
            ledger.RecordDeath(3, 1, 1, 20, MatchPhase.Playing); ledger.RecordDeath(3, 1, 2, 20, MatchPhase.Playing);
            Assert.That(ledger.Get(1).Kills, Is.EqualTo(1)); Assert.That(ledger.Get(2).Kills, Is.Zero);
            Assert.That(ledger.Get(2).Assists, Is.EqualTo(1)); Assert.That(ledger.Get(3).Deaths, Is.EqualTo(1));
        }
        [Test] public void RescueStartsNewLifeWithoutDeathAndClearsOldContributors()
        {
            var ledger = new MatchCombatStats(); ledger.Reset(1); ledger.BeginLife(1, 1, 1); ledger.BeginLife(2, 2, 1);
            ledger.RecordDamage(2, 1, 1, 1, 100, false, 1, MatchPhase.Playing); ledger.RecordDowned(2, 1, 1);
            ledger.BeginLife(2, 2, 2); ledger.RecordDeath(2, 1, 1, 2, MatchPhase.Playing);
            Assert.That(ledger.Get(1).Kills + ledger.Get(2).Deaths, Is.Zero);
        }
        [Test] public void BubbleAndInteractionWireRoundTrip()
        {
            var state = new PlayerSnapshot { LifeState = PlayerLifeState.Bubble, BubbleUntil = 123.25, BubbleRadius = 2.7f,
                BubbleCenterHeight = 1.1f, BubbleEvent = 9, BubbleResult = BubbleOutcome.Rescued, BubbleInkTeam = 2,
                BubbleEndedAt = 111.25, BubbleEndPosition = new Vector3(7, 8, 9), ConsumedInteract = 11 };
            using var writer = new FastBufferWriter(4096, Allocator.Temp);
            writer.WriteNetworkSerializable(state);
            using (var reader = new FastBufferReader(writer, Allocator.None))
            { reader.ReadNetworkSerializable(out PlayerSnapshot copy); Assert.That(copy.BubbleUntil, Is.EqualTo(state.BubbleUntil)); Assert.That(copy.BubbleRadius, Is.EqualTo(state.BubbleRadius)); Assert.That(copy.BubbleInkTeam, Is.EqualTo(2)); }
            var before = state; state.BubbleResult = BubbleOutcome.Expired; state.LifeState = PlayerLifeState.Dead; state.BubbleEvent++;
            writer.Seek(0); writer.Truncate(0); PlayerSnapshotDelta.WriteDelta(writer, state, before);
            using (var reader = new FastBufferReader(writer, Allocator.None)) { PlayerSnapshotDelta.ReadDelta(reader, ref before); Assert.That(before.BubbleResult, Is.EqualTo(BubbleOutcome.Expired)); Assert.That(before.IsDead, Is.True); }
            var frame = new PlayerInputFrame { InteractSequence = 12, InteractTarget = ulong.MaxValue - 1, InteractTargetLife = 14 };
            writer.Seek(0); writer.Truncate(0); writer.WriteNetworkSerializable(frame);
            using (var reader = new FastBufferReader(writer, Allocator.None))
            { reader.ReadNetworkSerializable(out PlayerInputFrame copy); Assert.That(copy.InteractSequence, Is.EqualTo(12)); Assert.That(copy.InteractTarget, Is.EqualTo(frame.InteractTarget)); Assert.That(copy.InteractTargetLife, Is.EqualTo(14)); }
        }
    }
}
#endif
