#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using Splatoon.Combat;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Optional rendered acceptance; never instantiated in ordinary rooms.
    [DefaultExecutionOrder(500)]
    public sealed class CameraReticleNetworkProbe : MonoBehaviour
    {
        static CameraReticleNetworkProbe instance;
        int frames, badFov, badProjection, badSwim;
        bool targetWithoutFire, hit, blocked, recovered, swim, killed;
        GameObject obstacle;
        readonly TpsAimSolver solver = new();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Initialize()
        {
            if (!Environment.GetCommandLineArgs().Contains("-cameraAcceptance") || instance != null) return;
            var root = new GameObject("Camera reticle network probe"); DontDestroyOnLoad(root);
            instance = root.AddComponent<CameraReticleNetworkProbe>();
        }
        void LateUpdate()
        {
            var player = PrototypePlayer.Local;
            if (player == null || Camera.main == null || !player.IsSpawned) return;
            var s = player.PresentedState;
            double age = player.NetworkManager.ServerTime.Time;
            if (s.Health <= 0 || player.HeroChangePending) return;
            frames++;
            PrototypeApp.Current.CaptureMouse(true);
            if (Mathf.Abs(Camera.main.fieldOfView - 60) > .001f) badFov++;
            // The local input look can be ahead of the most recent network snapshot.
            var look = (Vector2)typeof(PrototypePlayer).GetField("_look", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(player);
            s.Yaw = look.x; s.Pitch = look.y;
            var aim = solver.Resolve(player, s, s.NextMuzzle);
            var w = Splatoon.Config.GameplayConfig.GetWeapon(s.HeroId);
            var point = w.AimMode == Splatoon.Config.WeaponAimMode.WeaponReference && !s.ShowsSwimBody
                ? WeaponImpactPrediction.Guide(solver, aim, w, s, player.PlayerId).Point : aim.AimPoint;
            var expected = TpsAimSolver.ReticleViewport(Camera.main, point);
            if (Vector2.Distance(expected, player.ReticleViewport) > .002f) badProjection++;
            if (s.ShowsSwimBody)
            {
                swim = true;
                if (player.TargetReticleVisible || player.ImpactReticleVisible) badSwim++;
            }
            if (!targetWithoutFire && age >= 52 && age < 52.5 && player.TargetReticleVisible && !s.Firing && player.HitConfirmedUntil <= Time.unscaledTimeAsDouble)
            { targetWithoutFire = true; }
            if (!hit && age >= 52.5 && age < 56 && player.HitConfirmedUntil > Time.unscaledTimeAsDouble)
            { hit = true; }
            if (!killed && player.LastHitKilled && player.HitConfirmedUntil > Time.unscaledTimeAsDouble)
            { killed = true; }
            if (age >= 66 && age < 68)
            {
                if (obstacle == null)
                {
                    obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.name = "Camera probe muzzle obstruction";
                    obstacle.transform.position = aim.Muzzle + aim.InitialDirection * .25f;
                    obstacle.transform.localScale = Vector3.one * .2f; Physics.SyncTransforms();
                }
                else blocked |= player.MuzzleBlocked;
            }
            else if (age >= 68 && obstacle != null) { Destroy(obstacle); obstacle = null; }
            if (age >= 69 && age < 72) recovered |= !player.MuzzleBlocked;
        }
        public static bool Finish(string output)
        {
            if (instance == null) return true;
            var p = instance;
            bool passed = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && p.frames > 100 && p.badFov == 0 && p.badProjection == 0 && p.badSwim == 0 && p.swim && p.targetWithoutFire && p.hit && p.blocked && p.recovered;
            File.WriteAllText(output + ".camera.txt", $"passed={passed}\nsampledFrames={p.frames}\ngraphicsDevice={SystemInfo.graphicsDeviceType}\nbadFov={p.badFov}\nbadProjection={p.badProjection}\nbadSwim={p.badSwim}\nswimObserved={p.swim}\ntargetWithoutFire={p.targetWithoutFire}\nactualHit={p.hit}\nactualKill={p.killed}\nmuzzleBlocked={p.blocked}\nobstructionRecovered={p.recovered}\n");
            return passed;
        }
        void OnDestroy() { if (instance == this) instance = null; if (obstacle != null) Destroy(obstacle); }
    }
}
#endif
