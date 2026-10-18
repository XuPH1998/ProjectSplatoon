#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>Paid combat through the real host services and asynchronous surface publication.</summary>
    public sealed class BlockSurfaceCombatPlayTests
    {
        const float Dt = 1f / 60;
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockDefaultContractFixture _fixture;
        RaycastHit _floor;
        uint _shotId = 0x7c000000;
        BlockTerrain Terrain => _match.Blocks;
        BlockDynamicWorld Dynamic => Terrain.Dynamic;

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot");
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks?.Dynamic != null,
                "Real authority surface/free block world");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local;
            _match.enabled = false; foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match); _fixture.Tick();
            Assert.That(Terrain.SurfaceOnly, Is.True);
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out _floor, 10,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True, "Original arena floor");
            Reset();
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            _match?.Projectiles.Clear(); _match?.SubWeapons.Clear();
            BlockAttackService.Clear(); _fixture?.Dispose();
            if (Application.isPlaying) yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Place(Vector3 position, byte team = 1)
        {
            var state = _host.Snapshot.Value;
            state.Position = position; state.Team = team; state.Health = state.Ink = 100;
            state.LifeState = PlayerLifeState.Alive; state.ProtectedUntil = 0;
            state.Swimming = state.CompactBody = false; state.PaperPose = PaperPose.None;
            state.Grounded = true; state.AirHumanOffset = 0; state.VerticalSpeed = 0;
            state.SpecialWeaponId = 1; state.SpecialPhase = SpecialPhase.Charging;
            state.SpecialChargeLockedUntil = 0; state.SpecialPoints = 0;
            var controller = _host.GetComponent<CharacterController>();
            controller.enabled = false; _host.transform.position = position; controller.enabled = true;
            _host.Snapshot.Value = state; _host.SwimBody?.ApplyCollision(state); Physics.SyncTransforms();
        }

        void Reset()
        {
            _match.Projectiles.Clear(); _match.SubWeapons.Clear(); _match.SpecialWeapons.Clear();
            BlockAttackService.Clear(); Terrain.Clear(_match.State.Value.Round); _fixture.Tick();
            Place(_floor.point + Vector3.back * 8 + Vector3.up * .04f);
        }

        static IEnumerable<(Vector3Int cell, BlockCell value)> Cells(BlockWorld world)
        {
            foreach (var chunk in world.Chunks)
                for (int index = 0; index < BlockWorld.CellsPerChunk; index++)
                    if (chunk.Value.Cells[index].Occupied)
                        yield return (BlockWorld.CellFromIndex(chunk.Key, index), chunk.Value.Cells[index]);
        }

        IEnumerator Pump(Func<bool> ready, string label, Action simulate = null)
        {
            for (int frame = 0; frame < 1200 && !ready(); frame++)
            {
                _fixture.Now += Dt; simulate?.Invoke(); Terrain.Tick(true, _fixture.Now, Dt);
                Physics.SyncTransforms(); yield return null;
            }
            Assert.That(ready(), Is.True, label);
            Assert.That(Terrain.FailedTransactions, Is.Zero);
        }

        IEnumerator Fire(InkShot shot)
        {
            _match.Projectiles.SpawnForMeasurement(shot);
            yield return Pump(() => _match.Projectiles.Impacts.Any(hit => hit.Id == shot.Id),
                "Paid projectile reaches authoritative collision", () => _match.Projectiles.Simulate(_fixture.Now));
            _match.Projectiles.Clear();
            yield return Pump(() => Terrain.PendingTransactions == 0, "Surface work and paid callbacks drain");
        }

        void AssertCredit(BlockAttackToken token, double before = 0)
        {
            double expected = Math.Min(token.PointLimit, (token.Added + token.RemovedEnemy) / (double)token.BlocksPerPoint);
            Assert.That(token.AwardedPoints, Is.EqualTo(expected).Within(.00001));
            Assert.That(_host.Snapshot.Value.SpecialPoints - before, Is.EqualTo(expected).Within(.00001));
        }

        void AssertCounts()
        {
            Assert.That(Terrain.PinkCount, Is.EqualTo(Terrain.World.PinkCount + Dynamic.PinkCount));
            Assert.That(Terrain.BlueCount, Is.EqualTo(Terrain.World.BlueCount + Dynamic.BlueCount));
            _fixture.FlushEvents();
            Assert.That(_match.State.Value.PinkBlockCount, Is.EqualTo(Terrain.PinkCount));
            Assert.That(_match.State.Value.BlueBlockCount, Is.EqualTo(Terrain.BlueCount));
            Assert.That(_match.State.Value.DynamicBlockRevision, Is.EqualTo(Dynamic.LifecycleRevision));
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidMapThenFriendlyAttachedAndFreeHitsUseIndependentBodies()
        {
            uint paint = _match.PaintSequence;
            var first = _fixture.PaidShot(_host, 1, ++_shotId, _floor.point + Vector3.up * 1.2f, Vector3.down * 32);
            float paidInk = _host.Snapshot.Value.Ink;
            yield return Fire(first);
            Assert.That(first.BlockAttack.Added, Is.GreaterThan(0)); Assert.That(Dynamic.Count, Is.Zero);
            var cells = Cells(Terrain.World).ToArray();
            int row = BlockWorld.CellAt(_floor.point + Vector3.up * (BlockWorld.CellSize * .51f)).y;
            Assert.That(cells.All(pair => pair.value.SurfaceAttached && pair.value.Anchored && pair.cell.y == row), Is.True,
                "Only the first original-map layer may enter the lattice");
            Assert.That(Terrain.World.Falling.Count, Is.Zero);
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidInk).Within(.001));
            AssertCredit(first.BlockAttack);

            byte[] coating = Terrain.World.EncodeSnapshot();
            Vector3 center = BlockWorld.Center(cells.OrderBy(pair => (BlockWorld.Center(pair.cell) - _floor.point).sqrMagnitude).First().cell);
            double points = _host.Snapshot.Value.SpecialPoints;
            var second = _fixture.PaidShot(_host, 1, ++_shotId, center + Vector3.up * .45f, Vector3.down * 32);
            yield return Fire(second);
            Assert.That(second.BlockAttack.Added, Is.GreaterThan(0)); Assert.That(Dynamic.Count, Is.EqualTo(second.BlockAttack.Added));
            CollectionAssert.AreEqual(coating, Terrain.World.EncodeSnapshot(), "Friendly coating hits cannot grow another attached layer");
            Assert.That(Dynamic.Bodies.Values.All(body => !body.Body.isKinematic && body.Body.useGravity && body.Body.mass > 0 &&
                body.Collider.size == Vector3.one * BlockWorld.CellSize), Is.True);
            AssertCredit(second.BlockAttack, points);

            long created = Dynamic.CreatedCount;
            var top = Dynamic.Bodies.Values.OrderByDescending(body => body.Body.position.y).First();
            points = _host.Snapshot.Value.SpecialPoints;
            var third = _fixture.PaidShot(_host, 1, ++_shotId, top.Body.position + Vector3.up * .45f, Vector3.down * 32);
            yield return Fire(third);
            Assert.That(third.BlockAttack.Added, Is.GreaterThan(0));
            Assert.That(Dynamic.CreatedCount - created, Is.EqualTo(third.BlockAttack.Added), "Friendly free body hits also create independent bodies");
            CollectionAssert.AreEqual(coating, Terrain.World.EncodeSnapshot()); AssertCredit(third.BlockAttack, points);
            Assert.That(_match.PaintSequence, Is.EqualTo(paint), "Block combat emits no legacy planar paint");
            AssertCounts();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidEnemyHitDestroysAndSharedPelletPaymentCannotBuildTwice()
        {
            var enemyPosition = _floor.point + Vector3.up * 2;
            Assert.That(Dynamic.TrySpawn(2, enemyPosition, Quaternion.identity, out var enemy), Is.True);
            enemy.Body.Sleep(); long created = Dynamic.CreatedCount;
            var shot = _fixture.PaidShot(_host, 1, ++_shotId, enemyPosition + Vector3.back * .5f, Vector3.forward * 32);
            yield return Fire(shot);
            Assert.That(shot.BlockAttack.RemovedEnemy, Is.EqualTo(1)); Assert.That(shot.BlockAttack.Added, Is.Zero);
            Assert.That(Dynamic.CreatedCount, Is.EqualTo(created)); Assert.That(Dynamic.Count, Is.Zero);
            Assert.That(Terrain.World.PinkCount + Terrain.World.BlueCount, Is.Zero); AssertCredit(shot.BlockAttack);

            Reset();
            const int sharedProjectileHero = 7;
            var sharedWeapon = GameplayConfig.GetWeapon(sharedProjectileHero);
            Assert.That(sharedWeapon.BlockBuild && !BlockAttackService.ExplosiveMain(sharedWeapon), Is.True,
                "Use the authored nonexplosive four-bubble weapon; explosive hero 9 deliberately has no build budget");
            var token = _fixture.PayMain(_host, sharedProjectileHero);
            Assert.That(token.BuildLimit, Is.GreaterThan(0), "The real paid weapon must grant a construction quota");
            Assert.That(token.ProjectilesPerPayment, Is.EqualTo(sharedWeapon.BurstCount));
            Assert.That(token.ProjectilesPerPayment, Is.GreaterThan(1), "Loaded bubbles really share one paid attack");
            for (int pellet = 0; pellet < token.ProjectilesPerPayment; pellet++)
            {
                uint entity = ++_shotId;
                var point = _floor.point + new Vector3(pellet * .8f, 2.5f, 0);
                int before = token.Added;
                BlockAttackService.Build(token, point, Vector3.up, .22f, false, false, entityId: entity, eventOrdinal: 1);
                int actual = token.Added - before;
                Assert.That(actual, Is.InRange(0, (int)Math.Ceiling(token.BuildLimit / (double)token.ProjectilesPerPayment)));
                int balance = token.BuildRemaining, count = Dynamic.Count;
                BlockAttackService.Build(token, point, Vector3.up, .22f, false, false, entityId: entity, eventOrdinal: 1);
                Assert.That(Dynamic.Count, Is.EqualTo(count)); Assert.That(token.BuildRemaining, Is.EqualTo(balance), "Repeated impact has no second reservation");
            }
            Assert.That(token.Added, Is.GreaterThan(0)); Assert.That(token.Added, Is.LessThanOrEqualTo(token.BuildLimit));
            Assert.That(token.BuildRemaining + token.Added, Is.EqualTo(token.BuildLimit));
            Assert.That(Dynamic.Count, Is.EqualTo(token.Added)); AssertCredit(token); AssertCounts();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidDirectSharesOneNearestQuotaAcrossAttachedAndFreeEnemyBlocks()
        {
            var cell = BlockWorld.CellAt(_floor.point + Vector3.up * (BlockWorld.CellSize * .51f));
            var seed = _fixture.Empty(); seed.Set(cell, BlockCell.Create(2, true, true));
            seed.Set(cell + Vector3Int.right, BlockCell.Create(2, true, true));
            yield return _fixture.Seed(seed);
            Vector3 center = BlockWorld.Center(cell);
            Assert.That(Dynamic.TrySpawn(2, center + Vector3.up * BlockWorld.CellSize, Quaternion.identity, out var enemy), Is.True);
            enemy.Body.Sleep(); var token = _fixture.PayMain(_host, 1);
            Assert.That(token.DirectLimit, Is.GreaterThanOrEqualTo(2));
            BlockAttackService.Direct(token, center + Vector3.up * (BlockWorld.CellSize * .5f), 2, ++_shotId, 1);
            Assert.That(Dynamic.Count, Is.Zero, "The closer free target is removed in the same accepted event");
            yield return _fixture.Publish(() => token.RemovedEnemy == 2);
            Assert.That(Terrain.BlueCount, Is.EqualTo(1)); Assert.That(Terrain.World.BlueCount, Is.EqualTo(1));
            Assert.That(token.DirectRemaining, Is.EqualTo(token.DirectLimit - 2)); Assert.That(token.Added, Is.Zero);
            AssertCredit(token); AssertCounts();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidBurstBombKeepsFriendlyRadiusAndCountsBothRepresentationsOnce()
        {
            float charge = _fixture.PaySub(_host, SubWeaponType.BurstBomb);
            var state = _host.Snapshot.Value; var config = PlayerLoadout.SubWeapon(state);
            float radius = config.Blast.outerRadius;
            float friendly = radius * BlockGameplaySettings.FriendlyBlastRadiusFraction;
            Assert.That(friendly, Is.GreaterThan(.5f)); Assert.That(radius - friendly, Is.GreaterThan(.5f));
            float far = (radius + friendly) * .5f;
            var cell = BlockWorld.CellAt(_floor.point + Vector3.up * (BlockWorld.CellSize * .51f));
            Vector3 center = BlockWorld.Center(cell);
            var seed = _fixture.Empty();
            foreach (var entry in new[] { (x: -.4f, owner: (byte)1), (x: .4f, owner: (byte)2), (x: -far, owner: (byte)1), (x: far, owner: (byte)2) })
                seed.Set(BlockWorld.CellAt(center + Vector3.right * entry.x), BlockCell.Create(entry.owner, true, true));
            yield return _fixture.Seed(seed);
            foreach (var entry in new[] { (x: -.4f, owner: (byte)1), (x: .4f, owner: (byte)2), (x: -far, owner: (byte)1), (x: far, owner: (byte)2) })
            {
                Assert.That(Dynamic.TrySpawn(entry.owner, center + new Vector3(entry.x, .6f, .6f), Quaternion.identity, out var body), Is.True);
                body.Body.Sleep();
            }
            var burst = _match.SubWeapons.Spawn(_host, state, charge, _fixture.Now, Terrain.Round,
                new SubLaunchSolution { Position = center + Vector3.up * .8f, Velocity = Vector3.down * 6, Normal = Vector3.up });
            Assert.That(burst, Is.Not.Null); float paidInk = _host.Snapshot.Value.Ink;
            yield return Pump(() => burst.Removed, "Real BurstBomb collision emits explosion", () => _match.SubWeapons.Step(_fixture.Now, Dt, _match.Players));
            yield return _fixture.Publish();
            Assert.That(_match.SubWeapons.Effects.Count(effect => effect.EntityId == burst.State.Id && effect.Kind == SubEffectKind.Explosion), Is.EqualTo(1));
            Assert.That(Terrain.BlueCount, Is.Zero); Assert.That(Terrain.PinkCount, Is.EqualTo(2), "Far friendly coat and free body survive the smaller radius");
            Assert.That(Terrain.World.PinkCount, Is.EqualTo(1)); Assert.That(Dynamic.PinkCount, Is.EqualTo(1));
            Assert.That(burst.BlockAttack.RemovedEnemy, Is.EqualTo(4)); Assert.That(burst.BlockAttack.Added, Is.Zero);
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidInk).Within(.001)); AssertCredit(burst.BlockAttack);
            double points = _host.Snapshot.Value.SpecialPoints;
            _match.SubWeapons.Step(_fixture.Now + Dt, Dt, _match.Players); yield return _fixture.Publish();
            Assert.That(_host.Snapshot.Value.SpecialPoints, Is.EqualTo(points)); AssertCounts();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidAngleShooterEnemyCoatDestroysAndStopsLine()
            => PaidAngleShooterEnemyBlockDestroysAndStopsLine(false);

        [UnityTest, Timeout(600000)] public IEnumerator PaidAngleShooterEnemyFreeBodyDestroysAndStopsLine()
            => PaidAngleShooterEnemyBlockDestroysAndStopsLine(true);

        IEnumerator PaidAngleShooterEnemyBlockDestroysAndStopsLine(bool free)
        {
            var cell = BlockWorld.CellAt(_floor.point + Vector3.up * (BlockWorld.CellSize * .51f));
            Vector3 center = BlockWorld.Center(cell);
            if (free)
            {
                Assert.That(Dynamic.TrySpawn(2, center, Quaternion.identity, out var enemy), Is.True);
                enemy.Body.Sleep();
            }
            else
            {
                var seed = _fixture.Empty(); seed.Set(cell, BlockCell.Create(2, true, true));
                yield return _fixture.Seed(seed);
            }
            float charge = _fixture.PaySub(_host, SubWeaponType.AngleShooter);
            var state = _host.Snapshot.Value; var config = PlayerLoadout.SubWeapon(state);
            Vector3 origin = center + Vector3.back * .6f;
            Assert.That(new TpsAimSolver().ClosestCast(origin, Vector3.forward, 1, .04f,
                _host.PlayerId, out var captured, false, state.Team), Is.True);
            Assert.That(BlockAttackService.Contact(captured, out var contact), Is.True);
            Assert.That(contact.Owner, Is.EqualTo(2));
            Assert.That(contact.Kind, Is.EqualTo(free ? BlockContactKind.Dynamic : BlockContactKind.SurfaceAttached));
            var line = _match.SubWeapons.Spawn(_host, state, charge, _fixture.Now, Terrain.Round,
                new SubLaunchSolution { Position = origin, Velocity = Vector3.forward * config.Angle.speed, Normal = Vector3.up });
            Assert.That(line, Is.Not.Null); long created = Dynamic.CreatedCount;
            uint paint = _match.PaintSequence; float paidInk = _host.Snapshot.Value.Ink;
            _fixture.Now += Dt; _match.SubWeapons.Step(_fixture.Now, Dt, _match.Players);
            yield return Pump(() => line.BlockAttack.RemovedEnemy == 1 && Terrain.PendingTransactions == 0,
                "The paid line's enemy destruction and publication finish");
            Assert.That(BlockAttackService.Contact(captured, out _), Is.False,
                "Destroying the target intentionally invalidates the captured contact identity");
            Assert.That(line.Removed, Is.True,
                "An enemy block hit must end the original line even when synchronous free-body deletion invalidates its contact");
            Assert.That(line.State.Explosions, Is.Zero, "Enemy destruction must not become a reflection");
            Assert.That(line.BlockAttack.Added, Is.Zero); Assert.That(Dynamic.CreatedCount, Is.EqualTo(created));
            Assert.That(Terrain.BlueCount, Is.Zero); Assert.That(_match.PaintSequence, Is.EqualTo(paint));
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidInk).Within(.001));
            AssertCredit(line.BlockAttack); AssertCounts();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidShotHitsMovingRotatingBodyAndCannotResolveItsPooledReplacement()
        {
            Vector3 position = _floor.point + Vector3.up * 2;
            Assert.That(Dynamic.TrySpawn(2, position, Quaternion.identity, out var target), Is.True);
            target.Body.useGravity = false; target.Body.linearVelocity = Vector3.right * .2f;
            target.Body.angularVelocity = Vector3.up;
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            Assert.That(target.Body.position.x, Is.GreaterThan(position.x));
            Assert.That(Quaternion.Angle(target.Body.rotation, Quaternion.identity), Is.GreaterThan(.1f));
            var aim = new TpsAimSolver();
            Assert.That(aim.ClosestCast(target.Body.position + Vector3.back * .5f, Vector3.forward, 1, .01f,
                _host.PlayerId, out var captured, false), Is.True);
            Assert.That(captured.BlockQuery.IsKnown, Is.True); Assert.That(BlockAttackService.Contact(captured, out var contact), Is.True);
            Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.Dynamic)); Assert.That(contact.DynamicId, Is.EqualTo(target.Id));
            var shot = _fixture.PaidShot(_host, 1, ++_shotId, target.Body.position + Vector3.back * .5f, Vector3.forward * 32);
            ulong oldId = target.Id; yield return Fire(shot);
            Assert.That(shot.BlockAttack.RemovedEnemy, Is.EqualTo(1)); Assert.That(Dynamic.TryGet(oldId, out _), Is.False);
            Assert.That(Dynamic.TrySpawn(1, position + Vector3.right * 2, Quaternion.identity, out var replacement), Is.True);
            Assert.That(replacement, Is.SameAs(target), "Exercise the actual pooled component");
            Assert.That(replacement.Id, Is.Not.EqualTo(oldId));
            Assert.That(BlockAttackService.Contact(captured, out _), Is.False, "An old query cannot change ownership or bind to a new pooled body");
            AssertCredit(shot.BlockAttack); AssertCounts();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PaidAttachedDevicesFollowFreeBodiesAndRejectReleasedPooledSupports()
        {
            foreach (var type in new[] { SubWeaponType.SuctionBomb, SubWeaponType.Sprinkler })
            {
                Reset(); Vector3 position = _floor.point + Vector3.up * 2;
                Assert.That(Dynamic.TrySpawn(1, position, Quaternion.identity, out var support), Is.True);
                support.Body.Sleep(); float charge = _fixture.PaySub(_host, type);
                var entity = _match.SubWeapons.Spawn(_host, _host.Snapshot.Value, charge, _fixture.Now, Terrain.Round,
                    new SubLaunchSolution { Position = position + Vector3.back * .6f, Velocity = Vector3.forward * 8, Normal = Vector3.up });
                Assert.That(entity, Is.Not.Null);
                yield return Pump(() => entity.Attached, type + " really deploys on the free-body collider", () => _match.SubWeapons.Step(_fixture.Now, Dt, _match.Players));
                Assert.That(entity.BlockAttachmentId, Is.EqualTo(support.Id));
                Assert.That(entity.BlockAttachmentWorld, Is.SameAs(Dynamic)); Assert.That(entity.BlockAttachmentRound, Is.EqualTo(Terrain.Round));
                Vector3 local = entity.LocalPosition, localNormal = entity.LocalNormal;
                support.Body.position = position + Vector3.right * .5f; support.Body.rotation = Quaternion.Euler(0, 90, 0);
                support.transform.SetPositionAndRotation(support.Body.position, support.Body.rotation); Physics.SyncTransforms();
                _fixture.Now += Dt; _match.SubWeapons.Step(_fixture.Now, Dt, _match.Players);
                Assert.That(Vector3.Distance(entity.State.Position, support.transform.TransformPoint(local)), Is.LessThan(.001f));
                Assert.That(Vector3.Dot(entity.State.Normal, support.transform.TransformDirection(localNormal)), Is.GreaterThan(.999f));
                ulong id = support.Id; double fuse = entity.Fuse;
                Assert.That(Dynamic.DestroyIds(new[] { id }, 2), Is.EqualTo(1));
                Assert.That(Dynamic.TrySpawn(1, position + Vector3.right * 3, Quaternion.identity, out var replacement), Is.True);
                Assert.That(replacement, Is.SameAs(support)); Assert.That(replacement.Id, Is.Not.EqualTo(id));
                _fixture.Now += Dt; _match.SubWeapons.Step(_fixture.Now, Dt, _match.Players);
                if (type == SubWeaponType.SuctionBomb)
                {
                    Assert.That(entity.Attached, Is.False); Assert.That(entity.BlockAttachmentId, Is.Zero);
                    Assert.That(entity.State.Phase, Is.EqualTo(SubEntityPhase.Flying)); Assert.That(entity.Fuse, Is.EqualTo(fuse));
                }
                else Assert.That(entity.Removed, Is.True, "A persistent device cannot inherit a pooled replacement's support");
            }
        }
    }
}
#endif
