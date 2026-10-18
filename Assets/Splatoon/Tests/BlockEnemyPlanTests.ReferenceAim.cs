#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    // Proposed 53 tests: reuse the existing real Boot/host fixture, but explicitly select hero 1.
    // Fixture poses/target spawn are geometry contracts, not paid/network enemy-removal acceptance.
    public sealed partial class BlockEnemyPlanTests
    {
        static readonly Vector3 RegressionFloor = new(-6.5440898f, 0, 21.25318f);
        static readonly Vector3 RegressionFeet = new(-6.293436f, .02f, 18.765778f);
        IEnumerator RealRifleGirl()
        {
            var room = _match.State.Value; room.Phase = MatchPhase.Practice; _match.State.Value = room;
            if (_host.Snapshot.Value.HeroId != 1) _host.RequestHeroChange(1, HeroSelectionOrigin.Warmup);
            for (int frame = 0; frame < 300 && (_host.Snapshot.Value.HeroId != 1 || _host.HeroChangePending ||
                _host.Presentation == null || _host.Presentation.name != "RifleGirlPresentation"); frame++)
            {
                _host.Simulate(BlockDefaultContractFixture.Dt, _match.NetworkManager.ServerTime.Time, MatchPhase.Practice);
                yield return null;
            }
            Assert.That(_host.Snapshot.Value.HeroId, Is.EqualTo(1), _host.HeroChangeMessage);
            var profile = AssetDatabase.LoadAssetAtPath<CharacterPresentationProfile>(
                "Assets/GameResource/Characters/RifleGirl/RifleGirlPresentation.asset");
            Assert.That(profile, Is.Not.Null);
            Assert.That(_host.Presentation, Is.SameAs(profile), "Use the real bound RifleGirl camera and muzzle; never substitute the default profile");
            Assert.That(GameplayConfig.GetHero(1).WeaponConfigPath,
                Is.EqualTo("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset"));
            var asset = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(1).WeaponConfigPath);
            var weapon = GameplayConfig.GetWeapon(1);
            Assert.That(weapon.AimMode, Is.EqualTo(WeaponAimMode.WeaponReference));
            Assert.That(weapon.SpeedMin, Is.EqualTo(asset.speedMin)); Assert.That(weapon.SpeedMax, Is.EqualTo(asset.speedMax));
            Assert.That(weapon.CollisionRadius, Is.EqualTo(asset.collisionRadius)); Assert.That(weapon.ProjectileGravity, Is.EqualTo(asset.projectileGravity));
            Assert.That(weapon.StraightSeconds, Is.EqualTo(asset.straightSeconds));
            room.Phase = MatchPhase.Playing; _match.State.Value = room;
        }
        PlayerSnapshot RegressionState()
        {
            var state = _host.Snapshot.Value;
            state.Position = RegressionFeet; state.Yaw = state.Pitch = 0;
            state.AirHumanOffset = state.CameraRebaseOffset = 0;
            state.Swimming = state.CompactBody = false; state.Grounded = true;
            state.PlanarVelocity = state.Velocity = Vector3.zero; state.NextMuzzle = 0;
            state.WeaponPhase = WeaponPhase.Idle; state.SplatlingRemaining = state.BurstRemaining = 0;
            return state;
        }
        BlockDynamicBody RegressionTarget()
        {
            int mask = PlayerMotorSimulation.WorldMask & ~(1 << BlockTerrain.TerrainLayer) & ~(1 << BlockDynamicWorld.Layer);
            Assert.That(Physics.Raycast(RegressionFloor + Vector3.up * 3, Vector3.down, out var floor, 10,
                mask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(Terrain.IsAuthoredMapCollider(floor.collider), Is.True);
            Assert.That(floor.point.y, Is.EqualTo(0).Within(.001f), "Recorded Network52Debug01 flat-floor regression");
            Assert.That(HeroBodyShape.For(1).Fits(RegressionFeet, new Collider[32], PlayerMotorSimulation.WorldMask), Is.True);
            return Target(floor.point);
        }
        string ReferenceForecast(PlayerSnapshot state, BlockDynamicBody target, out Vector2 look,
            out WeaponImpactForecast forecast, out BlockContact contact)
        {
            object[] arguments = { state, target, default(Vector2), default(WeaponImpactForecast), default(BlockContact), "" };
            string result = Invoke("EnemyForecast", arguments).ToString();
            look = (Vector2)arguments[2]; forecast = (WeaponImpactForecast)arguments[3]; contact = (BlockContact)arguments[4];
            return result;
        }
        void AssertAimBounds()
        {
            Assert.That(Plan.aimMaximumResolvesPerSolve, Is.LessThanOrEqualTo(13));
            Assert.That(Plan.aimMaximumResolvesInFrame, Is.LessThanOrEqualTo(26));
            Assert.That(Plan.aimResolveBudgetPerFrame, Is.EqualTo(26));
        }
        void ReleaseEnemyAuthority()
        {
            // The existing fixture disables FixedUpdate. Boot, hero loading and planning may
            // therefore timeout normally. Clear its release latch through real accepted input,
            // never by assigning AttackNeedsRelease, lastReceivedAt or any shooting result.
            var before = _host.Snapshot.Value;
            var input = new PlayerInputFrame { Sequence = before.AcknowledgedInput + 1,
                Tick = (uint)Math.Max(0, _match.NetworkManager.ServerTime.Time * GameplayConfig.Global.SimulationRate),
                Revision = before.Revision, HeroRevision = before.HeroRevision,
                Look = new Vector2(before.Yaw, before.Pitch), JumpSequence = before.ConsumedJump,
                FireSequence = before.ConsumedFire, ReleaseSequence = before.ConsumedRelease,
                SpecialSequence = before.SpecialConsumed, SubPressSequence = before.SubConsumedPress,
                SubReleaseSequence = before.SubConsumedRelease };
            typeof(PrototypePlayer).GetMethod("AcceptInput", Methods).Invoke(_host, new object[] { input });
            _host.Simulate(BlockDefaultContractFixture.Dt,
                Math.Max(before.SimulatedAt, _match.NetworkManager.ServerTime.Time) + BlockDefaultContractFixture.Dt, MatchPhase.Playing);
            var after = _host.Snapshot.Value;
            Assert.That(after.AcknowledgedInput, Is.EqualTo(input.Sequence)); Assert.That(after.InputTimedOut, Is.False);
            Assert.That(after.AttackNeedsRelease, Is.False); Assert.That(after.WeaponPhase, Is.EqualTo(WeaponPhase.Idle));
            Assert.That(after.ShotSequence, Is.EqualTo(before.ShotSequence)); Assert.That(after.Ink, Is.EqualTo(before.Ink).Within(.001f));
        }

        [UnityTest, Timeout(600000)] public IEnumerator RealRifleNearGroundOldCameraAimHitsGroundAndSharedInverseHitsCurrentEnemy()
        {
            yield return RealRifleGirl();
            var target = RegressionTarget(); Command(1, "enemy");
            var state = RegressionState();
            var oldLook = (Vector2)ProbeType.GetMethod("AimAt", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { _host, state, target.Body.position });
            var oldState = state; oldState.Yaw = oldLook.x; oldState.Pitch = oldLook.y;
            var solver = new TpsAimSolver(); var weapon = GameplayConfig.GetWeapon(1);
            var oldAim = solver.Resolve(_host, oldState, oldState.NextMuzzle);
            var oldForecast = WeaponImpactPrediction.Guide(solver, oldAim, weapon, oldState, _host.PlayerId);
            Assert.That(oldForecast.Contact.Collider, Is.Not.Null);
            Assert.That(oldForecast.Contact.Collider.name, Does.StartWith("Ground"), "Keep a fixed old-behaviour counterexample, using the real weapon/profile");
            Assert.That(oldForecast.Age, Is.LessThan(weapon.StraightSeconds), "This recorded near failure is before gravity begins");
            Assert.That(oldForecast.Contact.Collider, Is.Not.SameAs(target.Collider));

            float ink = _host.Snapshot.Value.Ink; uint shots = _host.Snapshot.Value.ShotSequence;
            uint revision = Terrain.Dynamic.LifecycleRevision; long removed = Terrain.Dynamic.RemovedCount;
            Assert.That(ReferenceForecast(state, target, out var look, out var forecast, out var contact), Is.EqualTo("Ready"));
            Assert.That(float.IsFinite(look.x) && float.IsFinite(look.y), Is.True); Assert.That(look.y, Is.InRange(-65, 75));
            Assert.That(forecast.Contact.Collider, Is.SameAs(target.Collider));
            Assert.That(contact.Kind, Is.EqualTo(BlockContactKind.Dynamic)); Assert.That(contact.DynamicId, Is.EqualTo(target.Id));
            Assert.That(contact.Owner, Is.EqualTo(target.Owner)); Assert.That(target.World.Round, Is.EqualTo(Plan.identity.round));
            Assert.That(Plan.aimInverseCalls, Is.GreaterThan(0)); AssertAimBounds();
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(ink)); Assert.That(_host.Snapshot.Value.ShotSequence, Is.EqualTo(shots));
            Assert.That(Terrain.Dynamic.LifecycleRevision, Is.EqualTo(revision)); Assert.That(Terrain.Dynamic.RemovedCount, Is.EqualTo(removed));
            Assert.That(Plan.placements, Is.Zero, "A mathematical preflight cannot Respawn or place the player");
        }

        [UnityTest, Timeout(600000)] public IEnumerator SharedFrameQuotaDefersBothCandidateAndInputWithoutRejectingOrReplacingThem()
        {
            yield return RealRifleGirl();
            var target = RegressionTarget(); Command(1, "enemy");
            // Explicit fixture actual pose lets candidate work use the recorded clear stand without a diagnostic Respawn.
            // No CanStand/fire/payment result is assigned or accepted here.
            ReleaseEnemyAuthority(); // Run the real motor before installing the read-only regression snapshot.
            _host.Snapshot.Value = RegressionState(); Set(_probe, "_enemyPlaced", true);
            var feet = PlayerMotorSimulation.HumanPosition(_host.Snapshot.Value);
            Assert.That(Vector3.Distance(feet, RegressionFeet), Is.LessThan(.00001f));
            Assert.That(HeroBodyShape.For(1).Fits(feet, new Collider[32], PlayerMotorSimulation.WorldMask), Is.True);
            Assert.That(Vector3.Distance(feet, target.Body.position), Is.LessThanOrEqualTo(GameplayConfig.GetWeapon(1).EffectiveRange));
            Invoke("EnemyCandidates");
            Set(_probe, "_enemyAimBudgetFrame", Time.frameCount); Set(_probe, "_enemyAimFrameResolves", 26);
            int candidate = (int)Get(_probe, "_enemyCandidateAt"), stand = (int)Get(_probe, "_enemyStandAt");
            long calls = Plan.aimSolveCalls;
            ObserveNow();
            Assert.That((int)Get(_probe, "_enemyCandidateAt"), Is.EqualTo(candidate));
            Assert.That((int)Get(_probe, "_enemyStandAt"), Is.EqualTo(stand));
            Assert.That(Plan.aimSolveCalls, Is.EqualTo(calls)); Assert.That(Plan.aimBudgetDeferrals, Is.GreaterThan(0));
            Assert.That(Plan.replans, Is.Zero); Assert.That(Plan.placements, Is.Zero);
            Set(_probe, "_enemyAimFrameResolves", 25);
            long rejected = Plan.aimRejected, ready = Plan.aimReady;
            ObserveNow();
            Assert.That((int)Get(_probe, "_enemyAimFrameResolves"), Is.EqualTo(26), "The seed consumes the final actual Resolve; no partial iteration can begin");
            Assert.That(Plan.aimSolveCalls, Is.EqualTo(calls + 1)); Assert.That(Plan.aimReady, Is.EqualTo(ready));
            Assert.That(Plan.aimRejected, Is.EqualTo(rejected), "Partial-budget Deferred is not a geometry rejection");
            Assert.That((int)Get(_probe, "_enemyCandidateAt"), Is.EqualTo(candidate));
            Assert.That((int)Get(_probe, "_enemyStandAt"), Is.EqualTo(stand));
            Assert.That(Get(_probe, "_enemyAimHintTarget"), Is.Null, "A partial inverse cannot become a successful seed hint");
            yield return null; ObserveNow();
            Assert.That(Plan.status, Is.EqualTo("ready"), Plan.lastReason);
            Assert.That(Plan.identity.targetId, Is.EqualTo(target.Id));

            while ((int)Get(_probe, "_enemyAimFrameResolves") < 26)
                Assert.That(ReferenceForecast(_host.Snapshot.Value, target, out _, out _, out _), Is.EqualTo("Ready"));
            ulong locked = Plan.identity.targetId; int replans = Plan.replans; long before = Plan.aimBudgetDeferrals;
            ObserveNow(); Assert.That(PlanInput(_host.Snapshot.Value).Fire, Is.False);
            Assert.That(Plan.aimBudgetDeferrals, Is.GreaterThanOrEqualTo(before + 2));
            Assert.That(Plan.identity.targetId, Is.EqualTo(locked)); Assert.That(Plan.replans, Is.EqualTo(replans));
            yield return null;
            Assert.That(PlanInput(_host.Snapshot.Value).Fire, Is.True, "A new frame retries the shared solve for the same target");
            Assert.That(Plan.placements, Is.Zero); AssertAimBounds();
        }

        [UnityTest, Timeout(600000)] public IEnumerator PostPlacementAndMovingTargetInputReuseSharedFormalInverseWithOnePlacement()
        {
            yield return RealRifleGirl();
            var target = RegressionTarget(); Command(1, "enemy"); yield return Ready(); ReleaseEnemyAuthority();
            Assert.That(Plan.placements, Is.EqualTo(1));
            uint life = _host.Snapshot.Value.Revision; float ink = _host.Snapshot.Value.Ink; uint shots = _host.Snapshot.Value.ShotSequence;
            yield return null;
            var input = PlanInput(_host.Snapshot.Value);
            Assert.That(input.Fire, Is.True);
            Assert.That(ReferenceForecast(_host.Snapshot.Value, target, out var look, out _, out _), Is.EqualTo("Ready"));
            Assert.That(Vector2.Distance(input.Look, look), Is.LessThan(.0001f), "Input consumes the same shared solution, not the old camera-centre AimAt");
            target.Body.position += Vector3.right * .06f; target.Body.Sleep(); Physics.SyncTransforms();
            yield return null;
            Assert.That(PlanInput(_host.Snapshot.Value).Fire, Is.True, "A moved target is revalidated against its current collider");
            Assert.That(Plan.placements, Is.EqualTo(1)); Assert.That(_host.Snapshot.Value.Revision, Is.EqualTo(life));
            Assert.That(_host.Snapshot.Value.Ink, Is.EqualTo(ink)); Assert.That(_host.Snapshot.Value.ShotSequence, Is.EqualTo(shots));
            Assert.That(Plan.aimHintSeeds, Is.GreaterThan(0)); AssertAimBounds();
        }

        [UnityTest, Timeout(600000)] public IEnumerator SmallMathematicalResidualCannotAcceptAnOccludingWall()
        {
            yield return RealRifleGirl();
            var target = RegressionTarget(); Command(1, "enemy"); var state = RegressionState();
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.name = "Reference inverse formal occluder";
                wall.transform.position = (RegressionFeet + target.Body.position) * .5f + Vector3.up;
                wall.transform.localScale = new Vector3(8, 4, .15f); Physics.SyncTransforms();
                Assert.That(ReferenceForecast(state, target, out _, out var forecast, out _), Is.EqualTo("Rejected"));
                Assert.That(forecast.Contact.Collider, Is.Not.SameAs(target.Collider));
                Assert.That(Plan.aimReady, Is.Zero); Assert.That(Plan.placements, Is.Zero); AssertAimBounds();
            }
            finally { UnityEngine.Object.DestroyImmediate(wall); Physics.SyncTransforms(); }
        }

        PlayerSnapshot AuthorityEnemyStep(double now, ref uint fireSequence, ref uint releaseSequence, ref bool held,
            out PlayerInputFrame input)
        {
            var before = _host.Snapshot.Value;
            input = new PlayerInputFrame { Sequence = before.AcknowledgedInput + 1,
                Tick = (uint)Math.Max(0, _match.NetworkManager.ServerTime.Time * GameplayConfig.Global.SimulationRate),
                Revision = before.Revision, HeroRevision = before.HeroRevision, JumpSequence = before.ConsumedJump,
                FireSequence = fireSequence, ReleaseSequence = releaseSequence, SpecialSequence = before.SpecialConsumed,
                SubPressSequence = before.SubConsumedPress, SubReleaseSequence = before.SubConsumedRelease };
            // Real Input includes the generic downward default. Only the CLI Requested selector is bypassed.
            // Queue selection, timeout, movement, CanStand, weapon payment and production Spawn run unchanged.
            object[] arguments = { _host, input }; Invoke("Input", arguments); input = (PlayerInputFrame)arguments[1];
            if (input.Fire && !held && fireSequence == before.ConsumedFire) input.FireSequence = ++fireSequence;
            if (!input.Fire && held && releaseSequence == before.ConsumedRelease) input.ReleaseSequence = ++releaseSequence;
            held = input.Fire;
            BlockDynamicAcceptanceProbe.RecordEnemyInput(_host, input);
            typeof(PrototypePlayer).GetMethod("AcceptInput", Methods).Invoke(_host, new object[] { input });
            _host.Simulate(BlockDefaultContractFixture.Dt, now, MatchPhase.Playing);
            var accepted = (PlayerInputFrame)Get(_host, "_lastInput"); var after = _host.Snapshot.Value;
            Assert.That(after.AcknowledgedInput, Is.EqualTo(input.Sequence), "Real oldest queued input was consumed");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(accepted.Look.x, input.Look.x)), Is.LessThan(.0001f));
            Assert.That(accepted.Look.y, Is.EqualTo(input.Look.y).Within(.0001f));
            Assert.That(after.InputTimedOut, Is.False);
            return after;
        }
        void AssertActualEnemyEmission(PlayerSnapshot state, BlockDynamicBody target)
        {
            var weapon = GameplayConfig.GetWeapon(state.HeroId);
            var solver = new TpsAimSolver(); var aim = solver.Resolve(_host, state, state.LastShotMuzzle);
            for (int pellet = 0; pellet < weapon.PelletCount; pellet++)
            {
                var shot = _match.Projectiles.Spawned[_match.Projectiles.Spawned.Count - weapon.PelletCount + pellet];
                var expected = WeaponLaunch.Velocity(aim, weapon, state.LastShotCharge, state.PlanarVelocity, state.Yaw,
                    state.LastShotSpread, state.LastShotVerticalSpread, state.LastShotSpreadBias, shot.Seed, true);
                Assert.That(Vector3.Distance(shot.Origin, aim.Muzzle), Is.LessThan(.001f));
                Assert.That(Vector3.Distance(shot.Velocity, expected), Is.LessThan(.001f), "Production sampled spread uses the accepted view without tuning");
                Assert.That(shot.ShotSequence, Is.EqualTo(state.ShotSequence));
                Assert.That(shot.BlockAttack, Is.Not.Null); Assert.That(shot.BlockAttack.Credit.Owner, Is.EqualTo(_host.PlayerId));
                Assert.That(shot.BlockAttack.Credit.Round, Is.EqualTo(Terrain.Round));
            }
            var formal = WeaponImpactPrediction.Guide(solver, aim, weapon, state, _host.PlayerId);
            Assert.That(formal.Contact.Collider, Is.SameAs(target.Collider), "The accepted angle retains the formal same-target centre contact");
        }

        [UnityTest, Timeout(600000)] public IEnumerator ThreeSameRenderStepsAcrossThreePaidEmissionsKeepStrictFireWitnesses()
        {
            yield return RealRifleGirl();
            var target = RegressionTarget(); Command(1, "enemy"); yield return Ready(); ReleaseEnemyAuthority();
            var initial = _host.Snapshot.Value; var weapon = GameplayConfig.GetWeapon(1);
            Assert.That(initial.WeaponPhase, Is.EqualTo(WeaponPhase.Idle)); Assert.That(initial.AttackNeedsRelease, Is.False);
            Assert.That(weapon.StartSeconds, Is.EqualTo(2 * (double)BlockDefaultContractFixture.Dt).Within(1e-8));
            Assert.That(WeaponSimulation.FireInterval(weapon), Is.EqualTo(6 * (double)BlockDefaultContractFixture.Dt).Within(1e-8));
            var queued = Get(_host, "_serverInputs");
            Assert.That((int)queued.GetType().GetProperty("Count").GetValue(queued), Is.Zero);
            _match.Projectiles.Clear();
            var execution = (BlockDynamicAcceptanceProbe.EnemyExecutionReport)Get(_report, "enemyExecution");
            int samplesBefore = execution.samples.Count;
            long deferredBefore = Plan.aimBudgetDeferrals;
            double now = Math.Max(initial.SimulatedAt, _match.NetworkManager.ServerTime.Time) + BlockDefaultContractFixture.Dt;
            uint fireSequence = initial.ConsumedFire, releaseSequence = initial.ConsumedRelease;
            bool held = false;
            for (int cycle = 0; cycle < 6; cycle++)
            {
                yield return null; // A real new render frame, followed by exactly three catch-up authority steps.
                int renderFrame = Time.frameCount; long resolvesBefore = Plan.aimResolves;
                for (int fixedStep = 0; fixedStep < 3; fixedStep++)
                {
                    int step = cycle * 3 + fixedStep;
                    Assert.That(Time.frameCount, Is.EqualTo(renderFrame));
                    var before = _host.Snapshot.Value;
                    var after = AuthorityEnemyStep(now + step * (double)BlockDefaultContractFixture.Dt,
                        ref fireSequence, ref releaseSequence, ref held, out var input);
                    Assert.That(input.Fire, Is.True, "Every locked input, including fixed step three, retains a fresh formal check");
                    Assert.That(input.CancelFire, Is.False);
                    Assert.That(after.WeaponPhase, Is.EqualTo(step < 2 ? WeaponPhase.Starting : WeaponPhase.Firing));
                    uint paid = step < 2 ? 0u : (uint)(1 + (step - 2) / 6);
                    bool emitted = step is 2 or 8 or 14;
                    Assert.That(after.ShotSequence, Is.EqualTo(initial.ShotSequence + paid));
                    Assert.That(before.Ink - after.Ink,
                        Is.EqualTo(emitted ? WeaponSimulation.InkCost(weapon, after.LastShotCharge) : 0).Within(.001f));
                    Assert.That(_match.Projectiles.Spawned.Count, Is.EqualTo((int)paid * weapon.PelletCount));
                    if (emitted) AssertActualEnemyEmission(after, target);
                }
                Assert.That(Plan.aimResolves - resolvesBefore, Is.EqualTo(3), "The same-target hints cost actual one-Resolve checks, not three whole solves");
            }
            Assert.That(Plan.aimBudgetDeferrals, Is.EqualTo(deferredBefore));
            var observed = execution.samples.GetRange(samplesBefore, execution.samples.Count - samplesBefore);
            var emissions = observed.FindAll(item => item.kind == "weapon" && item.emitted);
            Assert.That(emissions.Count, Is.EqualTo(3), "Cover startup and two real six-tick cooldown cycles, at steps 3, 9 and 15");
            foreach (var emission in emissions)
            {
                var input = observed.Find(item => item.kind == "input" && item.sequence == emission.sequence);
                var authority = observed.Find(item => item.kind == "authority-input" && item.sequence == emission.sequence);
                Assert.That(input, Is.Not.Null); Assert.That(authority, Is.Not.Null);
                Assert.That(input.fire && authority.fire && emission.fire, Is.True, "Keep the unchanged strict acceptance witness");
                Assert.That(input.cancelFire || authority.cancelFire || emission.cancelFire || authority.timedOut, Is.False);
                Assert.That(authority.queuedBefore, Is.EqualTo(1)); Assert.That(authority.discardedInputs, Is.Zero);
                Assert.That(emission.canShoot && emission.standQueried && emission.alive && !emission.needsRelease, Is.True);
                Assert.That(emission.materialBefore - emission.materialAfter,
                    Is.EqualTo(WeaponSimulation.InkCost(weapon)).Within(.001f));
                Assert.That(input.plan.targetId, Is.EqualTo(target.Id)); Assert.That(authority.plan.targetId, Is.EqualTo(target.Id));
                Assert.That(emission.plan.targetId, Is.EqualTo(target.Id));
            }
            Assert.That(Plan.placements, Is.EqualTo(1)); Assert.That(Plan.replans, Is.Zero); AssertAimBounds();
        }

        [UnityTest, Timeout(600000)] public IEnumerator ExhaustedActualBudgetPreservesCommittedLookWithoutClaimingStrictFireWitness()
        {
            yield return RealRifleGirl();
            var target = RegressionTarget(); Command(1, "enemy"); yield return Ready(); ReleaseEnemyAuthority(); yield return null;
            var initial = _host.Snapshot.Value; var weapon = GameplayConfig.GetWeapon(1);
            _match.Projectiles.Clear();
            uint fireSequence = initial.ConsumedFire, releaseSequence = initial.ConsumedRelease;
            bool held = false; int renderFrame = Time.frameCount;
            double now = Math.Max(initial.SimulatedAt, _match.NetworkManager.ServerTime.Time) + BlockDefaultContractFixture.Dt;
            for (int step = 0; step < 2; step++)
            {
                var started = AuthorityEnemyStep(now + step * (double)BlockDefaultContractFixture.Dt,
                    ref fireSequence, ref releaseSequence, ref held, out var accepted);
                Assert.That(accepted.Fire, Is.True); Assert.That(started.WeaponPhase, Is.EqualTo(WeaponPhase.Starting));
                Assert.That(started.ShotSequence, Is.EqualTo(initial.ShotSequence));
            }
            // Deliberate negative fixture: arbitrary work can exhaust the hard budget. Fire=false
            // still permits an already committed Starting shot, so it must never restore default Look.
            Set(_probe, "_enemyAimFrameResolves", 26);
            long deferred = Plan.aimBudgetDeferrals, rejected = Plan.aimRejected;
            var before = _host.Snapshot.Value;
            var emitted = AuthorityEnemyStep(now + 2 * (double)BlockDefaultContractFixture.Dt,
                ref fireSequence, ref releaseSequence, ref held, out var input);
            Assert.That(Time.frameCount, Is.EqualTo(renderFrame));
            Assert.That(input.Fire || input.CancelFire, Is.False);
            Assert.That(Vector2.Distance(input.Look, new Vector2(before.Yaw, before.Pitch)), Is.LessThan(.0001f));
            Assert.That(Plan.aimBudgetDeferrals, Is.EqualTo(deferred + 1)); Assert.That(Plan.aimRejected, Is.EqualTo(rejected));
            Assert.That(emitted.WeaponPhase, Is.EqualTo(WeaponPhase.Firing));
            Assert.That(emitted.ShotSequence, Is.EqualTo(initial.ShotSequence + 1));
            Assert.That(before.Ink - emitted.Ink, Is.EqualTo(WeaponSimulation.InkCost(weapon, emitted.LastShotCharge)).Within(.001f));
            AssertActualEnemyEmission(emitted, target);
            Assert.That(Plan.placements, Is.EqualTo(1)); Assert.That(Plan.replans, Is.Zero); AssertAimBounds();
        }
    }
}
#endif
