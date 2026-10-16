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
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>Paid default weapon services against published block collision, without legacy paint fixtures.</summary>
    public sealed class BlockWeaponIntegrationTests
    {
        const float Dt = 1f / 60;
        PrototypeApp _app;
        PrototypeMatch _match;
        PrototypePlayer _host, _enemy;
        BlockTerrain _terrain;
        Vector3 _floor;
        double _now;
        uint _shotId;
        Action _serverTick;

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(ready(), Is.True, label);
        }
        IEnumerator Setup()
        {
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot");
            _app = PrototypeApp.Current; yield return _app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks != null, "Block host");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local; _terrain = _match.Blocks;
            _enemy = _match.AddTestBot(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab"));
            Assert.That(_enemy, Is.Not.Null); _app.CaptureMouse(false); _match.enabled = false;
            // Disabling a component stops FixedUpdate, not NGO's event subscription.
            // Keep real authority collision/effect events until their assertions inspect them.
            _serverTick = (Action)Delegate.CreateDelegate(typeof(Action), _match,
                typeof(PrototypeMatch).GetMethod("ServerTick", BindingFlags.Instance | BindingFlags.NonPublic));
            _match.NetworkManager.NetworkTickSystem.Tick -= _serverTick;
            foreach (var player in _match.Players) player.enabled = false;
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out var floor, 10, PlayerMotorSimulation.WorldMask), Is.True);
            _floor = floor.point; _now = _match.NetworkManager.ServerTime.Time;
            Place(_host, _floor + Vector3.back * 8, 1); Place(_enemy, _floor + Vector3.right * 20, 2);
            Assert.That(BlockAttackService.Active, Is.True);
        }
        IEnumerator Run(Func<IEnumerator> scenario)
        {
            yield return Setup();
            yield return scenario();
            yield return LeaveRoom();
        }
        IEnumerator LeaveRoom()
        {
            if (_match != null)
            {
                if (_serverTick != null && _match.NetworkManager.NetworkTickSystem != null)
                    _match.NetworkManager.NetworkTickSystem.Tick += _serverTick;
                _serverTick = null;
                _match.Projectiles.Clear(); _match.SubWeapons.Clear(); _match.SpecialWeapons.Clear();
            }
            BlockAttackService.Clear();
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
        }
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            yield return LeaveRoom();
            yield return new ExitPlayMode();
        }
        void Place(PrototypePlayer player, Vector3 at, byte team)
        {
            var s = player.Snapshot.Value; s.Position = at; s.Health = s.Ink = 100; s.Team = team;
            s.LifeState = PlayerLifeState.Alive; s.ProtectedUntil = 0; s.Swimming = s.CompactBody = false;
            s.PaperPose = PaperPose.None; s.Grounded = true; s.AirHumanOffset = 0; s.VerticalSpeed = 0;
            s.SpecialPhase = SpecialPhase.Charging; s.SpecialChargeLockedUntil = 0; s.SpecialPoints = 0;
            s.MarkedUntilPink = s.MarkedUntilBlue = s.MistUntil = s.MistExposure = 0; s.Yaw = s.Pitch = 0;
            var controller = player.GetComponent<CharacterController>(); controller.enabled = false;
            player.transform.position = at; controller.enabled = true; player.Snapshot.Value = s;
            player.SwimBody?.ApplyCollision(s); Physics.SyncTransforms();
        }
        IEnumerator PumpUntil(Func<bool> ready, string label, Action simulate = null, int maximumFrames = 1800)
        {
            for (int frame = 0; frame < maximumFrames && !ready(); frame++)
            {
                _now += Dt; simulate?.Invoke(); _terrain.Tick(true, _now, Dt); Physics.SyncTransforms(); yield return null;
            }
            Assert.That(ready(), Is.True, label);
        }
        IEnumerator Step(float seconds, Action simulate)
        {
            for (int frame = 0; frame < Mathf.CeilToInt(seconds / Dt); frame++)
            { _now += Dt; simulate(); _terrain.Tick(true, _now, Dt); Physics.SyncTransforms(); yield return null; }
            yield return PumpUntil(() => _terrain.PendingTransactions == 0, "Terrain callbacks drained");
        }
        IEnumerator Install(BlockWorld seed)
        {
            _match.Projectiles.Clear(); _match.SubWeapons.Clear(); _match.SpecialWeapons.Clear(); BlockAttackService.Clear();
            _terrain.Clear(_match.State.Value.Round); Assert.That(_terrain.QueueSeed(seed), Is.True);
            yield return PumpUntil(() => _terrain.PendingTransactions == 0, "Seed collision published");
            Assert.That(_terrain.PinkCount, Is.EqualTo(seed.PinkCount), "Published fixture retains all friendly cells");
            Assert.That(_terrain.BlueCount, Is.EqualTo(seed.BlueCount), "Published fixture retains all enemy cells");
            Place(_host, _floor + Vector3.back * 8, 1); Place(_enemy, _floor + Vector3.right * 20, 2);
        }
        BlockWorld Empty() => new(_terrain.World.Dimensions, _match.State.Value.Round, _terrain.World.Bottom);
        BlockWorld Wall(byte team)
        {
            var seed = Empty(); var cell = BlockWorld.CellAt(_floor + Vector3.up * 1.2f);
            for (int x = -12; x <= 12; x++) for (int y = -9; y <= 9; y++)
                seed.Set(cell + new Vector3Int(x, y, 0), BlockCell.Create(team, true));
            return seed;
        }
        BlockWorld Platform(bool mixed = true, float height = .05f)
        {
            var seed = Empty(); var cell = BlockWorld.CellAt(_floor + Vector3.up * height);
            for (int x = -12; x <= 12; x++) for (int z = -12; z <= 12; z++)
                seed.Set(cell + new Vector3Int(x, 0, z), BlockCell.Create(mixed && (x + z) % 2 != 0 ? (byte)2 : (byte)1, true));
            return seed;
        }
        InkShot Main(int hero, Vector3 origin, Vector3 direction)
        {
            var s = _host.Snapshot.Value; s.HeroId = hero; s.ShotSequence++; s.FireBurstSequence = s.ShotSequence;
            s.LastShotCharge = 1; s.HeroRevision++; s.SpecialWeaponId = 1;
            var weapon = GameplayConfig.GetWeapon(hero); s.Ink -= WeaponSimulation.InkCost(weapon, s.LastShotCharge); _host.Snapshot.Value = s;
            var token = BlockAttackService.Main(_host.PlayerId, s, _match.State.Value.Round, weapon);
            var shot = new InkShot { Id = ++_shotId + 900000, Round = _match.State.Value.Round, Shooter = _host.PlayerId,
                HeroId = hero, Team = s.Team, Born = _now, Origin = origin, Velocity = direction.normalized * 32,
                Lifecycle = s.Revision, HeroRevision = s.HeroRevision, ShotSequence = s.ShotSequence, ActionId = s.ShotSequence,
                Configuration = weapon, BlockAttack = token, Seed = 117, Charge = 1 };
            _match.Projectiles.SpawnForMeasurement(shot); return shot;
        }
        void Projectiles() => _match.Projectiles.Simulate(_now);
        void Subs() => _match.SubWeapons.Step(_now, Dt, _match.Players);
        void Specials() => _match.SpecialWeapons.Step(_now, Dt, _match.Players);
        SubWeaponService.Entity Sub(SubWeaponType type, Vector3 at, Vector3 velocity, Collider surface = null)
        {
            var s = _host.Snapshot.Value; s.HeroId = 1; s.SubWeaponId = (int)type + 1; s.SubAction++;
            var config = PlayerLoadout.SubWeapon(s); s.Ink = 100 - config.Common.inkCost; _host.Snapshot.Value = s;
            var launch = new SubLaunchSolution { Position = at, Velocity = velocity, Normal = Vector3.up, Surface = surface };
            var entity = _match.SubWeapons.Spawn(_host, s, type == SubWeaponType.FizzyBomb ? 1 : 0, _now, _match.State.Value.Round, launch);
            Assert.That(entity, Is.Not.Null, type + " accepted deployment"); Assert.That(entity.BlockAttack, Is.Not.Null);
            Assert.That(_match.SubWeapons.Lifecycle.Any(e => e.Kind == SubLifecycleKind.Spawn && e.State.Id == entity.State.Id), Is.True);
            return entity;
        }
        SpecialWeaponService.Entity Special(int id, Vector3 at, Vector3 velocity)
        {
            var s = _host.Snapshot.Value; s.SpecialWeaponId = id; s.SpecialAction++; s.SpecialAttack++; _host.Snapshot.Value = s;
            var entity = _match.SpecialWeapons.Spawn(_host, s, _now, _match.State.Value.Round);
            if (id != (int)SpecialWeaponType.Reefslider)
            { entity.State.Position = entity.State.P0 = entity.State.P1 = entity.State.P2 = at; entity.State.Velocity = velocity; }
            Assert.That(entity.BlockAttack, Is.Not.Null);
            Assert.That(_match.SpecialWeapons.Lifecycle.Any(e => e.State.Id == entity.State.Id), Is.True);
            return entity;
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator AllNinePaidMainsRouteFriendlyConstructionAndEnemyRemovalThroughRealCollision()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return Run(MainCollisionScenario);
            yield return new ExitPlayMode();
        }
        IEnumerator MainCollisionScenario()
        {
            for (int hero = 1; hero <= 9; hero++) for (byte targetTeam = 1; targetTeam <= 2; targetTeam++)
            {
                yield return Install(Wall(targetTeam));
                var wallCell = BlockWorld.CellAt(_floor + Vector3.up * 1.2f); var center = BlockWorld.Center(wallCell);
                Assert.That(Physics.Raycast(center + Vector3.back * 1.2f, Vector3.forward, out var wallHit, 2,
                    1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore), Is.True, "Actual published wall in the projectile path");
                Assert.That(_terrain.TryContact(wallHit, out var wallContact), Is.True);
                Assert.That(wallContact.Owner, Is.EqualTo(targetTeam));
                bool explosive = hero == 3 || hero == 5 || hero == 9;
                if (!explosive && targetTeam == 2) Place(_enemy, _floor + Vector3.forward * 2, 2);
                int pink = _terrain.PinkCount, blue = _terrain.BlueCount; uint paint = _match.PaintSequence;
                var shot = Main(hero, center + Vector3.back * 1.2f, Vector3.forward); var token = shot.BlockAttack;
                float paidInk = _host.Snapshot.Value.Ink;
                yield return PumpUntil(() => _match.Projectiles.PendingCount == 0 && _terrain.PendingTransactions == 0,
                    "Main " + hero + " completes its collision and terrain work", Projectiles);
                Assert.That(_match.Projectiles.Impacts.Any(e => e.Id == shot.Id && e.Hit), Is.True, "Main " + hero + " actually contacts terrain");
                Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidInk), "Terrain does not charge the shot twice");
                Assert.That(_match.PaintSequence, Is.EqualTo(paint), "Default route never emits old paint stamps");
                if (explosive)
                {
                    Assert.That(token.Added, Is.Zero, "Explosive main " + hero + " never constructs cubes");
                    Assert.That(_terrain.PinkCount + _terrain.BlueCount, Is.LessThan(pink + blue), "Explosive collision removes published cubes");
                    Assert.That(_match.Projectiles.Explosions.Any(e => e.ShotId == shot.Id), Is.True);
                }
                else if (targetTeam == 1)
                {
                    Assert.That(token.Added, Is.InRange(1, token.BuildLimit));
                    Assert.That(_terrain.PinkCount - pink, Is.EqualTo(token.Added));
                    Assert.That(_terrain.BlueCount, Is.EqualTo(blue));
                }
                else
                {
                    Assert.That(token.RemovedEnemy, Is.InRange(1, token.DirectLimit));
                    Assert.That(blue - _terrain.BlueCount, Is.EqualTo(token.RemovedEnemy));
                    Assert.That(_terrain.World.TryGet(wallCell, out _), Is.False, "The cell at the actual contact is among the nearest removals");
                    Assert.That(_terrain.World.TryGet(wallCell + Vector3Int.right * 12, out _), Is.True, "Distant enemy cells remain");
                    Assert.That(_enemy.Snapshot.Value.Health, Is.EqualTo(100), "Main " + hero + " ends on enemy cubes and cannot hit the player behind them");
                    Assert.That(_match.Projectiles.Impacts.Any(e => e.Id == shot.Id && e.Damage > 0), Is.False);
                }
                Assert.That(token.AwardedPoints, Is.LessThanOrEqualTo(BlockGameplaySettings.MainSpecialPointCap));
            }
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator EightExplosiveSubsPublishBlastsAndRemoveEnemyTerrainWithoutExplosionConstruction()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return Run(ExplosiveSubScenario);
            yield return new ExitPlayMode();
        }
        IEnumerator ExplosiveSubScenario()
        {
            var types = new[] { SubWeaponType.SplatBomb, SubWeaponType.SuctionBomb, SubWeaponType.BurstBomb, SubWeaponType.CurlingBomb,
                SubWeaponType.Autobomb, SubWeaponType.FizzyBomb, SubWeaponType.Torpedo, SubWeaponType.InkMine };
            foreach (var type in types)
            {
                yield return Install(Platform()); int blue = _terrain.BlueCount;
                var at = BlockWorld.Center(BlockWorld.CellAt(_floor + Vector3.up * .05f)) + Vector3.up * .05f;
                Collider support = null;
                if (type == SubWeaponType.InkMine)
                {
                    Place(_host, at + Vector3.up * .02f, 1);
                    Assert.That(SubWeaponService.TryMineGround(_host.Snapshot.Value, out var hit), Is.True);
                    at = hit.point; support = hit.collider;
                }
                var entity = Sub(type, at + (type == SubWeaponType.InkMine ? Vector3.zero : Vector3.up * .6f), Vector3.down * 6, support);
                if (type == SubWeaponType.InkMine) Place(_enemy, at + Vector3.right, 2);
                yield return PumpUntil(() => _match.SubWeapons.Effects.Any(e => e.EntityId == entity.State.Id && e.Kind == SubEffectKind.Explosion),
                    type + " publishes its actual explosion", Subs);
                // Terrain publication is asynchronous. Keep the real weapon clock
                // advancing too, so delayed Fizzy bursts are not frozen until expiry.
                yield return PumpUntil(() => _terrain.PendingTransactions == 0, type + " blast terrain publishes", Subs);
                Assert.That(_terrain.BlueCount, Is.LessThan(blue), type + " destroys enemy cubes");
                Assert.That(entity.BlockAttack.RemovedEnemy, Is.GreaterThan(0));
                // Curling/Fizzy can have legal paid travel trails; the blast itself cannot add cells.
                if (type != SubWeaponType.CurlingBomb && type != SubWeaponType.FizzyBomb)
                    Assert.That(entity.BlockAttack.Added, Is.Zero, type + " explosion does not construct terrain");
                Assert.That(entity.BlockAttack.Added, Is.LessThanOrEqualTo(entity.BlockAttack.BuildLimit));
                Assert.That(entity.BlockAttack.AwardedPoints, Is.LessThanOrEqualTo(BlockGameplaySettings.SubSpecialPointCap));
                if (type == SubWeaponType.FizzyBomb)
                {
                    yield return PumpUntil(() => entity.Removed && _terrain.PendingTransactions == 0, "All charged Fizzy bursts finish", Subs);
                    Assert.That(_match.SubWeapons.Effects.Count(e => e.EntityId == entity.State.Id && e.Kind == SubEffectKind.Explosion), Is.EqualTo(3),
                        $"Full-charge Fizzy: charge={entity.State.Charge}, completed={entity.State.Explosions}, born={entity.State.Born:R}, expires={entity.State.Expires:R}, now={_now:R}");
                }
            }
        }

        // Includes Editor Play Mode reloads; scenario frame limits and assertions remain unchanged.
        [UnityTest, Timeout(600000)]
        public IEnumerator SensorMistAngleSprinklerAndWallKeepTheirDistinctNonexplosiveRoutes()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return Run(NonexplosiveSubScenario);
            yield return new ExitPlayMode();
        }
        IEnumerator NonexplosiveSubScenario()
        {
            foreach (var type in new[] { SubWeaponType.PointSensor, SubWeaponType.ToxicMist })
            {
                yield return Install(Platform()); var before = _terrain.ExportSnapshot();
                Place(_enemy, _floor + Vector3.right * 2, 2);
                var entity = Sub(type, _floor + Vector3.up * .6f, Vector3.down * 6);
                yield return Step(1.5f, Subs);
                Assert.That(entity.State.Phase, Is.EqualTo(SubEntityPhase.Active));
                Assert.That(_terrain.ExportSnapshot(), Is.EqualTo(before), type + " changes status without changing terrain");
                Assert.That(_enemy.Snapshot.Value.Health, Is.EqualTo(100));
                if (type == SubWeaponType.PointSensor) Assert.That(_enemy.Snapshot.Value.MarkedUntilPink, Is.GreaterThan(_now));
                else { Assert.That(_enemy.Snapshot.Value.Ink, Is.LessThan(100)); Assert.That(_enemy.Snapshot.Value.MistMoveRate, Is.LessThan(1)); }
            }
            foreach (byte targetTeam in new byte[] { 1, 2 })
            {
                yield return Install(Wall(targetTeam)); var center = BlockWorld.Center(BlockWorld.CellAt(_floor + Vector3.up * 1.2f));
                int blue = _terrain.BlueCount; var angle = Sub(SubWeaponType.AngleShooter, center + Vector3.back, Vector3.forward * 250);
                yield return PumpUntil(() => angle.Removed && _terrain.PendingTransactions == 0, "Angle contact concludes", Subs);
                if (targetTeam == 1) Assert.That(angle.BlockAttack.Added, Is.GreaterThan(0));
                else { Assert.That(blue - _terrain.BlueCount, Is.EqualTo(angle.BlockAttack.RemovedEnemy)); Assert.That(angle.BlockAttack.RemovedEnemy, Is.GreaterThan(0)); }
                Assert.That(_match.SubWeapons.Effects.Any(e => e.Kind == SubEffectKind.Explosion), Is.False);
            }
            yield return Install(Platform(false));
            var sprinkler = Sub(SubWeaponType.Sprinkler, _floor + Vector3.up * .6f, Vector3.down * 6);
            yield return PumpUntil(() => sprinkler.BlockAttack.Added > 0 && _terrain.PendingTransactions == 0, "Sprinkler deploys and constructs paid cubes", Subs);
            Assert.That(sprinkler.Removed, Is.False); float paidInk = _host.Snapshot.Value.Ink;
            yield return Step(12, Subs);
            Assert.That(sprinkler.BlockAttack.Added, Is.InRange(1, sprinkler.BlockAttack.BuildLimit));
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(paidInk), "Persistent pulses share one paid deployment");
            Assert.That(sprinkler.BlockAttack.AwardedPoints, Is.LessThanOrEqualTo(BlockGameplaySettings.SubSpecialPointCap));
            yield return Install(Platform(false));
            var wall = Sub(SubWeaponType.SplashWall, _floor + Vector3.up * .6f, Vector3.down * 6);
            yield return PumpUntil(() => wall.State.Phase == SubEntityPhase.Active && _terrain.PendingTransactions == 0, "Wall deploys", Subs);
            Assert.That(wall.HitTarget, Is.Not.Null); Assert.That(wall.BlockAttack.Added, Is.InRange(1, wall.BlockAttack.BuildLimit));
            yield return PumpUntil(() => wall.Removed, "Wall lifetime is finite", Subs);
            Assert.That(_match.SubWeapons.Lifecycle.Any(e => e.State.Id == wall.State.Id && e.Kind == SubLifecycleKind.Remove), Is.True);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator BlockDeploymentsSurviveChunkReplacementAndReactToTheirExactSupportCell()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return Run(AttachmentScenario);
            yield return new ExitPlayMode();
        }
        IEnumerator AttachmentScenario()
        {
            foreach (var type in new[] { SubWeaponType.SuctionBomb, SubWeaponType.InkMine, SubWeaponType.Sprinkler, SubWeaponType.SplashWall })
            {
                yield return Install(Platform(false, 1.55f));
                Assert.That(Physics.Raycast(_floor + Vector3.up * 3, Vector3.down, out var hit, 4, 1 << BlockTerrain.TerrainLayer), Is.True);
                var contact = hit.point;
                if (type == SubWeaponType.InkMine) Place(_host, contact + Vector3.up * .02f, 1);
                var entity = Sub(type, contact + (type == SubWeaponType.InkMine ? Vector3.zero : Vector3.up * .4f), Vector3.down * 6,
                    type == SubWeaponType.InkMine ? hit.collider : null);
                yield return PumpUntil(() => entity.BlockAttachment && _terrain.PendingTransactions == 0, type + " attaches to a published block", Subs);
                var cell = entity.BlockAttachmentCell; var at = entity.State.Position;
                var replacement = _terrain.World.Clone(); replacement.Set(cell + Vector3Int.right * 3, BlockCell.Create(2, true));
                Assert.That(_terrain.QueueSeed(replacement), Is.True);
                yield return PumpUntil(() => _terrain.PendingTransactions == 0, "Same chunk replacement publishes");
                _now += Dt; Subs();
                Assert.That(entity.Removed, Is.False, type + " retains a surviving support cell after the old chunk Transform is retired");
                Assert.That(entity.State.Position, Is.EqualTo(at)); Assert.That(_terrain.World.TryGet(cell, out _), Is.True);
                var cut = _terrain.World.Clone();
                // Clear the contact envelope too, so a detached suction bomb cannot
                // immediately catch an adjacent surviving face during this same step.
                for (int x = -4; x <= 4; x++) for (int y = -4; y <= 4; y++) for (int z = -4; z <= 4; z++)
                    cut.Set(cell + new Vector3Int(x, y, z), default);
                Assert.That(_terrain.QueueSeed(cut), Is.True);
                yield return PumpUntil(() => _terrain.PendingTransactions == 0, "Exact attachment cell removal publishes");
                _now += Dt; Subs();
                if (type == SubWeaponType.SuctionBomb)
                { Assert.That(entity.BlockAttachment, Is.False); Assert.That(entity.Attached, Is.False); Assert.That(entity.Removed, Is.False); Assert.That(entity.State.Phase, Is.EqualTo(SubEntityPhase.Flying)); }
                else Assert.That(entity.Removed, Is.True, type + " is removed when its support cell disappears");
            }
            yield return Install(Empty());
            var mapSurface = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                mapSurface.name = "Static attachment transform fixture";
                mapSurface.transform.position = _floor + Vector3.up * 2;
                mapSurface.transform.localScale = new Vector3(1, .2f, 1); Physics.SyncTransforms();
                var bomb = Sub(SubWeaponType.SuctionBomb, _floor + Vector3.up * 2.5f, Vector3.down * 6);
                yield return PumpUntil(() => bomb.Attached && !bomb.BlockAttachment, "Suction attaches to static-map Transform", Subs);
                var originalPosition = bomb.State.Position; var translation = Vector3.right * .25f;
                mapSurface.transform.position += translation; Physics.SyncTransforms(); _now += Dt; Subs();
                Assert.That(bomb.Removed, Is.False);
                Assert.That(Vector3.Distance(bomb.State.Position, originalPosition + translation), Is.LessThan(.0001f),
                    "Static-map attachments continue to follow their retained Transform");
            }
            finally { UnityEngine.Object.Destroy(mapSurface); }
        }

        // Includes Editor Play Mode reloads; scenario frame limits and assertions remain unchanged.
        [UnityTest, Timeout(600000)]
        public IEnumerator AllFiveSpecialServicesSeparateExplosionsSonarAndFiniteRainConstruction()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return Run(SpecialScenario);
            yield return new ExitPlayMode();
        }
        IEnumerator SpecialScenario()
        {
            foreach (int id in new[] { 1, 2, 5 })
            {
                yield return Install(Platform()); int blue = _terrain.BlueCount;
                var point = _floor + Vector3.up * .6f;
                if (id == 5) Place(_host, _floor + Vector3.up * .12f, 1);
                var entity = Special(id, point, Vector3.down * 32);
                yield return PumpUntil(() => entity.BlockAttack.RemovedEnemy > 0 && _terrain.PendingTransactions == 0,
                    entity.State.Type + " publishes terrain destruction", Specials);
                Assert.That(_terrain.BlueCount, Is.LessThan(blue)); Assert.That(entity.BlockAttack.Added, Is.Zero);
                yield return Step(3, Specials); Assert.That(entity.BlockAttack.Added, Is.Zero, "Delayed special splashes never construct blocks");
                Assert.That(entity.BlockAttack.AwardedPoints, Is.Zero, "Special terrain damage cannot charge another special");
            }
            yield return Install(Platform(false)); var before = _terrain.ExportSnapshot();
            Place(_enemy, _floor + Vector3.right * 2, 2);
            var sonar = Special(3, _floor + Vector3.up * .4f, Vector3.down * 6);
            yield return Step(3, Specials);
            Assert.That(sonar.State.Pulse, Is.GreaterThan(0)); Assert.That(_enemy.Snapshot.Value.MarkedUntilPink, Is.GreaterThan(_now));
            Assert.That(_terrain.ExportSnapshot(), Is.EqualTo(before), "WaveBreaker marks/damages players without changing terrain");
            Assert.That(sonar.BlockAttack.Added + sonar.BlockAttack.RemovedEnemy, Is.Zero);
            yield return Install(Empty()); var rain = Special(4, _floor + Vector3.up * .4f, Vector3.down * 6);
            yield return Step(10, Specials);
            Assert.That(rain.Removed, Is.True, "Rain deployment expires");
            Assert.That(rain.BlockAttack.Added, Is.InRange(1, rain.BlockAttack.BuildLimit));
            Assert.That(rain.BlockAttack.AwardedPoints, Is.Zero); Assert.That(_host.Snapshot.Value.SpecialPoints, Is.Zero);
            int rainCount = _terrain.PinkCount; yield return Step(2, Specials);
            Assert.That(_terrain.PinkCount, Is.EqualTo(rainCount), "Expired rain cannot keep creating terrain");
            yield return Install(Platform()); int enemyBeforeRain = _terrain.BlueCount;
            rain = Special(4, _floor + Vector3.up * .4f, Vector3.down * 6);
            rain.State.Direction = Vector3.zero; yield return Step(4, Specials);
            Assert.That(rain.BlockAttack.RemovedEnemy, Is.GreaterThan(0), "Rain drops can directly remove enemy blocks");
            Assert.That(_terrain.BlueCount, Is.LessThan(enemyBeforeRain)); Assert.That(rain.BlockAttack.Added, Is.LessThanOrEqualTo(rain.BlockAttack.BuildLimit));
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator RescueExecutionAndExpiryNeverModifyBlocksOrGrantTerrainCredit()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity"); yield return new EnterPlayMode();
            yield return Run(RescueScenario);
            yield return new ExitPlayMode();
        }
        IEnumerator RescueScenario()
        {
            foreach (var outcome in new[] { BubbleOutcome.Rescued, BubbleOutcome.Executed, BubbleOutcome.Expired })
            {
                yield return Install(Platform()); var before = _terrain.ExportSnapshot(); uint paint = _match.PaintSequence;
                Assert.That(before, Is.Not.Empty); Assert.That(outcome, Is.Not.EqualTo(BubbleOutcome.None));
                Place(_enemy, _floor + Vector3.right * 3, 2); _enemy.ReceiveDamage(1, 1000, Vector3.forward, _host.PlayerId);
                Assert.That(_enemy.Snapshot.Value.IsBubble, Is.True);
                double at = outcome == BubbleOutcome.Expired ? _enemy.Snapshot.Value.BubbleUntil : _now;
                _enemy.EndBubble(outcome, at);
                Assert.That(_enemy.Snapshot.Value.BubbleResult, Is.EqualTo(outcome));
                yield return PumpUntil(() => _terrain.PendingTransactions == 0, "Rescue callback completion");
                Assert.That(_terrain.ExportSnapshot(), Is.EqualTo(before), outcome + " cannot mint or destroy blocks");
                Assert.That(_match.PaintSequence, Is.EqualTo(paint)); Assert.That(_host.Snapshot.Value.SpecialPoints, Is.Zero);
            }
        }
    }
}
#endif
