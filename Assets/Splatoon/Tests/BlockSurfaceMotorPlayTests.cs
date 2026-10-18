#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    /// <summary>Real map/controller/PhysX acceptance for the attached and loose block split.</summary>
    public sealed class BlockSurfaceMotorPlayTests
    {
        const float Dt = .02f;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        PrototypeMatch _match;
        PrototypePlayer _host;
        BlockDefaultContractFixture _fixture;
        RaycastHit _floor;

        static IEnumerator Wait(Func<bool> ready, string label)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 45;
            while (!ready() && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            Assert.That(ready(), Is.True, label);
        }

        [UnitySetUp] public IEnumerator Setup()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode();
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot config/assets");
            yield return PrototypeApp.Current.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && PrototypeMatch.Current?.Blocks?.Dynamic != null,
                "Authority surface/free block world");
            _match = PrototypeMatch.Current; _host = PrototypePlayer.Local;
            _match.enabled = false;
            foreach (var player in _match.Players) player.enabled = false;
            PrototypeApp.Current.CaptureMouse(false);
            _fixture = new BlockDefaultContractFixture(_match);
            _fixture.Tick();
            Assert.That(_match.Blocks.SurfaceOnly, Is.True);
            Assert.That(Physics.Raycast(new Vector3(0, 4, -20), Vector3.down, out _floor, 10,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True, "Actual authored map floor");
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            _fixture?.Dispose();
            yield return new ExitPlayMode();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        static PlayerSnapshot Standing(Vector3 feet) => new()
        {
            HeroId = 1, HeroRevision = 1, Team = 1, Health = 100, Ink = 50,
            LifeState = PlayerLifeState.Alive, Revision = 1,
            Position = feet, Grounded = true, Movement = MovementMode.Human,
            SubWeaponId = 1, SpecialWeaponId = 1, SpecialPhase = SpecialPhase.Charging,
            AttackNeedsRelease = true, SubNeedsRelease = true, SpecialNeedsRelease = true
        };

        static PlayerMotorSimulation Motor(Vector3 feet, out CharacterController controller)
        {
            var root = new GameObject("Surface motor acceptance") { layer = 8 };
            controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f; controller.center = Vector3.up * .9f;
            controller.radius = .35f; controller.skinWidth = .03f; controller.stepOffset = .3f;
            var motor = new PlayerMotorSimulation(controller);
            motor.Restore(Standing(feet), false);
            return motor;
        }

        [UnityTest, Timeout(600000)] public IEnumerator MapStepKeepsOrdinaryStepOffset()
        {
            Vector3 origin = _floor.point + Vector3.right * 4;
            var step = new GameObject("Authored 20 cm map step");
            step.transform.position = origin + new Vector3(0, .1f, 1.1f);
            var box = step.AddComponent<BoxCollider>(); box.size = new Vector3(1.5f, .2f, 1.4f);
            var motor = Motor(origin + Vector3.up * .035f, out var controller);
            var state = Standing(controller.transform.position);
            var input = new PlayerInputFrame { Move = Vector2.up, HeroRevision = 1 };
            Physics.SyncTransforms();
            float highest = state.Position.y;
            for (int i = 0; i < 50; i++)
            {
                motor.Step(ref state, input, Dt, _fixture.Now + i * Dt, false);
                highest = Mathf.Max(highest, state.Position.y);
                Assert.That(controller.stepOffset, Is.EqualTo(.3f).Within(.00001f), "Per-move step override must restore");
                yield return new WaitForFixedUpdate();
            }
            Assert.That(highest - origin.y, Is.GreaterThan(.15f), "Ordinary map steps still raise the capsule");
            Assert.That(state.Position.z, Is.GreaterThan(origin.z + .8f));
        }

        [UnityTest, Timeout(600000)] public IEnumerator AttachedLayerDoesNotRaiseCapsuleAndRetainsFriendlySurfaceOwnership()
        {
            var token = _fixture.PayMain(_host, 1);
            BlockAttackService.Build(token, _floor.point, _floor.normal, .22f, false, true,
                entityId: 9910001, eventOrdinal: 1);
            yield return _fixture.Publish(() => token.Added > 0);
            Assert.That(Physics.GetIgnoreLayerCollision(8, BlockTerrain.TerrainLayer), Is.True);
            Assert.That(Physics.Raycast(_floor.point + Vector3.up * 2, Vector3.down, out var physical, 3,
                PlayerMotorSimulation.WorldMask, QueryTriggerInteraction.Ignore), Is.True);
            Assert.That(physical.collider, Is.EqualTo(_floor.collider));
            Assert.That(physical.point.y, Is.EqualTo(_floor.point.y).Within(.002f));
            Assert.That(PlayerMotorSimulation.TryGetGround(_floor.point + Vector3.up * .035f, out var contact), Is.True);
            Assert.That(contact.Owner, Is.EqualTo(1));
            Assert.That(contact.Point.y, Is.EqualTo(_floor.point.y).Within(.002f), "Traversal uses the map support plane");
            var motor = Motor(_floor.point + Vector3.up * .035f, out var controller);
            var state = Standing(controller.transform.position);
            var input = new PlayerInputFrame { Swim = true, HeroRevision = 1 };
            for (int i = 0; i < 8; i++)
            {
                motor.Step(ref state, input, Dt, _fixture.Now + i * Dt, false);
                yield return new WaitForFixedUpdate();
            }
            Assert.That(state.Swimming, Is.True);
            Assert.That(state.SwimSource, Is.EqualTo(SwimSurface.Friendly));
            Assert.That(state.FriendlyInkContact, Is.True);
            Assert.That(state.Position.y - _floor.point.y, Is.LessThan(.1f), "The surface coating cannot act as a character stair");
            state.Team = 2; motor.Restore(state, false);
            motor.Step(ref state, input, Dt, _fixture.Now + 9 * Dt, false);
            Assert.That(state.Swimming, Is.False, "Enemy coating keeps its traversal restriction");
        }

        [UnityTest, Timeout(600000)] public IEnumerator AuthorityPushesLooseCubeWhileReplayCannotApplyForceOrAutoStep()
        {
            Vector3 spawn = _floor.point + new Vector3(0, BlockWorld.CellSize * .5f + .002f, .6f);
            Assert.That(_match.Blocks.Dynamic.TrySpawn(1, spawn, Quaternion.identity, out var loose), Is.True,
                "An actual authority cube in an empty map-floor position");
            var motor = (PlayerMotorSimulation)typeof(PrototypePlayer).GetField("_motor", Private).GetValue(_host);
            var state = _host.Snapshot.Value;
            var standing = Standing(_floor.point + new Vector3(0, .035f, .12f));
            standing.HeroRevision = state.HeroRevision; standing.Revision = state.Revision;
            var input = new PlayerInputFrame { Move = Vector2.up, HeroRevision = standing.HeroRevision,
                Revision = standing.Revision };
            motor.Restore(standing);
            // Calling the same motor outside Simulate is the owner/replay path.
            // IsServer alone must not authorize physics side effects on a host.
            var predicted = standing;
            for (int i = 0; i < 6; i++) motor.Step(ref predicted, input, Dt, _fixture.Now + i * Dt, false);
            yield return new WaitForFixedUpdate();
            Assert.That(Mathf.Abs(loose.Body.linearVelocity.z), Is.LessThan(.01f), "Replay never pushes authority bodies");
            Assert.That(loose.transform.position.z, Is.EqualTo(spawn.z).Within(.01f));
            loose.Body.linearVelocity = loose.Body.angularVelocity = Vector3.zero;
            motor.Restore(standing); _host.Snapshot.Value = standing;
            ((IDictionary<uint, PlayerInputFrame>)typeof(PrototypePlayer).GetField("_serverInputs", Private).GetValue(_host)).Clear();
            float start = loose.transform.position.z, highest = standing.Position.y;
            for (int i = 0; i < 55; i++)
            {
                typeof(PrototypePlayer).GetField("_lastInput", Private).SetValue(_host, input);
                typeof(PrototypePlayer).GetField("_lastReceivedAt", Private).SetValue(_host, _match.NetworkManager.ServerTime.Time);
                _fixture.Now += Dt;
                _host.Simulate(Dt, _fixture.Now, MatchPhase.Practice);
                highest = Mathf.Max(highest, _host.Snapshot.Value.Position.y);
                yield return new WaitForFixedUpdate();
            }
            Assert.That(loose.transform.position.z - start, Is.GreaterThan(.15f), "The player's real authority capsule pushes the mass");
            Assert.That(highest - standing.Position.y, Is.LessThan(.12f), "Walking into one loose cube cannot auto-step onto its top");
            Assert.That(_host.GetComponent<CharacterController>().stepOffset, Is.EqualTo(.3f).Within(.00001f));
            Assert.That(PlayerMotorSimulation.Query(loose.transform.position + Vector3.back,
                Vector3.forward, 2, out var freeContact) && PlayerMotorSimulation.CanClimb(freeContact, 1), Is.False,
                "A loose cube cannot become a neutral or friendly traversal wall");
        }
    }
}
#endif
