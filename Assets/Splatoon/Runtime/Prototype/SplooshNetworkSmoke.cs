#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in acceptance in two independent Windows players; no ordinary-room behavior.</summary>
    public sealed class SplooshNetworkSmoke : MonoBehaviour
    {
        static SplooshNetworkSmoke instance;
        public static bool Active => instance != null;
        bool host, ready, ending, resync, reset, killed, died, respawned, captured, tall, smallAgain, paper, remoteSmall, combatPlaced, secondDuel, receivedShot, confirmedEnemyHit;
        float started; double nextSwitch; int players, snapshots, emitted; uint sequence, life, hash, paintSequence;
        readonly HashSet<string> captures = new(); readonly List<string> errors = new();
        static string Arg(string key, string fallback) => HeroSelectionSmoke.Arg(key, fallback);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if (instance != null || !Environment.GetCommandLineArgs().Contains("-splooshRole")) return;
            var go = new GameObject("Sploosh network acceptance"); DontDestroyOnLoad(go); instance = go.AddComponent<SplooshNetworkSmoke>();
        }
        async UniTaskVoid Start()
        {
            host = Arg("-splooshRole", "host") == "host"; started = Time.realtimeSinceStartup; Application.logMessageReceived += OnLog;
            try
            {
                await UniTask.WaitUntil(() => PrototypeApp.Current != null && PrototypeApp.Current.Ready);
                if (!host) await UniTask.Delay(TimeSpan.FromSeconds(8));
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    await PrototypeApp.Current.Connect(host, "127.0.0.1", ushort.Parse(Arg("-splooshPort", "18518")));
                    if (PrototypeApp.Current.InRoom) break;
                    await UniTask.Delay(TimeSpan.FromSeconds(1));
                }
                if (!PrototypeApp.Current.InRoom) throw new Exception(PrototypeApp.Current.Error);
                ready = true;
            }
            catch (Exception e) { Finish(e.ToString()); }
        }
        void Update()
        {
            if (ending) return;
            if (Time.realtimeSinceStartup - started > 180) { Finish("Timed out"); return; }
            var match = PrototypeMatch.Current; var p = PrototypePlayer.Local;
            if (!ready || match == null || p == null) return;
            double age = p.NetworkManager.ServerTime.Time; var s = p.Snapshot.Value;
            players = Math.Max(players, PrototypePlayer.ByOwner.Count);
            if (s.Revision == life && s.HeroId == 8 && s.ShotSequence > sequence) emitted += (int)(s.ShotSequence - sequence);
            sequence = s.ShotSequence; life = s.Revision;
            int desired = age >= 20 && age < 26 ? 1 : 8;
            if (age < 75 && s.Health > 0 && !p.HeroChangePending && s.HeroId != desired && age >= nextSwitch)
            {
                Debug.Log($"[SPLOOSH-SWITCH] {s.HeroId}->{desired} at={age:F2} feet={PlayerMotorSimulation.HumanPosition(s):F4} previousReply={p.HeroChangeMessage}");
                p.RequestHeroChange(desired, HeroSelectionOrigin.Warmup); nextSwitch = age + 1;
            }
            tall |= age >= 20 && s.HeroId == 1; smallAgain |= tall && s.HeroId == 8;
            paper |= s.HeroId == 8 && s.ShowsSwimBody && p.SwimBody.FlatHitActive;
            remoteSmall |= PrototypePlayer.ByOwner.Values.Any(other => other != p && other.Snapshot.Value.HeroId == 8 &&
                Mathf.Abs(other.SwimBody.CapsuleHitVolume.height - 1.2f) < .001f);
            if (!host && age > 50 && !resync)
            {
                typeof(PrototypeMatch).GetMethod("RequestSnapshotRpc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(match, new object[] { default(Unity.Netcode.RpcParams) }); resync = true;
            }
            if (host && (age >= 52 && !combatPlaced || age >= 53.8 && !secondDuel))
            {
                // Put both players in a known open lane. The subsequent shots still come from
                // each process's normal input, prediction, network command and authority paths.
                var anchor = PrototypeArena.Spawn(1, 0);
                foreach (var other in match.Players)
                {
                    var fight = other.Snapshot.Value; fight.Revision++; fight.Position = anchor + Vector3.forward * (fight.Team == 1 ? 0 : 2);
                    fight.Health = fight.Ink = 100; fight.ProtectedUntil = 0; fight.RespawnsAt = fight.DiedAt = 0; fight.Swimming = fight.CompactBody = false;
                    fight.PaperPose = PaperPose.None; fight.AirHumanOffset = 0; fight.Movement = MovementMode.Human; fight.Grounded = true;
                    fight.Yaw = fight.BodyYaw = fight.Team == 1 ? 0 : 180; fight.Pitch = FightPitch(other); fight.Velocity = fight.PlanarVelocity = Vector3.zero; fight.VerticalSpeed = 0;
                    fight.AttackNeedsRelease = false; fight.AttackRecoveryUntil = fight.AttackMoveUntil = 0;
                    other.Snapshot.Value = fight; new PlayerMotorSimulation(other.GetComponent<CharacterController>()).Restore(fight);
                }
                if (combatPlaced) secondDuel = true;
                combatPlaced = true;
            }
            receivedShot |= age >= 52 && age < 56 && s.Health < 100 && s.LastDamageAt >= 52;
            confirmedEnemyHit |= age >= 52 && age < 56 && p.HitConfirmedUntil > Time.unscaledTimeAsDouble;
            if (host && age > 56 && !killed)
            { foreach (var other in match.Players) other.ReceiveDamage((byte)(other.Snapshot.Value.Team == 1 ? 2 : 1), 200, Vector3.forward); killed = true; }
            died |= s.Health <= 0; respawned |= died && s.Health > 0 && s.HeroId == 8;
            if (age > 72 && !captured)
            { hash = match.Arena.OwnershipHash(); paintSequence = match.AppliedPaintSequence; captured = match.InitialSyncComplete; }
            if (host && age > 76 && !reset) { var m = match.State.Value; m.Phase = MatchPhase.Finished; match.State.Value = m; match.ReturnToRoom(); reset = true; }
            if (!host && age > 78) reset = match.State.Value.Phase == MatchPhase.Practice && match.AppliedPaintSequence == 0;
            if (s.HeroId == 8 && age > 10 && age < 19)
            {
                // Authority changes before LateUpdate publishes the matching visual pose.
                if (s.ShowsSwimBody && p.SwimBody.BodyRenderer.enabled && age > 16) Capture("paper");
                else if (!s.ShowsSwimBody && age < 14) Capture("combat");
            }
            if (age > (host ? 90 : 85)) Finish(null);
        }
        public static void ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
            if (instance == null || !instance.ready || instance.ending) return;
            double age = player.NetworkManager.ServerTime.Time;
            input.Move = Vector2.zero; input.Look = new Vector2((player.PresentedState.Team == 1 ? 0 : 180) + 22, 12);
            input.Fire = !player.HeroChangePending && player.PresentedState.HeroId == 8 &&
                (age >= 4 && age < 14 || age >= 28 && age < 40 || (age >= 52.5 && age < 53.4 && player.PresentedState.Team == 1 || age >= 54 && age < 55 && player.PresentedState.Team == 2) || age >= 62 && age < 65);
            if (age >= 52 && age < 56) input.Look = new Vector2(player.PresentedState.Team == 1 ? 0 : 180, FightPitch(player));
            input.Swim = age >= 15 && age < 19 || age >= 42 && age < 48;
            // Validate growth at the original open spawn. Exercise locomotion after the switch,
            // so moving up against cover cannot turn an expected clearance rejection into a harness failure.
            if (input.Swim && age >= 42) input.Move = Vector2.up * .2f;
        }
        // Aim the open-lane fixture at the small opponent's torso using the current camera pivot.
        static float FightPitch(PrototypePlayer player) => Mathf.Atan2(player.Presentation.CameraPivot.y - .6f, 2) * Mathf.Rad2Deg;
        void Capture(string name)
        {
            if (!captures.Add(name)) return;
            string output = Path.GetDirectoryName(Path.GetFullPath(Arg("-splooshOutput", "Reports/SplooshGirl/network.txt")));
            Directory.CreateDirectory(output);
            var camera = new GameObject("Sploosh capture camera").AddComponent<Camera>(); camera.CopyFrom(Camera.main); camera.enabled = false;
            camera.transform.SetPositionAndRotation(Camera.main.transform.position, Camera.main.transform.rotation);
            camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>().renderPostProcessing = false;
            var target = new RenderTexture(1280, 720, 24); target.Create();
            var previous = RenderTexture.active; var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
                File.WriteAllBytes(Path.Combine(output, (host ? "host-" : "client-") + name + ".png"), pixels.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; target.Release(); Destroy(camera.gameObject); Destroy(target); Destroy(pixels); }
        }
        void OnLog(string message, string stack, LogType type)
        {
            if (message.StartsWith("[INK] Snapshot applied")) snapshots++;
            if ((type == LogType.Error || type == LogType.Exception) && errors.Count < 12) errors.Add(message);
        }
        async void Finish(string error)
        {
            if (ending) return; ending = true;
            bool passed = error == null && errors.Count == 0 && players >= 2 && emitted > 25 && paper && remoteSmall && tall && smallAgain &&
                captured && paintSequence > 0 && respawned && reset && receivedShot && confirmedEnemyHit && (host || resync && snapshots >= 2);
            string cameraOutput = Arg("-splooshOutput", "Reports/SplooshGirl/network.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cameraOutput)));
            passed &= CameraReticleNetworkProbe.Finish(cameraOutput);
            string report = $"passed={passed}\nrole={(host ? "host" : "client")}\nerror={error}\nshots8={emitted}\nmaxPlayers={players}\nsmallRemoteBody={remoteSmall}\npaperObserved={paper}\nswitch8to1to8={tall && smallAgain}\nfinalPaintHash={hash}\nfinalPaintSequence={paintSequence}\ninitialSyncAndCapture={captured}\nresyncRequested={resync}\nappliedSnapshots={snapshots}\nrespawnRetained={respawned}\nroundReset={reset}\n";
            report += $"receivedDamageDuringFight={receivedShot}\nconfirmedEnemyProjectileHit={confirmedEnemyHit}\n";
            var signature = typeof(PrototypeApp).GetField("_signature", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(PrototypeApp.Current) as byte[];
            report += "contentSignature=" + (signature == null ? "unavailable" : BitConverter.ToString(signature).Replace("-", "").ToLowerInvariant()) + "\n";
            report += "runtimeErrors=" + string.Join(" | ", errors) + "\nEnvironment=two independent processes on one PC; not physical two-machine/device acceptance\n";
            string path = Arg("-splooshOutput", "Reports/SplooshGirl/network.txt"); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); File.WriteAllText(path, report);
            Debug.Log("[SPLOOSH] " + report);
            if (PrototypeApp.Current != null && PrototypeApp.Current.InRoom) await PrototypeApp.Current.Leave();
            Application.Quit(passed ? 0 : 1);
        }
        void OnDestroy() { Application.logMessageReceived -= OnLog; if (instance == this) instance = null; }
    }
}
#endif
