#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
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
    // All gameplay transitions use real build/destroy/Tick. Reflection registers
    // the map collider and reads scheduler/pending/view state; it never edits it.
    public sealed class BlockGravityBarrierPublicationFailureTests
    {
        const double Epoch = 10, InversionAt = 10.25;
        const int PumpLimit = 3000;
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly Vector3Int MainFloor = new(2, 0, 2), SideFloor = new(12, 0, 2);
        static readonly Vector3Int PinkSource = MainFloor + Vector3Int.up * 3;
        GameObject _root, _mapFloor;
        BlockTerrain _terrain;
        ulong _mainCohort, _mainRun;
        int _mainDestroyCallbacks, _pinkBuildCallbacks, _sideDestroyCallbacks, _sourceBlueCount;
        bool _sideRemovalPublished, _sideCommandAdmitted;
        BlockMutationResult _sideResult, _sideCommittedAtWitness;
        Action<BlockMutationResult> _sideCompletion;
        readonly List<NativeViewSnapshot> _sourceViews = new();
        sealed class NativeViewSnapshot
        {
            public Vector3Int Key;
            public object View;
            public GameObject Object;
            public MeshCollider Collider;
            public Mesh CollisionMesh, RenderMesh, SharedMesh;
            public bool Enabled, ActiveSelf, ActiveInHierarchy;
        }
        object _pendingWitness;
        BlockWorld _source;
        byte[] _sourceBytes;
        long _failedBefore, _invalidationsBefore;
        uint _sourceRevision;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            _root = new GameObject("Committed inversion barrier rejected-publication contract");
            _terrain = _root.AddComponent<BlockTerrain>();
            _terrain.PublicationPolicy = BlockPublicationPolicy.CompatibilityEight(); _terrain.Initialize(null);
            _mapFloor = new GameObject("Registered map floor whose public transform invalidates a proof");
            _mapFloor.transform.SetParent(_root.transform, false);
            _mapFloor.transform.position = new Vector3(.75f, -.05f, .25f);
            var collider = _mapFloor.AddComponent<BoxCollider>();
            collider.size = new Vector3(4, .1f, 2);
            var registration = typeof(BlockTerrain).GetField("_staticColliders", Fields);
            Assert.That(registration, Is.Not.Null);
            ((HashSet<Collider>)registration.GetValue(_terrain)).Add(collider);
            Physics.SyncTransforms();
            _terrain.Tick(true, Epoch, .016f);
        }

        [UnityTearDown]
        public IEnumerator Teardown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [UnityTest, Timeout(600000), Description("First-version real support-proof rejection safety; original same-draft retirement and carrier conjunctions are diagnostic.")]
        public IEnumerator RejectingARealRetirementDraftPreservesThePublishedInversionBarrier()
        {
            // First-version safety contract: a real support edit must reach a
            // positive native-proof publication, whose real rejection preserves
            // the complete committed source and its original inversion barrier.
            // Same-draft retirement/carrier-result conjunctions are diagnostic;
            // paid partial/failure settlement remains a separate CORE/BuildQuota gate.
            yield return SeedTwoNaturalMovingColumns();
            yield return PublishMainInversion();
            yield return FindSupportRevalidationPending();
            Assert.That(_pendingWitness, Is.Not.Null,
                "A real SideFloor support removal did not reach a positive native-proof publication; real rejection coverage was not reached.");
            Assert.That(ReadField<object>(_terrain, "_pending"), Is.SameAs(_pendingWitness));
            Assert.That(_terrain.World, Is.SameAs(_source));
            AssertPublishedSurvivors();
            AssertNativeTerrainViews();
            Assert.That(_terrain.BuildGravityBarrierColumns, Is.EqualTo(1));
            Assert.That(_mainDestroyCallbacks, Is.Zero);
            AssertSideResultBeforeRejection();

            // Change an actual registered scene collider. The selected guard's
            // ray starts above this map floor, whose minimum changes from 0 to 1.
            const string rejectionWarning = "[BLOCK] Atomic terrain transaction rejected: Static support changed after private landing proof.";
            var sourceFloorPosition = _mapFloor.transform.position;
            int faultRestoreCallbacks = 0;
            Application.LogCallback restoreFloor = (message, stackTrace, type) =>
            {
                if (type != LogType.Warning || message != rejectionWarning) return;
                // Publish already incremented StaticProofInvalidations and Fail
                // already incremented FailedTransactions before this real warning.
                // Undo only this fixture's map-transform fault before Tick can
                // stage the next legitimate recovery proof in the same frame.
                faultRestoreCallbacks++;
                _mapFloor.transform.position = sourceFloorPosition;
                Physics.SyncTransforms();
            };
            Application.logMessageReceived += restoreFloor;
            try
            {
                LogAssert.Expect(LogType.Warning, rejectionWarning);
                _mapFloor.transform.position += Vector3.up * .05f;
                Physics.SyncTransforms();
                yield return Pump(InversionAt, () => _terrain.StaticProofInvalidations == _invalidationsBefore + 1);
            }
            finally
            {
                Application.logMessageReceived -= restoreFloor;
                _mapFloor.transform.position = sourceFloorPosition;
                Physics.SyncTransforms();
            }
            Assert.That(faultRestoreCallbacks, Is.EqualTo(1));
            Assert.That(_terrain.StaticProofInvalidations, Is.EqualTo(_invalidationsBefore + 1));
            Assert.That(_mapFloor.transform.position, Is.EqualTo(sourceFloorPosition));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(_failedBefore + 1));
            Assert.That(_terrain.World, Is.SameAs(_source),
                "A rejected support-proof publication cannot swap the published World.");
            Assert.That(_terrain.Revision, Is.EqualTo(_sourceRevision));
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
            AssertPublishedSurvivors();
            AssertNativeTerrainViews();
            Assert.That(_terrain.BuildGravityBarrierColumns, Is.EqualTo(1),
                "Discarding a draft must preserve the original committed inversion barrier.");
            AssertSideResultAfterRejection();
            Assert.That(_mainDestroyCallbacks, Is.Zero);
            Assert.That(_pinkBuildCallbacks, Is.EqualTo(1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);

            // The unique actual rejection warning restored the map already.
            yield return Pump(InversionAt, () => _terrain.PendingTransactions == 0);
            AssertPublishedSurvivors();
            Assert.That(_terrain.BuildGravityBarrierColumns, Is.EqualTo(1));
            AssertSideResultAfterRejection();
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());

            int recoveryCallbacks = 0;
            bool retiredVisible = false, pinkActivatedVisible = false;
            Action<BlockTerrainCommit> observe = commit =>
            {
                Assert.That(commit.ChangedChunks.Length, Is.LessThanOrEqualTo(8));
                if (!_terrain.World.Cohorts.ContainsKey(_mainCohort)
                    && !_terrain.World.Falling.ContainsKey(_mainRun)) retiredVisible = true;
                if (_terrain.World.Falling.Values.Any(run => run.Cells.Any(cell => cell.Owner == 1)))
                {
                    Assert.That(retiredVisible, Is.True,
                        "Pink activation requires the original owner's actual retirement publication.");
                    pinkActivatedVisible = true;
                }
            };
            _terrain.PublishedCommit += observe;
            try
            {
                var old = _terrain.World.Falling[_mainRun];
                var point = BlockWorld.Center(old.Bottom) - Vector3.up * old.Offset(InversionAt);
                _terrain.QueueDestroy(point, 0, 0, 1, true,
                    result => { recoveryCallbacks++; _mainDestroyCallbacks++; Assert.That(result.RemovedEnemy, Is.EqualTo(1));
                        Assert.That(result.RemovedFriendly, Is.Zero); }, 1, 0);
                yield return Pump(InversionAt, () => recoveryCallbacks == 1 && pinkActivatedVisible);
                Assert.That(retiredVisible, Is.True);
                yield return Pump(10.7, () => _terrain.PendingTransactions == 0
                    && _terrain.World.Falling.Count == 0 && _terrain.World.Cohorts.Count == 0
                    && _terrain.BuildGravityBarrierColumns == 0);
            }
            finally { _terrain.PublishedCommit -= observe; }
            Assert.That(_terrain.World.TryGet(MainFloor + Vector3Int.up, out var pink), Is.True);
            Assert.That(pink.Owner, Is.EqualTo(1));
            Assert.That(pink.Durability, Is.EqualTo(100));
            Assert.That(pink.Flags, Is.Zero);
            Assert.That(_terrain.World.TryGet(PinkSource, out _), Is.False);
            Assert.That(_terrain.BlueCount, Is.EqualTo(_sourceBlueCount - 1));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
            Assert.That(recoveryCallbacks, Is.EqualTo(1));
            Assert.That(_mainDestroyCallbacks, Is.EqualTo(1));
            AssertSideResultAfterRejection();
            var sideLanding = SideFloor + Vector3Int.up * (_sideRemovalPublished ? 0 : 1);
            Assert.That(_terrain.World.TryGet(sideLanding, out var side), Is.True);
            Assert.That(side.Owner, Is.EqualTo(2));
            Assert.That(side.Durability, Is.EqualTo(100));
            Assert.That(side.Flags, Is.Zero);
            if (!_sideRemovalPublished)
                Assert.That(_terrain.World.TryGet(SideFloor, out var retainedSideFloor) && retainedSideFloor.Owner == 2, Is.True);
            Assert.That(_terrain.StaticProofInvalidations, Is.EqualTo(_invalidationsBefore + 1));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(_failedBefore + 1));
            Assert.That(_terrain.HistoricalRecoveryCount, Is.Zero);
            CollectionAssert.AreEqual(_sourceBytes, _source.EncodeSnapshot());
        }

        IEnumerator SeedTwoNaturalMovingColumns()
        {
            _failedBefore = _terrain.FailedTransactions;
            _invalidationsBefore = _terrain.StaticProofInvalidations;
            foreach (var floor in new[] { MainFloor, SideFloor })
            {
                int built = 0;
                _terrain.QueueBuild(BuildPoint(floor), Vector3.up, 2, 6, 0, false,
                    result => { built++; Assert.That(result.Added, Is.EqualTo(6)); });
                yield return Pump(Epoch, () => built == 1 && _terrain.PendingTransactions == 0);
            }
            Assert.That(_terrain.BlueCount, Is.EqualTo(12));
            foreach (var floor in new[] { MainFloor, SideFloor })
            {
                int destroyed = 0;
                var point = BlockWorld.Center(floor); point.y = .3f;
                _terrain.QueueDestroy(point, .1001f, 0, 1, true,
                    result => { destroyed++; Assert.That(result.RemovedEnemy, Is.EqualTo(4)); }, 4, 0);
                yield return Pump(Epoch, () => destroyed == 1 && _terrain.PendingTransactions == 0
                    && _terrain.World.Cohorts.Values.Any(c => c.Bottom == floor + Vector3Int.up * 5
                        && c.Phase == FallingCohortPhase.Falling));
            }
            Assert.That(_terrain.BlueCount, Is.EqualTo(4));
            Assert.That(_terrain.PinkCount, Is.Zero);
            var main = _terrain.World.Cohorts.Values.Single(c => c.Bottom == MainFloor + Vector3Int.up * 5);
            _mainCohort = main.Id; _mainRun = main.MemberIds.Single();
            foreach (var cohort in _terrain.World.Cohorts.Values)
            {
                Assert.That(cohort.StartedAt, Is.EqualTo(Epoch));
                Assert.That(cohort.LandingKnown, Is.True);
                Assert.That(cohort.LandingOffset, Is.EqualTo(.4f).Within(1e-6));
            }
        }

        IEnumerator PublishMainInversion()
        {
            _terrain.QueueBuild(BuildPoint(PinkSource), Vector3.up, 1, 1, 0, false,
                result => { _pinkBuildCallbacks++; Assert.That(result.Added, Is.EqualTo(1)); });
            yield return Pump(InversionAt, () => _pinkBuildCallbacks == 1 && _terrain.PendingTransactions == 0);
            _sourceBlueCount = 4; _sideRemovalPublished = false;
            AssertPublishedSurvivors();
            Assert.That(_terrain.BuildGravityBarrierColumns, Is.EqualTo(1));
            Assert.That(_terrain.FailedTransactions, Is.EqualTo(_failedBefore));
        }

        IEnumerator FindSupportRevalidationPending()
        {
            _pendingWitness = null; _sideDestroyCallbacks = 0; _sideResult = default;
            _sideCompletion = result => { _sideDestroyCallbacks++; _sideResult = result; };
            // Real removal invalidates the existing Side body's landing generation.
            // Observe immediately: the native proof may share its deletion draft,
            // or arise in a later background draft after deletion has published.
            _terrain.QueueDestroy(BlockWorld.Center(SideFloor), 0, 0, 1, true, _sideCompletion, 1, 0);
            object lastObservedPending = null;
            for (int frame = 0; frame < PumpLimit; frame++)
            {
                _terrain.Tick(true, InversionAt, .016f);
                var pending = ReadField<object>(_terrain, "_pending");
                if (pending != null)
                {
                    var next = ReadField<BlockWorld>(pending, "Next");
                    var proofs = ReadField<object>(pending, "StaticProofs");
                    int proofCount = proofs == null ? 0 : (int)proofs.GetType().GetProperty("Count").GetValue(proofs);
                    bool sourceSidePresent = _terrain.World.TryGet(SideFloor, out _);
                    object command = null; BlockMutationResult staged = default;
                    foreach (var row in (IEnumerable)ReadField<object>(pending, "Results"))
                    {
                        var admitted = ReadField<object>(row, "Item1");
                        if (!ReferenceEquals(ReadField<Action<BlockMutationResult>>(admitted, "Callback"), _sideCompletion)) continue;
                        Assert.That(ReadField<bool>(admitted, "Build"), Is.False);
                        command = admitted; staged = ReadField<BlockMutationResult>(row, "Item2"); break;
                    }
                    if (!ReferenceEquals(pending, lastObservedPending))
                    {
                        lastObservedPending = pending;
                        TestContext.WriteLine($"[BLOCK-REJECTION-SUPPORT] frame={frame} sourceRevision={_terrain.Revision} draftRevision={next.Revision} sourceBlue={_terrain.BlueCount} sourceSidePresent={sourceSidePresent} draftSidePresent={next.TryGet(SideFloor, out _)} mainCohortPresent={next.Cohorts.ContainsKey(_mainCohort)} mainRunPresent={next.Falling.ContainsKey(_mainRun)} staticProofs={proofCount} sideResultAdmitted={command != null} stagedRemovedEnemy={staged.RemovedEnemy} sideCallbacks={_sideDestroyCallbacks} sideResultRemovedEnemy={_sideResult.RemovedEnemy}");
                    }
                    bool actualSideFloorProof = false;
                    if (proofs != null) foreach (var proof in (IEnumerable)proofs)
                    {
                        var at = ReadField<Vector3>(proof, "Position");
                        var bounds = _mapFloor.GetComponent<BoxCollider>().bounds;
                        var horizontal = new Vector3(at.x, bounds.center.y, at.z);
                        var sideCenter = BlockWorld.Center(SideFloor);
                        actualSideFloorProof |= at.y > .05f && bounds.Contains(horizontal)
                            && Mathf.Abs(at.x - sideCenter.x) < 1e-6f
                            && Mathf.Abs(at.z - sideCenter.z) < 1e-6f
                            && ReadField<int>(proof, "Minimum") == 0;
                    }
                    if (proofCount > 0 && actualSideFloorProof)
                    {
                        _sideRemovalPublished = !sourceSidePresent; _sideCommandAdmitted = command != null;
                        _sideCommittedAtWitness = command == null ? _sideResult : ReadField<BlockMutationResult>(command, "Total");
                        _sourceBlueCount = _terrain.BlueCount;
                        Assert.That(_sourceBlueCount, Is.EqualTo(_sideRemovalPublished ? 3 : 4));
                        Assert.That(next.TryGet(SideFloor, out _), Is.False);
                        Assert.That(next.BlueCount, Is.EqualTo(3));
                        Assert.That(next.PinkCount, Is.EqualTo(1));
                        if (_sideRemovalPublished)
                        {
                            Assert.That(command, Is.Null, "An already completed one-cell deletion cannot be admitted twice.");
                            Assert.That(_sideDestroyCallbacks, Is.EqualTo(1));
                            Assert.That(_sideResult.RemovedEnemy, Is.EqualTo(1));
                        }
                        else
                        {
                            Assert.That(command, Is.Not.Null, "An unpublished SideFloor deletion must be attributed to its actual admitted command.");
                            Assert.That(_sideDestroyCallbacks, Is.Zero);
                            Assert.That(staged.Added, Is.Zero);
                            Assert.That(staged.RemovedEnemy, Is.EqualTo(1));
                            Assert.That(staged.RemovedFriendly, Is.Zero);
                            Assert.That(_sideCommittedAtWitness.Changed, Is.Zero);
                        }
                        _source = _terrain.World; _sourceBytes = _source.EncodeSnapshot(); _sourceRevision = _terrain.Revision;
                        CaptureNativeTerrainViews(); _pendingWitness = pending; yield break;
                    }
                }
                yield return null;
            }
        }

        void AssertSideResultBeforeRejection()
        {
            Assert.That(_sideDestroyCallbacks, Is.EqualTo(_sideRemovalPublished ? 1 : 0));
            Assert.That(_sideCommandAdmitted, Is.EqualTo(!_sideRemovalPublished));
            Assert.That(_sideCommittedAtWitness.Added, Is.Zero);
            Assert.That(_sideCommittedAtWitness.RemovedEnemy, Is.EqualTo(_sideRemovalPublished ? 1 : 0));
            Assert.That(_sideCommittedAtWitness.RemovedFriendly, Is.Zero);
        }

        void AssertSideResultAfterRejection()
        {
            Assert.That(_sideDestroyCallbacks, Is.EqualTo(1));
            Assert.That(_sideResult.Added, Is.EqualTo(_sideCommittedAtWitness.Added));
            Assert.That(_sideResult.RemovedEnemy, Is.EqualTo(_sideCommittedAtWitness.RemovedEnemy),
                "Only a published SideFloor deletion can enter the callback; a rejected staged removal remains excluded.");
            Assert.That(_sideResult.RemovedFriendly, Is.EqualTo(_sideCommittedAtWitness.RemovedFriendly));
        }

        void CaptureNativeTerrainViews()
        {
            _sourceViews.Clear();
            foreach (DictionaryEntry pair in (IDictionary)ReadField<object>(_terrain, "_views"))
            {
                var view = pair.Value; var collider = ReadField<MeshCollider>(view, "Collider");
                var obj = ReadField<GameObject>(view, "Object");
                _sourceViews.Add(new NativeViewSnapshot { Key = (Vector3Int)pair.Key, View = view, Object = obj,
                    Collider = collider, CollisionMesh = ReadField<Mesh>(view, "CollisionMesh"),
                    RenderMesh = ReadField<Mesh>(view, "RenderMesh"), SharedMesh = collider.sharedMesh,
                    Enabled = collider.enabled, ActiveSelf = obj.activeSelf, ActiveInHierarchy = obj.activeInHierarchy });
            }
            Assert.That(_sourceViews.Count, Is.GreaterThan(0), "The source must contain actual published native terrain Colliders.");
        }
        void AssertNativeTerrainViews()
        {
            var views = (IDictionary)ReadField<object>(_terrain, "_views");
            Assert.That(views.Count, Is.EqualTo(_sourceViews.Count));
            foreach (var snapshot in _sourceViews)
            {
                Assert.That(views.Contains(snapshot.Key), Is.True);
                var view = views[snapshot.Key];
                Assert.That(view, Is.SameAs(snapshot.View));
                Assert.That(ReadField<GameObject>(view, "Object"), Is.SameAs(snapshot.Object));
                Assert.That(ReadField<MeshCollider>(view, "Collider"), Is.SameAs(snapshot.Collider));
                Assert.That(ReadField<Mesh>(view, "CollisionMesh"), Is.SameAs(snapshot.CollisionMesh));
                Assert.That(ReadField<Mesh>(view, "RenderMesh"), Is.SameAs(snapshot.RenderMesh));
                Assert.That(snapshot.Collider.sharedMesh, Is.SameAs(snapshot.SharedMesh));
                Assert.That(snapshot.Collider.enabled, Is.EqualTo(snapshot.Enabled));
                Assert.That(snapshot.Object.activeSelf, Is.EqualTo(snapshot.ActiveSelf));
                Assert.That(snapshot.Object.activeInHierarchy, Is.EqualTo(snapshot.ActiveInHierarchy));
            }
        }

        void AssertPublishedSurvivors()
        {
            Assert.That(_terrain.World.Cohorts.ContainsKey(_mainCohort), Is.True);
            Assert.That(_terrain.World.Falling.TryGetValue(_mainRun, out var main), Is.True);
            float expectedOffset = .5f * BlockWorld.Gravity * .25f * .25f;
            Assert.That(main.Offset(InversionAt), Is.EqualTo(expectedOffset).Within(1e-6));
            Assert.That(main.Speed(InversionAt), Is.EqualTo(BlockWorld.Gravity * .25f).Within(1e-6));
            Assert.That(_terrain.World.TryGet(PinkSource, out var pink), Is.True);
            Assert.That(pink.Owner, Is.EqualTo(1));
            Assert.That(_terrain.World.Falling.Values.Any(run => run.Cells.Any(cell => cell.Owner == 1)), Is.False);
            Assert.That(_terrain.World.TryGet(MainFloor, out var mainFloor), Is.True);
            bool sidePresent = _terrain.World.TryGet(SideFloor, out var sideFloor);
            Assert.That(sidePresent, Is.EqualTo(!_sideRemovalPublished));
            Assert.That(mainFloor.Owner, Is.EqualTo(2));
            if (sidePresent) Assert.That(sideFloor.Owner, Is.EqualTo(2));
            Assert.That(_terrain.BlueCount, Is.EqualTo(_sourceBlueCount));
            Assert.That(_terrain.PinkCount, Is.EqualTo(1));
        }

        IEnumerator Pump(double now, Func<bool> ready)
        {
            for (int frame = 0; frame < PumpLimit && !ready(); frame++)
            {
                _terrain.Tick(true, now, .016f);
                yield return null;
            }
            Assert.That(ready(), Is.True,
                "Bounded Editor scheduling did not reach the required state; this is not a Player timing gate.");
        }
        static T ReadField<T>(object owner, string name)
        {
            var field = owner.GetType().GetField(name, Fields);
            Assert.That(field, Is.Not.Null, "Required read-only fixture field: " + name);
            return (T)field.GetValue(owner);
        }
        static Vector3 BuildPoint(Vector3Int cell) => BlockWorld.Center(cell) - Vector3.up * .051f;
    }
}
#endif
