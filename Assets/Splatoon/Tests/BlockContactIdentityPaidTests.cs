#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // Reports candidate. Synthetic, explicitly anchored/detached target seeds and
    // measurement launch geometry; real host, production WeaponSimulation payment,
    // Main token and Projectiles.Simulate collision/terrain/credit consumption.
    // This is not an InputSystem, ordinary aim or gameplay-built-structure gate.
    public sealed class BlockContactIdentityPaidTests
    {
        const float Dt = 1f / 60;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const int PumpLimit = 1800;
        PrototypeApp _app;
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockTerrain _terrain;
        Action _serverTick;
        double _now;
        Vector3 _floor;
        uint _id;
        readonly Vector3Int _bottom = new(0, 200, 0);

        [UnityTest, Timeout(600000)]
        public IEnumerator PaidSharedFacesRetainQueryIdentityAndStaleKnownContactsNeverReconstruct()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            // Captures and case locals live inside this post-reload iterator.
            yield return PostEnterScenario();
            yield return Leave();
            yield return new ExitPlayMode();
        }

        IEnumerator PostEnterScenario()
        {
            yield return SetupHost();
            yield return StablePaidEnemyFace();
            yield return FallingPaidEnemyFace();
            yield return WaveBreakerEmbeddedExactAttachment();
            yield return StaleStableClearAndReplacement();
            yield return StaleFallingTime();

            // Retain a real query across a real host-room terrain destruction and
            // replacement. IsKnown must remain set after its Unity object dies.
            var oldQuery = FallingQuery(out _);
            var oldTerrain = _terrain;
            yield return Leave();
            Assert.That(oldTerrain == null, Is.True, "Room exit destroys the source terrain");
            Assert.That(oldQuery.BlockQuery.IsKnown, Is.True, "Destroyed source must not turn known metadata into an unknown contact");
            Assert.That(oldQuery.BlockQuery.TryResolveCurrent(out _), Is.False);
            yield return SetupHost();
            Assert.That(_terrain, Is.Not.SameAs(oldTerrain));
            Assert.That(BlockTerrain.Current, Is.SameAs(_terrain));
            Assert.That(oldQuery.BlockQuery.TryResolveCurrent(out _), Is.False, "Another real host terrain cannot accept the old query identity");
            Assert.That(BlockAttackService.Contact(oldQuery, out _), Is.False);
        }

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        IEnumerator SetupHost()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            _app = PrototypeApp.Current;
            yield return _app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks != null, "Real block host");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local; _terrain = _match.Blocks;
            Assert.That(_host.IsServer && _match.IsServer && BlockAttackService.Active, Is.True);
            _app.CaptureMouse(false); // Release only. Never focus a window or capture the physical mouse.
            _match.enabled = false;
            _serverTick = (Action)Delegate.CreateDelegate(typeof(Action), _match,
                typeof(PrototypeMatch).GetMethod("ServerTick", Private));
            _match.NetworkManager.NetworkTickSystem.Tick -= _serverTick;
            foreach (var player in _match.Players) player.enabled = false;
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10,
                PlayerMotorSimulation.WorldMask), Is.True);
            _floor = floor.point; _now = _match.NetworkManager.ServerTime.Time;
            foreach (var collider in _match.Arena.GetComponentsInChildren<Collider>(true))
                if (collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger)
                    Assert.That(collider.bounds.max.y, Is.LessThan(BlockWorld.Center(_bottom).y - 1),
                        "The actual map stays below these diagnostic targets; do not remove map obstruction to pass");
            Assert.That(GameplayConfig.GetWeapon(4).CollisionRadius, Is.EqualTo(.14976911f));
            Assert.That(_terrain.PublicationPolicy.OrdinaryAttackKeys, Is.EqualTo(8));
            Assert.That(_terrain.PublicationPolicy.FrameKeys, Is.EqualTo(32));
            Place(1);
        }

        void Place(byte team)
        {
            var s = _host.Snapshot.Value;
            s.HeroId = 4; s.Team = team; s.Health = s.Ink = 100;
            s.Position = _floor + Vector3.back * 8; s.LifeState = PlayerLifeState.Alive;
            s.ProtectedUntil = 0; s.Swimming = s.CompactBody = false; s.PaperPose = PaperPose.None;
            s.Grounded = true; s.VerticalSpeed = s.AirHumanOffset = 0; s.PlanarVelocity = s.Velocity = Vector3.zero;
            s.SpecialWeaponId = 1; s.SpecialPhase = SpecialPhase.Charging;
            s.SpecialChargeLockedUntil = 0; s.SpecialPoints = 0;
            var controller = _host.GetComponent<CharacterController>(); controller.enabled = false;
            _host.transform.position = s.Position; controller.enabled = true; _host.Snapshot.Value = s;
            _host.SwimBody?.ApplyCollision(s); Physics.SyncTransforms();
        }

        BlockWorld Empty() => new(_terrain.World.Dimensions, _terrain.Round, _terrain.World.Bottom);
        IEnumerator Install(bool falling, byte hostTeam)
        {
            _match.Projectiles.Clear(); _match.SubWeapons.Clear(); _match.SpecialWeapons.Clear();
            BlockAttackService.Clear(); _terrain.Clear(_match.State.Value.Round); Place(hostTeam);
            var seed = Empty();
            Assert.That(seed.Set(_bottom, BlockCell.Create(1, !falling)), Is.True);
            Assert.That(seed.Set(_bottom + Vector3Int.up, BlockCell.Create(2, !falling)), Is.True);
            if (falling) Assert.That(seed.Detach(_bottom, 2, _now), Is.Not.Null);
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            // Remote publication mode transports the complete seeded falling run.
            // It does not let authority settle it while the synthetic fixture is installed.
            yield return Pump(() => _terrain.PendingTransactions == 0, "Synthetic target seed published", false);
            Assert.That(_terrain.PinkCount, Is.EqualTo(1)); Assert.That(_terrain.BlueCount, Is.EqualTo(1));
            Assert.That(_terrain.FallingCount, Is.EqualTo(falling ? 2 : 0));
            Assert.That(_match.State.Value.PinkBlockCount, Is.EqualTo(1));
            Assert.That(_match.State.Value.BlueBlockCount, Is.EqualTo(1));
        }

        IEnumerator Pump(Func<bool> ready, string label, bool authority = true, Action service = null)
        {
            for (int frame = 0; frame < PumpLimit && !ready(); frame++)
            {
                _now += Dt; _terrain.Tick(authority, _now, Dt); service?.Invoke();
                Physics.SyncTransforms(); yield return null;
            }
            Assert.That(ready(), Is.True, label);
            Assert.That(_terrain.FailedTransactions, Is.Zero);
        }

        IEnumerator PayMain(Action<BlockAttackToken, PlayerSnapshot> paid)
        {
            var state = _host.Snapshot.Value; var weapon = GameplayConfig.GetWeapon(state.HeroId);
            Assert.That(state.WeaponPhase == WeaponPhase.Idle || state.WeaponPhase == WeaponPhase.Firing, Is.True);
            // Release through the same production step, then wait the actual configured cooldown.
            var input = new PlayerInputFrame { HeroRevision = state.HeroRevision, Revision = state.Revision,
                Sequence = state.AcknowledgedInput + 1, FireSequence = state.ConsumedFire,
                ReleaseSequence = state.ConsumedRelease + 1, Fire = false };
            for (int frame = 0; frame < 120 && (frame == 0 || state.WeaponPhase != WeaponPhase.Idle || _now < state.NextShotAt); frame++)
            {
                _now += Dt;
                Assert.That(WeaponSimulation.Step(ref state, input, weapon, _now, false, true), Is.False);
                _terrain.Tick(false, _now, Dt); yield return null;
            }
            Assert.That(state.WeaponPhase, Is.EqualTo(WeaponPhase.Idle));
            Assert.That(_now, Is.GreaterThanOrEqualTo(state.NextShotAt));
            input.Sequence++; input.FireSequence++; input.Fire = true;
            float material = state.Ink; uint sequence = state.ShotSequence; bool emitted = false;
            for (int frame = 0; frame < 120 && !emitted; frame++)
            {
                _now += Dt; emitted = WeaponSimulation.Step(ref state, input, weapon, _now, false, true);
                _terrain.Tick(false, _now, Dt); if (!emitted) yield return null;
            }
            Assert.That(emitted, Is.True, "Production WeaponSimulation accepts one configured paid emission");
            Assert.That(state.ShotSequence, Is.EqualTo(sequence + 1));
            Assert.That(material - state.Ink, Is.EqualTo(WeaponSimulation.InkCost(weapon, state.LastShotCharge)).Within(.001f));
            _host.Snapshot.Value = state;
            var token = BlockAttackService.Main(_host.PlayerId, state, _terrain.Round, weapon);
            Assert.That(token, Is.Not.Null); Assert.That(token.Credit.Kind, Is.EqualTo(PaintAttackKind.Main));
            Assert.That(token.Credit.Owner, Is.EqualTo(_host.PlayerId)); Assert.That(token.Credit.Team, Is.EqualTo(state.Team));
            Assert.That(BlockAttackService.Main(_host.PlayerId, state, _terrain.Round, weapon), Is.SameAs(token));
            paid(token, state);
        }

        InkShot MeasurementShot(BlockAttackToken token, PlayerSnapshot state, Vector3 origin, Vector3 direction)
        {
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            return new InkShot { Id = 960000 + ++_id, Round = _terrain.Round, Shooter = _host.PlayerId,
                Team = state.Team, HeroId = state.HeroId, Configuration = weapon, BlockAttack = token,
                Born = _now, Origin = origin, Velocity = direction.normalized * weapon.SpeedMin,
                Seed = 117, Charge = state.LastShotCharge, Lifecycle = state.Revision, HeroRevision = state.HeroRevision,
                ShotSequence = state.ShotSequence, ActionId = state.ShotActionId };
        }

        IEnumerator StablePaidEnemyFace()
        {
            yield return Install(false, 2);
            BlockAttackToken token = null; PlayerSnapshot payment = default;
            yield return PayMain((t, s) => { token = t; payment = s; });
            Vector3 min = (Vector3)_bottom * BlockWorld.CellSize;
            Vector3 origin = min + new Vector3(.05f, BlockWorld.CellSize, .05f);
            var solver = new TpsAimSolver();
            Assert.That(_terrain.TryOverlapStable(origin, GameplayConfig.GetWeapon(4).CollisionRadius, Vector3.down,
                out var exactCollider, out var exact, out _), Is.True);
            Assert.That(exact.Cell, Is.EqualTo(_bottom)); Assert.That(exact.Owner, Is.EqualTo(1));
            Assert.That(solver.Overlap(origin, GameplayConfig.GetWeapon(4).CollisionRadius, _host.PlayerId,
                Vector3.down, out var transported, false), Is.True);
            Assert.That(transported.Collider, Is.SameAs(exactCollider));
            Assert.That(transported.BlockQuery.IsKnown, Is.True);
            Assert.That(BlockAttackService.Contact(transported, out var resolved), Is.True);
            Assert.That(resolved.Cell, Is.EqualTo(_bottom)); Assert.That(resolved.Owner, Is.EqualTo(1));
            Assert.That(resolved.Stable, Is.True);
            Assert.That(_terrain.TryContact(transported.Collider, transported.Point, transported.Normal, out var legacy), Is.True);
            Assert.That(legacy.Cell, Is.EqualTo(_bottom + Vector3Int.up)); Assert.That(legacy.Owner, Is.EqualTo(2),
                "The independent point-only consumer witnesses the opposite branch at this shared face");
            var shot = MeasurementShot(token, payment, origin, Vector3.up);
            yield return ExecutePaidDirect(shot, transported, 2, 1);
            Assert.That(_terrain.World.TryGet(_bottom, out _), Is.False);
            Assert.That(_terrain.World.TryGet(_bottom + Vector3Int.up, out var friend), Is.True);
            Assert.That(friend.Owner, Is.EqualTo(2));
        }

        TpsCollision FallingQuery(out BlockFallingHit exact)
        {
            var run = _terrain.World.Falling.Values.Single(r => r.Cells.Count(c => c.Occupied) == 2);
            var lower = BlockWorld.Center(run.Bottom) - Vector3.up * run.Offset(_terrain.SimulationTime);
            var box = new Bounds(lower, Vector3.one * BlockWorld.CellSize);
            var upperBox = new Bounds(lower + Vector3.up * BlockWorld.CellSize, Vector3.one * BlockWorld.CellSize);
            // At 20m, center+.1 and the two .05 extents can leave a one-ULP
            // gap between adjacent source cells. Probe the real upper box's
            // bottom plane; do not pretend the rounded planes are identical.
            int gapUlps = Math.Abs(BitConverter.SingleToInt32Bits(box.max.y) - BitConverter.SingleToInt32Bits(upperBox.min.y));
            Assert.That(gapUlps, Is.LessThanOrEqualTo(1), "Adjacent source-cell geometry differs by at most the observed float rounding");
            Vector3 origin = new(.3f, upperBox.min.y, lower.z);
            float radius = GameplayConfig.GetWeapon(4).CollisionRadius;
            double lowerDy = Math.Max(0, (double)origin.y - box.max.y);
            Assert.That(lowerDy, Is.LessThan(radius));
            double lowerEntry = origin.x - (double)box.max.x - Math.Sqrt((double)radius * radius - lowerDy * lowerDy);
            double upperEntry = origin.x - (double)upperBox.max.x - radius;
            Assert.That(upperEntry, Is.LessThanOrEqualTo(lowerEntry), "The upper real cube is the nearest or tied sphere contact");
            Assert.That(_terrain.RaycastFalling(origin, Vector3.left, .5f, radius, out exact), Is.True);
            Assert.That(exact.Owner, Is.EqualTo(2)); Assert.That(exact.Cell, Is.EqualTo(run.Bottom + Vector3Int.up));
            Assert.That(exact.Distance, Is.EqualTo((float)upperEntry));
            Assert.That(exact.Point.x, Is.EqualTo(upperBox.max.x)); Assert.That(exact.Point.y, Is.EqualTo(upperBox.min.y));
            Assert.That(exact.Normal, Is.EqualTo(Vector3.right));
            Assert.That(Vector3.Distance(upperBox.ClosestPoint(exact.Point), exact.Point), Is.Zero);
            TestContext.WriteLine($"[CONTACT-INTERFACE] lowerMax={box.max.y:R} upperMin={upperBox.min.y:R} gapUlps={gapUlps} lowerEntry={lowerEntry:R} upperEntry={upperEntry:R}");
            var solver = new TpsAimSolver();
            Assert.That(solver.ClosestCast(origin, Vector3.left, .5f, radius, _host.PlayerId, out var result, false), Is.True);
            Assert.That(result.Collider, Is.SameAs(exact.Collider));
            Assert.That(result.BlockQuery.IsKnown, Is.True);
            Assert.That(BlockAttackService.Contact(result, out var resolved), Is.True);
            Assert.That(resolved.Owner, Is.EqualTo(2)); Assert.That(resolved.Cell, Is.EqualTo(exact.Cell));
            Assert.That(resolved.Stable, Is.False);
            return result;
        }

        IEnumerator FallingPaidEnemyFace()
        {
            yield return Install(true, 1);
            BlockAttackToken token = null; PlayerSnapshot payment = default;
            yield return PayMain((t, s) => { token = t; payment = s; });
            var transported = FallingQuery(out var exact);
            Assert.That(_terrain.TryContact(transported.Collider, transported.Point, transported.Normal, out var legacy), Is.True);
            Assert.That(legacy.Owner, Is.EqualTo(1)); Assert.That(legacy.Cell, Is.EqualTo(_bottom));
            Assert.That(transported.Collider.enabled, Is.False, "Falling proxy remains a shooting identity, not a locomotion collider");
            Assert.That(_terrain.TryGetGround(exact.Point + Vector3.up * .1f, out _), Is.False);
            Assert.That(Physics.Raycast(exact.Point + Vector3.right * .1f, Vector3.left, .2f,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.False);
            Vector3 origin = new(.3f, exact.Point.y, exact.Point.z);
            var shot = MeasurementShot(token, payment, origin, Vector3.left);
            Assert.That(InkBallistics.Position(shot, shot.Configuration, Dt).y, Is.EqualTo(origin.y),
                "Unchanged pistol ballistics preserve the exact shared plane during the measured first segment");
            yield return ExecutePaidDirect(shot, transported, 1, 2);
            Assert.That(_terrain.PinkCount, Is.EqualTo(1 + token.Added)); Assert.That(_terrain.BlueCount, Is.Zero);
        }

        IEnumerator ExecutePaidDirect(InkShot shot, TpsCollision expectedContact, byte friendlyTeam, byte enemyTeam)
        {
            string contactKind = expectedContact.Collider.enabled ? "stable" : "falling";
            var token = shot.BlockAttack; int pink = _terrain.PinkCount, blue = _terrain.BlueCount;
            uint paint = _match.PaintSequence; float paidMaterial = _host.Snapshot.Value.Ink;
            double points = _host.Snapshot.Value.SpecialPoints; int seen = 0;
            Action<InkShot, Collider, Vector3, Vector3, double> observe = (actual, collider, point, normal, age) =>
            {
                if (actual.Id != shot.Id) return;
                seen++;
                Assert.That(actual.BlockAttack, Is.SameAs(token));
                Assert.That(collider, Is.SameAs(expectedContact.Collider));
                Assert.That(Vector3.Distance(point, expectedContact.Point), Is.LessThan(.00001f));
                Assert.That(Vector3.Dot(normal, expectedContact.Normal), Is.GreaterThan(.999f));
            };
            _match.Projectiles.ContactObserved += observe;
            try
            {
                _match.Projectiles.SpawnForMeasurement(shot);
                // Query and production resolver consume one published geometry pose.
                // This controlled service test does not claim ordinary player aim.
                _match.Projectiles.Simulate(_now + Dt);
                Assert.That(seen, Is.EqualTo(1), "Actual TraceSegment resolves the expected shared-face collision once");
                Assert.That(token.DirectRemaining, Is.Zero,
                    "Direct reserves the weapon removal budget before the asynchronous result");
                Assert.That(token.BuildRemaining, Is.EqualTo(token.BuildLimit),
                    "At the actual enemy impact, no Build reservation is made; later paid foot drops retain their normal path");
                Assert.That(token.RemovedEnemy, Is.Zero); Assert.That(token.AwardedPoints, Is.Zero);
                Assert.That(_host.Snapshot.Value.SpecialPoints, Is.EqualTo(points), "Unpublished enemy removal pays nothing");
                yield return Pump(() => _match.Projectiles.PendingCount == 0 && _terrain.PendingTransactions == 0,
                    "Paid main and published direct removal complete", true, () => _match.Projectiles.Simulate(_now));
            }
            finally { _match.Projectiles.ContactObserved -= observe; }
            Assert.That(token.Added, Is.InRange(0, token.AuxiliaryLimit),
                "Normal paid foot drops may build on the distant map, using only the shared auxiliary allowance");
            Assert.That(token.RemovedEnemy, Is.EqualTo(1));
            Assert.That(token.DirectRemaining, Is.EqualTo(token.DirectLimit - 1));
            Assert.That(token.BuildRemaining, Is.EqualTo(token.BuildLimit - token.Added));
            Assert.That(_terrain.PinkCount, Is.EqualTo(pink - (enemyTeam == 1 ? 1 : 0) + (friendlyTeam == 1 ? token.Added : 0)));
            Assert.That(_terrain.BlueCount, Is.EqualTo(blue - (enemyTeam == 2 ? 1 : 0) + (friendlyTeam == 2 ? token.Added : 0)));
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(pink + blue - 1 + token.Added));
            Assert.That(_terrain.World.PinkCount + _terrain.World.BlueCount, Is.EqualTo(1 + token.Added),
                "Only published paid foot construction and the enemy deletion change count; falling/settled transitions conserve it");
            Assert.That(token.AwardedPoints, Is.EqualTo(Math.Min(token.PointLimit, (token.Added + 1d) / token.BlocksPerPoint)).Within(1e-8));
            Assert.That(_host.Snapshot.Value.SpecialPoints - points, Is.EqualTo(token.AwardedPoints).Within(1e-8));
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidMaterial));
            Assert.That(_match.State.Value.PinkBlockCount, Is.EqualTo(_terrain.PinkCount));
            Assert.That(_match.State.Value.BlueBlockCount, Is.EqualTo(_terrain.BlueCount));
            Assert.That(_match.State.Value.TerrainRevision, Is.EqualTo(_terrain.Revision));
            Assert.That(_match.PaintSequence, Is.EqualTo(paint));
            Assert.That(_match.Projectiles.Impacts.Single(i => i.Id == shot.Id).Damage, Is.Zero);
            int removed = token.RemovedEnemy; double awarded = token.AwardedPoints;
            _match.Projectiles.Simulate(_now + Dt);
            Assert.That(token.RemovedEnemy, Is.EqualTo(removed)); Assert.That(token.AwardedPoints, Is.EqualTo(awarded));
            TestContext.WriteLine($"[CONTACT-PAID] kind={contactKind} friendly={friendlyTeam} enemy={enemyTeam} paid={WeaponSimulation.InkCost(shot.Configuration, shot.Charge):R} added={token.Added} removed={token.RemovedEnemy} points={token.AwardedPoints:R} revision={_terrain.Revision}");
        }

        IEnumerator StaleStableClearAndReplacement()
        {
            yield return Install(false, 2);
            Vector3 origin = (Vector3)_bottom * BlockWorld.CellSize + new Vector3(.05f, .1f, .05f);
            var solver = new TpsAimSolver();
            Assert.That(solver.Overlap(origin, .01f, _host.PlayerId, Vector3.down, out var old, false), Is.True);
            var oldWorld = _terrain.World;
            Assert.That(old.BlockQuery.TryResolveCurrent(out _), Is.True);
            _terrain.Clear(_terrain.Round);
            Assert.That(old.BlockQuery.IsKnown, Is.True); Assert.That(old.BlockQuery.TryResolveCurrent(out _), Is.False);
            Assert.That(BlockAttackService.Contact(old, out _), Is.False, "Clear invalidates known identity rather than requesting point reconstruction");
            var seed = Empty(); seed.Set(_bottom, BlockCell.Create(1, true)); seed.Set(_bottom + Vector3Int.up, BlockCell.Create(2, true));
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, "Same cells rebuilt via a distinct published world", false);
            Assert.That(_terrain.World, Is.Not.SameAs(oldWorld));
            Assert.That(old.BlockQuery.IsKnown, Is.True); Assert.That(old.BlockQuery.TryResolveCurrent(out _), Is.False);
            Assert.That(BlockAttackService.Contact(old, out _), Is.False);
            Assert.That(solver.Overlap(origin, .01f, _host.PlayerId, Vector3.down, out var fresh, false), Is.True);
            Assert.That(BlockAttackService.Contact(fresh, out var current), Is.True); Assert.That(current.Cell, Is.EqualTo(_bottom));
        }

        IEnumerator WaveBreakerEmbeddedExactAttachment()
        {
            _match.Projectiles.Clear(); _match.SpecialWeapons.Clear(); _match.SubWeapons.Clear();
            BlockAttackService.Clear(); _terrain.Clear(_terrain.Round); Place(1);
            var support = _bottom + Vector3Int.up;
            var seed = Empty(); Assert.That(seed.Set(support, BlockCell.Create(1, true)), Is.True);
            Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return Pump(() => _terrain.PendingTransactions == 0, "Isolated synthetic WaveBreaker support published", false);

            var state = _host.Snapshot.Value; state.SpecialWeaponId = 3;
            var config = SpecialWeaponConfigService.Current.Get(state.SpecialWeaponId);
            Assert.That(config.Type, Is.EqualTo(SpecialWeaponType.WaveBreaker));
            float cost = PlayerLoadout.From(state).RequiredPoints;
            // Explicit readiness fixture. This case does not claim these points
            // were earned by construction; normal Step must still consume the
            // actual configured cost and commit exactly one throw.
            state.SpecialPoints = cost;
            var motor = (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(_host);
            bool canStand = motor.CanStand(state.Position); Assert.That(canStand, Is.True);
            var input = new PlayerInputFrame { HeroRevision = state.HeroRevision, Revision = state.Revision,
                Sequence = state.AcknowledgedInput + 1, SpecialSequence = state.SpecialConsumed + 1,
                FireSequence = state.SpecialConsumedFire, ReleaseSequence = state.SpecialConsumedRelease };
            uint action = state.SpecialAction, attack = state.SpecialAttack;
            Assert.That(SpecialWeaponSimulation.Step(ref state, input, config, cost,
                GameplayConfig.GetHero(state.HeroId).MaxInk, canStand, _now), Is.False);
            Assert.That(state.SpecialAction, Is.EqualTo(action + 1)); Assert.That(state.SpecialPoints, Is.Zero);
            for (int frame = 0; frame < 120 && state.SpecialPhase != SpecialPhase.Active; frame++)
            {
                _now += Dt;
                Assert.That(SpecialWeaponSimulation.Step(ref state, input, config, cost,
                    GameplayConfig.GetHero(state.HeroId).MaxInk, canStand, _now), Is.False);
                _terrain.Tick(false, _now, Dt); yield return null;
            }
            Assert.That(state.SpecialPhase, Is.EqualTo(SpecialPhase.Active));
            input.Fire = true; input.FireSequence++; _now += Dt;
            Assert.That(SpecialWeaponSimulation.Step(ref state, input, config, cost,
                GameplayConfig.GetHero(state.HeroId).MaxInk, canStand, _now), Is.False);
            Assert.That(state.SpecialAiming, Is.True);
            input.Fire = false; input.ReleaseSequence++; _now += Dt;
            Assert.That(SpecialWeaponSimulation.Step(ref state, input, config, cost,
                GameplayConfig.GetHero(state.HeroId).MaxInk, canStand, _now), Is.False);
            bool committed = false;
            for (int frame = 0; frame < 120 && !committed; frame++)
            {
                _now += Dt; committed = SpecialWeaponSimulation.Step(ref state, input, config, cost,
                    GameplayConfig.GetHero(state.HeroId).MaxInk, canStand, _now);
                _terrain.Tick(false, _now, Dt); if (!committed) yield return null;
            }
            Assert.That(committed, Is.True); Assert.That(state.SpecialAttack, Is.EqualTo(attack + 1));
            Assert.That(state.SpecialPoints, Is.Zero); _host.Snapshot.Value = state;
            var entity = _match.SpecialWeapons.Spawn(_host, state, _now, _terrain.Round);
            Assert.That(entity, Is.Not.Null); Assert.That(entity.BlockAttack, Is.Not.Null);
            Assert.That(entity.BlockAttack.PointLimit, Is.Zero);

            Vector3 origin = (Vector3)support * BlockWorld.CellSize + new Vector3(.05f, .01f, .05f);
            Vector3 velocity = Vector3.down * 2;
            entity.State.Position = entity.State.P0 = entity.State.P1 = entity.State.P2 = origin;
            entity.State.Velocity = velocity; // Explicit measured deployment geometry, not ordinary player aiming.
            Assert.That(SpecialWeaponService.ThrowCollision(config, origin, Vector3.down * .01f, out var known), Is.True);
            Assert.That(known.BlockQuery.IsKnown, Is.True);
            Assert.That(BlockAttackService.Contact(known, out var exact), Is.True);
            Assert.That(exact.Stable, Is.True); Assert.That(exact.Cell, Is.EqualTo(support));
            Vector3 preview = origin, previewVelocity = velocity;
            Assert.That(SpecialWeaponService.AdvanceThrow(config, ref preview, ref previewVelocity, out var normal, Dt), Is.True);
            Assert.That(normal, Is.EqualTo(Vector3.up));
            var oldGuessedCell = BlockWorld.CellAt(preview - normal * .05f);
            Assert.That(oldGuessedCell, Is.EqualTo(support + Vector3Int.down));
            Assert.That(_terrain.World.TryGet(oldGuessedCell, out _), Is.False,
                "Old StepThrow's reconstructed support is an empty neighbour");
            var before = _terrain.ExportSnapshot();
            _now += Dt; _terrain.Tick(false, _now, Dt);
            _match.SpecialWeapons.Step(_now, Dt, _match.Players);
            Assert.That(entity.State.Phase, Is.EqualTo(SpecialEntityPhase.Active));
            Assert.That(entity.BlockAttachment, Is.True); Assert.That(entity.BlockAttachmentCell, Is.EqualTo(support));
            Assert.That(entity.Target, Is.Not.Null); Assert.That(entity.State.Pulse, Is.Zero);
            CollectionAssert.AreEqual(before, _terrain.ExportSnapshot(), "Deployment does not generate or delete terrain");

            bool deleted = false;
            // Diagnostic terrain deletion, explicitly not an additional paid
            // attack claim. The new Main paid cases above test the payment path.
            _terrain.QueueDestroy(BlockWorld.Center(support), .2f, 0, 2, true,
                result => { Assert.That(result.RemovedEnemy, Is.EqualTo(1)); deleted = true; }, 1, 0);
            yield return Pump(() => deleted && _terrain.PendingTransactions == 0, "Actual occupied support deletion publishes", true,
                () => _match.SpecialWeapons.Step(_now, Dt, _match.Players));
            Assert.That(_terrain.World.TryGet(support, out _), Is.False);
            _now += Dt; _match.SpecialWeapons.Step(_now, Dt, _match.Players);
            Assert.That(entity.BlockAttachment, Is.False); Assert.That(entity.State.Health, Is.Zero);
            Assert.That(entity.Target == null, Is.True, "The generator retires after its exact occupied support is removed");
            if (entity.State.Pulse == 0)
                Assert.That(entity.Removed, Is.True, "Without an emitted pulse, exact-support destruction retires the whole entity");
            // Existing emitted waves may finish after the generator is destroyed;
            // target retirement and health=0 above remain mandatory in either case.
            Assert.That(entity.BlockAttack.Added + entity.BlockAttack.RemovedEnemy, Is.Zero);
            Assert.That(entity.BlockAttack.AwardedPoints, Is.Zero); Assert.That(_host.Snapshot.Value.SpecialPoints, Is.Zero);
        }

        IEnumerator StaleFallingTime()
        {
            yield return Install(true, 1);
            var old = FallingQuery(out var exact);
            var sameWorld = _terrain.World; uint revision = _terrain.Revision;
            int count = _terrain.PinkCount + _terrain.BlueCount;
            _now += Dt; _terrain.Tick(false, _now, Dt);
            Assert.That(_terrain.World, Is.SameAs(sameWorld)); Assert.That(_terrain.Revision, Is.EqualTo(revision));
            Assert.That(_terrain.World.Falling[exact.RunId].Cells[1].Occupied, Is.True);
            Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.EqualTo(count));
            Assert.That(_terrain.TryContact(old.Collider, old.Point, old.Normal, out _), Is.True,
                "The legacy geometry still accepts this point, so fallback would hide the stale-time failure");
            Assert.That(old.BlockQuery.IsKnown, Is.True); Assert.That(old.BlockQuery.TryResolveCurrent(out _), Is.False);
            Assert.That(BlockAttackService.Contact(old, out _), Is.False, "Known stale falling contact must not fall back through the shared proxy");
            FallingQuery(out _); // A newly captured pose remains usable.
        }

        IEnumerator Leave()
        {
            if (_match != null)
            {
                if (_serverTick != null && _match.NetworkManager.NetworkTickSystem != null)
                    _match.NetworkManager.NetworkTickSystem.Tick += _serverTick;
                _serverTick = null;
                _match.Projectiles.Clear(); _match.SubWeapons.Clear(); _match.SpecialWeapons.Clear();
            }
            BlockAttackService.Clear();
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom)
                yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return Wait(() => BlockTerrain.Current == null, "Host terrain destroyed after leave");
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            yield return Leave();
            yield return new ExitPlayMode();
        }
    }
}
#endif
