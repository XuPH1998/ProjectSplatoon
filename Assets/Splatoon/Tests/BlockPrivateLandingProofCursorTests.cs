#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    // Focused real Terrain proof-bridge contracts. These do not substitute for
    // full authority publication, Player contact, LAN or performance acceptance.
    public sealed class BlockPrivateLandingProofCursorTests
    {
        const double SourceTime = 10.1;
        static readonly Vector3Int FirstBottom = new(2, 80, 2), SecondBottom = new(4, 80, 2);
        static readonly MethodInfo AdvanceProof = typeof(BlockTerrain).GetMethod("AdvancePrivateLandingProof", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo RetainedMotion = typeof(FallingCohort).GetField("RetainedSupportMotion", BindingFlags.Instance | BindingFlags.NonPublic);
        GameObject _root;
        BlockTerrain _terrain;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Independent private landing proof cursor contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.Initialize(null);
            Assert.That(AdvanceProof, Is.Not.Null);
            Assert.That(RetainedMotion, Is.Not.Null);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ConsecutiveStableHitProofsOfFiveThenThreeCellsCannotReuseTheOldValidationCursor()
        {
            var source = _terrain.World;
            var first = BuildParent(source, FirstBottom);
            var second = BuildParent(source, SecondBottom);
            source.Set(FirstBottom + Vector3Int.down * 3, BlockCell.Create(1));
            source.Set(SecondBottom + Vector3Int.down * 3, BlockCell.Create(1));
            PublishParents(source);
            DamageParent(source, first, 5);
            DamageParent(source, second, 3);
            byte[] before = source.EncodeSnapshot();

            using var firstPreparation = WaitForProof(source, first, 5);
            Assert.That(firstPreparation.LandingProofRequest.SourceTopExclusive - FirstBottom.y, Is.EqualTo(5));
            CompleteBridge(firstPreparation);
            var firstNext = FinishPrivateWorld(source, firstPreparation);
            Assert.That(firstPreparation.Children[0].LandingOffset, Is.EqualTo(.2f).Within(1e-6));
            Assert.That(firstNext.Cohorts.ContainsKey(first.Id), Is.False);

            // Deliberately do not Abort/Reset/QueueSeed or recreate Terrain.
            // The prior completed validation cursor was five, greater than this span.
            using var secondPreparation = WaitForProof(source, second, 3);
            Assert.That(secondPreparation.LandingProofRequest.SourceTopExclusive - SecondBottom.y, Is.EqualTo(3));
            CompleteBridge(secondPreparation);
            var secondNext = FinishPrivateWorld(source, secondPreparation);
            Assert.That(secondPreparation.Children[0].LandingOffset, Is.EqualTo(.2f).Within(1e-6));
            Assert.That(secondNext.Cohorts.ContainsKey(second.Id), Is.False);
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            Assert.That(_terrain.World, Is.SameAs(source), "focused bridge preparation cannot publish either private World");
            yield return null;
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ASecondLongerProofMustReadItsValidationPrefixAndRejectAnUpwardOverlapResolution()
        {
            var source = _terrain.World;
            var first = BuildParent(source, FirstBottom);
            var second = BuildParent(source, SecondBottom);
            source.Set(FirstBottom + Vector3Int.down * 3, BlockCell.Create(1));
            source.Set(SecondBottom + Vector3Int.down * 3, BlockCell.Create(1));
            // An explicit overlap diagnostic: the obstacle lies above the downward
            // ray/scan origin, in the first five proposed landing destinations.
            // A previous cursor of five would skip it in a seven-cell proof.
            source.Set(SecondBottom + Vector3Int.up, BlockCell.Create(1));
            PublishParents(source);
            DamageParent(source, first, 5);
            DamageParent(source, second, 7);
            byte[] before = source.EncodeSnapshot();

            using var firstPreparation = WaitForProof(source, first, 5);
            CompleteBridge(firstPreparation);
            FinishPrivateWorld(source, firstPreparation);

            using var secondPreparation = WaitForProof(source, second, 7);
            var exception = Assert.Throws<InvalidOperationException>(() => CompleteBridge(secondPreparation));
            StringAssert.Contains("upward", exception.Message);
            Assert.That(secondPreparation.WaitingForLandingProof, Is.True);
            Assert.That(secondPreparation.Ready, Is.False);
            Assert.Throws<InvalidOperationException>(() => secondPreparation.TakePreparedWorld());
            CollectionAssert.AreEqual(before, source.EncodeSnapshot());
            Assert.That(_terrain.World, Is.SameAs(source));
            yield return null;
        }

        static FallingCohort BuildParent(BlockWorld source, Vector3Int bottom)
        {
            for (int i = 0; i < 16; i++) Assert.That(source.Set(bottom + Vector3Int.up * i, BlockCell.Create(2)), Is.True);
            var parent = source.BeginCohort(bottom, 16);
            source.DetachCohortPiece(parent.Id, 16, 10, out _);
            RetainedMotion.SetValue(parent, Array.CreateInstance(RetainedMotion.FieldType.GetElementType(), 399));
            // This modeled diagnostic begins with a legal baseline-ground
            // cap; stale-proof work is requested after initial publication.
            parent.LandingKnown = true; parent.LandingOffset = bottom.y * BlockWorld.CellSize;
            return source.Cohorts[parent.Id];
        }

        static void DamageParent(BlockWorld source, FallingCohort parent, int index)
        {
            var run = source.Falling[source.Cohorts[parent.Id].MemberIds[0]];
            Assert.That(source.RemoveFallingCell(run.Id, index, out _, out _), Is.True);
        }

        static void PublishParents(BlockWorld source)
        {
            source.PreparePendingCohortsForPublication(out _);
            source.Seal(source.Revision + 1, source.RevisionKeys.ToArray());
            source.ActivateRebasePendingCohorts(10);
            foreach (var parent in source.Cohorts.Values) parent.NeedsLandingProof = true;
        }

        static FallingPrivateReplacement WaitForProof(BlockWorld source, FallingCohort parent, int firstSpan)
        {
            var run = source.Falling[parent.MemberIds[0]];
            var spans = new[]
            {
                new FallingCohortSpan(run.Bottom, run.Bottom.y + firstSpan, new[] { new FallingCohortPiece(run.Id, 0, firstSpan) }),
                new FallingCohortSpan(run.Bottom + Vector3Int.up * (firstSpan + 1), run.Bottom.y + 16,
                    new[] { new FallingCohortPiece(run.Id, firstSpan + 1, 15 - firstSpan) })
            };
            var membership = source.BeginCohortReplacement(parent.Id, spans, out _);
            for (int step = 0; step < 2000 && !membership.Ready; step++)
            {
                membership.Step(source, 1024, out int work);
                Assert.That(work, Is.InRange(1, 1024));
                Assert.That(membership.Failed, Is.False);
            }
            Assert.That(membership.Ready, Is.True);
            var preparation = source.BeginPrivateReplacement(membership, SourceTime);
            for (int step = 0; step < 2000 && !preparation.WaitingForLandingProof; step++)
            {
                preparation.Step(source, 1024, out int work);
                Assert.That(work, Is.InRange(1, 1024));
                Assert.That(preparation.Failed, Is.False);
                Assert.That(preparation.Ready, Is.False);
            }
            Assert.That(preparation.WaitingForLandingProof, Is.True);
            Assert.That(preparation.LandingProofRequest.LiveLowerId, Is.Zero);
            return preparation;
        }

        void CompleteBridge(FallingPrivateReplacement preparation)
        {
            for (int stage = 0; stage < 128 && preparation.WaitingForLandingProof; stage++)
            {
                // Generous test clock isolates the cursor contract; this is not a
                // production wall-time assertion. Admission stays at 1,024 checks.
                object[] arguments = { preparation, 0, 1024, Stopwatch.StartNew(), 1000d };
                try { AdvanceProof.Invoke(_terrain, arguments); }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                { throw exception.InnerException; }
                Assert.That((int)arguments[1], Is.InRange(1, 1024));
            }
            Assert.That(preparation.WaitingForLandingProof, Is.False, "a prior longer proof must not leave this transaction in a zero-work private lock");
        }

        BlockWorld FinishPrivateWorld(BlockWorld source, FallingPrivateReplacement preparation)
        {
            for (int step = 0; step < 2000 && !preparation.Ready; step++)
            {
                if (preparation.WaitingForLandingProof) CompleteBridge(preparation);
                preparation.Step(source, 1024, out int work);
                Assert.That(work, Is.InRange(1, 1024));
                Assert.That(preparation.Failed, Is.False);
            }
            Assert.That(preparation.Ready, Is.True);
            return preparation.TakePreparedWorld();
        }
    }
}
#endif
