#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class RescueBubblePlayTests
    {
        const string Output = "Reports/RescueBubble";
        static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static IEnumerator Wait(Func<bool> condition, string label)
        {
            double end = Time.realtimeSinceStartupAsDouble + 50;
            while (!condition() && Time.realtimeSinceStartupAsDouble < end) yield return null;
            Assert.That(condition(), Is.True, label);
        }
        [UnityTest] public IEnumerator HostRescueExecutionTimeoutMovementAndEveryHeroEnvelope()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");
            yield return new EnterPlayMode(); yield return Scenario(); yield return new ExitPlayMode();
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            if (!Application.isPlaying) yield break;
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) yield return PrototypeApp.Current.Leave().ToCoroutine();
            yield return new ExitPlayMode();
        }
        static void Place(PrototypePlayer player, Vector3 feet, byte team, int hero = 1)
        {
            var s = player.Snapshot.Value; s.LifeState = PlayerLifeState.Alive; s.Health = GameplayConfig.GetHero(hero).MaxHealth;
            s.Ink = 12; s.HeroId = hero; s.Team = team; s.Position = feet; s.ProtectedUntil = s.RespawnsAt = s.DiedAt = s.BubbleUntil = 0;
            s.Swimming = s.CompactBody = false; s.Grounded = false; s.Movement = MovementMode.Air; s.PaperPose = PaperPose.None;
            s.Velocity = s.PlanarVelocity = Vector3.zero; s.VerticalSpeed = s.AirHumanOffset = 0;
            var cc = player.GetComponent<CharacterController>(); cc.enabled = false; player.transform.position = feet; cc.enabled = true;
            player.Snapshot.Value = s;
            typeof(PrototypePlayer).GetMethod("EnsureHeroPresentation", Private).Invoke(player, new object[] { hero });
            player.CharacterView.Present(s, .016f, player.NetworkManager.ServerTime.Time);
            PrototypeMatch.Current.CombatStats.BeginLife(player.PlayerId, team, s.Revision);
            player.SwimBody.ApplyCollision(s); Physics.SyncTransforms();
        }
        static void Hit(PrototypePlayer attacker, PrototypePlayer victim)
            => victim.ReceiveDamage(attacker.Snapshot.Value.Team, 10000, Vector3.forward, attacker.PlayerId);
        static PlayerInputFrame Request(PrototypePlayer actor, PrototypePlayer target, uint sequence)
            => new() { Revision = actor.Snapshot.Value.Revision, InteractSequence = sequence, InteractTarget = target.PlayerId, InteractTargetLife = target.Snapshot.Value.Revision };
        static void Near(PrototypePlayer actor, PrototypePlayer victim, byte team)
        {
            var s = victim.Snapshot.Value;
            Place(actor, s.BubbleCenter + Vector3.right * (s.BubbleRadius + .5f) - HeroBodyShape.For(actor.Snapshot.Value.HeroId).Center, team, actor.Snapshot.Value.HeroId);
        }
        static void Capture(PrototypePlayer player, string name)
        {
            typeof(PrototypePlayer).GetMethod("LateUpdate", Private).Invoke(player, null);
            var s = player.Snapshot.Value;
            var go = new GameObject("Bubble acceptance camera"); var camera = go.AddComponent<Camera>();
            camera.CopyFrom(Camera.main); camera.enabled = false; camera.fieldOfView = 45; camera.aspect = 16f / 9;
            camera.transform.position = s.BubbleCenter + new Vector3(1.5f, .6f, -3).normalized * s.BubbleRadius * 4;
            camera.transform.LookAt(s.BubbleCenter);
            var rt = new RenderTexture(1280, 720, 24); camera.targetTexture = rt; camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = rt;
            var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false); texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
            File.WriteAllBytes(Output + "/" + name + ".png", texture.EncodeToPNG());
            RenderTexture.active = previous; camera.targetTexture = null; Object.Destroy(texture); Object.Destroy(rt); Object.Destroy(go);
        }
        static IEnumerator Scenario()
        {
            Directory.CreateDirectory(Output);
            yield return Wait(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready, "Boot ready");
            var app = PrototypeApp.Current; yield return app.StartWeaponDebugRoom().ToCoroutine();
            yield return Wait(() => PrototypePlayer.Local != null && Camera.main != null, "Host ready");
            var host = PrototypePlayer.Local; var match = PrototypeMatch.Current;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Gameplay/Prototype/Prefabs/PrototypePlayer.prefab");
            var victim = match.AddTestBot(prefab); var friend = match.AddTestBot(prefab);
            yield return Wait(() => victim != null && friend != null && victim.CharacterView != null, "Bots ready");
            app.CaptureMouse(false); match.enabled = false; foreach (var p in match.Players) p.enabled = false;
            var phase = match.State.Value; phase.Phase = MatchPhase.Playing; phase.EndsAt = match.NetworkManager.ServerTime.Time + 1000; match.State.Value = phase;
            match.CombatStats.Reset(phase.Round);
            Place(host, new Vector3(20, 40, 20), 1); Place(friend, new Vector3(35, 40, 20), 2);
            var sizes = new List<string> { "hero,radius,height,clearance" }; uint seq = 0;
            foreach (var hero in LubanConfigService.Current.Tables.TbHero.DataList)
            {
                Place(victim, new Vector3(25, 40, 20), 2, hero.Id); Hit(host, victim);
                var bubble = victim.Snapshot.Value;
                Assert.That(bubble.IsBubble, Is.True); Assert.That(bubble.RespawnsAt, Is.Zero); Assert.That(bubble.DiedAt, Is.Zero);
                Assert.That(bubble.BubbleUntil - match.NetworkManager.ServerTime.Time, Is.EqualTo(10).Within(.1));
                var bounds = victim.CharacterView.MeasureBubblePose(victim.transform, bubble);
                Assert.That(bubble.BubbleRadius, Is.GreaterThanOrEqualTo(RescueBubbleRules.Radius(bounds, bubble.BubbleCenterHeight, .2f) - .001f));
                Assert.That(victim.CharacterView.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Locomotion"), Is.True);
                sizes.Add($"{hero.Id},{bubble.BubbleRadius},{bounds.size.y},0.2"); Capture(victim, "hero-" + hero.Id);
                victim.ReceiveDamage(1, 10000, default, host.PlayerId); Assert.That(victim.Snapshot.Value.BubbleUntil, Is.EqualTo(bubble.BubbleUntil));
                Near(friend, victim, 2); var request = Request(friend, victim, ++seq);
                friend.ResolveBubbleInteraction(request, match.NetworkManager.ServerTime.Time);
                var rescued = victim.Snapshot.Value;
                Assert.That(rescued.IsAlive, Is.True); Assert.That(rescued.Health, Is.EqualTo(hero.MaxHealth)); Assert.That(rescued.Ink, Is.EqualTo(hero.MaxInk));
                Assert.That(rescued.ProtectedUntil, Is.Zero); Assert.That(rescued.Position, Is.EqualTo(bubble.Position)); Assert.That(rescued.Revision, Is.EqualTo(bubble.Revision + 1));
                friend.ResolveBubbleInteraction(request, match.NetworkManager.ServerTime.Time); Assert.That(victim.Snapshot.Value.Revision, Is.EqualTo(rescued.Revision));
                Assert.That(match.CombatStats.Get(victim.PlayerId).Deaths, Is.Zero);
            }
            File.WriteAllLines(Output + "/hero-envelopes.csv", sizes);
            // Synthetic floor isolates the sphere motor from unrelated arena topology.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = "Bubble test floor"; floor.transform.position = new Vector3(25, 38, 20); floor.transform.localScale = new Vector3(30, 1, 30);
            Physics.SyncTransforms(); Place(victim, new Vector3(25, 42, 20), 2); Hit(host, victim);
            var move = victim.Snapshot.Value; Vector3 initial = move.Position; bool bounced = false;
            using (var motor = new RescueBubbleMotor())
            {
                for (int i = 0; i < 420; i++)
                {
                    float previous = move.VerticalSpeed;
                    motor.Step(ref move, new PlayerInputFrame { Move = Vector2.right }, 1f / 60);
                    bounced |= previous < 0 && move.VerticalSpeed > 0;
                    Assert.That(move.BubbleCenter.y - move.BubbleRadius, Is.GreaterThanOrEqualTo(38.49f));
                    Assert.That(move.VerticalSpeed, Is.GreaterThanOrEqualTo(-1.001f));
                }
            }
            Assert.That(bounced, Is.True); Assert.That(move.Position.x - initial.x, Is.EqualTo(4.2f).Within(.06f));
            victim.Snapshot.Value = move; victim.transform.position = move.Position; Physics.SyncTransforms(); Capture(victim, "floor-bounce");
            Near(friend, victim, 2); var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.position = (PrototypePlayer.InteractionPoint(friend.Snapshot.Value) + move.BubbleCenter) * .5f;
            wall.transform.localScale = new Vector3(.1f, 10, 10); Physics.SyncTransforms();
            friend.ResolveBubbleInteraction(Request(friend, victim, ++seq), match.NetworkManager.ServerTime.Time);
            Assert.That(victim.Snapshot.Value.IsBubble, Is.True, "No rescue through wall"); Object.Destroy(wall); yield return null;
            // Execution credits the downer, not a different enemy finisher.
            Near(friend, victim, 1); var paintBefore = match.PaintSequence;
            friend.ResolveBubbleInteraction(Request(friend, victim, ++seq), match.NetworkManager.ServerTime.Time);
            Assert.That(victim.Snapshot.Value.IsDead, Is.True); Assert.That(victim.Snapshot.Value.BubbleResult, Is.EqualTo(BubbleOutcome.Executed));
            Assert.That(match.CombatStats.Get(host.PlayerId).Kills, Is.EqualTo(1)); Assert.That(match.CombatStats.Get(friend.PlayerId).Kills, Is.Zero);
            Assert.That(victim.Snapshot.Value.RespawnsAt - victim.Snapshot.Value.DiedAt, Is.EqualTo(GameplayConfig.Mode.RespawnSeconds).Within(.001));
            // Timeout wins even when a friendly request arrives at the exact deadline.
            Place(victim, new Vector3(25, 42, 20), 2); Hit(host, victim); Near(friend, victim, 2);
            var timed = victim.Snapshot.Value;
            victim.EndBubble(BubbleOutcome.Rescued, timed.BubbleUntil);
            Assert.That(victim.Snapshot.Value.BubbleResult, Is.EqualTo(BubbleOutcome.Expired));
            victim.ExpireBubble(timed.BubbleUntil + 1); Assert.That(match.CombatStats.Get(host.PlayerId).Kills, Is.EqualTo(2));
            Object.Destroy(floor);
            // Real arena paint must be replicated and cannot damage any nearby player.
            Place(victim, PrototypeArena.Spawn(2, 0), 2); Hit(host, victim);
            var dying = victim.Snapshot.Value; Near(friend, victim, 1); float hp = friend.Snapshot.Value.Health;
            paintBefore = match.PaintSequence;
            victim.EndBubble(BubbleOutcome.Executed, match.NetworkManager.ServerTime.Time);
            Assert.That(friend.Snapshot.Value.Health, Is.EqualTo(hp), "Pure ink blast cannot damage players");
            Assert.That(match.PaintSequence, Is.GreaterThan(paintBefore), "Death explosion paints real arena surfaces");
            Assert.That(victim.Snapshot.Value.BubbleInkTeam, Is.EqualTo(host.Snapshot.Value.Team));
            victim.Simulate(1f / 60, victim.Snapshot.Value.RespawnsAt, MatchPhase.Playing);
            Assert.That(victim.Snapshot.Value.IsAlive, Is.True, "Original respawn flow resumes");
            // Exercise real FixedUpdate expiry (not a direct call), including the spawn platform geometry.
            Place(victim, PrototypeArena.Spawn(2, 0), 2); Hit(host, victim); match.enabled = true;
            var traces = new List<string>(); bool sawDeath = false, sawRespawn = false;
            double due = victim.Snapshot.Value.BubbleUntil, traceAt = 0, deadline = Time.realtimeSinceStartupAsDouble + 17;
            while (!sawRespawn && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var sample = victim.Snapshot.Value; double clock = match.NetworkManager.ServerTime.Time;
                sawDeath |= sample.IsDead;
                sawRespawn |= sawDeath && sample.IsAlive;
                if (clock >= traceAt) { traceAt = clock + .5; traces.Add($"{clock:R},{sample.LifeState},{sample.BubbleResult},{sample.Position},{sample.BubbleUntil:R},{sample.RespawnsAt:R}"); }
                yield return null;
            }
            File.WriteAllLines(Output + "/fixed-expiry.csv", traces);
            Assert.That(sawDeath, Is.True, "FixedUpdate expires bubble at its deadline " + due);
            Assert.That(sawRespawn, Is.True, "FixedUpdate returns to normal spawn");
            match.enabled = false;
            // Competing requests are resolved in receive order and never produce two outcomes.
            Place(host, new Vector3(20, 40, 20), 1); Place(victim, new Vector3(25, 40, 20), 2); Hit(host, victim);
            Near(friend, victim, 2); Near(host, victim, 1);
            uint eventBefore = victim.Snapshot.Value.BubbleEvent;
            match.QueueBubbleInteraction(friend, Request(friend, victim, ++seq));
            match.QueueBubbleInteraction(host, Request(host, victim, 1000));
            typeof(PrototypeMatch).GetMethod("ResolveBubbleInteractions", Private).Invoke(match, new object[] { match.NetworkManager.ServerTime.Time });
            Assert.That(victim.Snapshot.Value.IsAlive, Is.True); Assert.That(victim.Snapshot.Value.BubbleResult, Is.EqualTo(BubbleOutcome.Rescued));
            Assert.That(victim.Snapshot.Value.BubbleEvent, Is.EqualTo(eventBefore + 1));
            // Match finish freezes the bubble, and room reset clears it without another death blast.
            Hit(host, victim); var frozen = victim.Snapshot.Value;
            phase = match.State.Value; phase.Phase = MatchPhase.Finished; match.State.Value = phase;
            paintBefore = match.PaintSequence;
            victim.Simulate(.1f, frozen.BubbleUntil + 100, MatchPhase.Finished); victim.ExpireBubble(frozen.BubbleUntil + 100);
            Assert.That(victim.Snapshot.Value.IsBubble, Is.True); Assert.That(victim.Snapshot.Value.Position, Is.EqualTo(frozen.Position));
            Assert.That(match.PaintSequence, Is.EqualTo(paintBefore));
            match.ReturnToRoom(); Assert.That(victim.Snapshot.Value.IsAlive, Is.True); Assert.That(victim.Snapshot.Value.BubbleResult, Is.EqualTo(BubbleOutcome.None));
            File.WriteAllText(Output + "/host-acceptance.txt", "PASS: all heroes enclosed; rescue, execution, timeout, duplicate input, wall occlusion, slow movement, bounce, stats, paint-only blast and respawn.");
            yield return app.Leave().ToCoroutine();
        }
    }
}
#endif
