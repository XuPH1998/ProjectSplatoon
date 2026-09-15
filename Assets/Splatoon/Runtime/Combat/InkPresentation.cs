using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    /// <summary>Bounded pools; particles have no gameplay collision callbacks.</summary>
    public sealed class InkPresentation : MonoBehaviour
    {
        public static InkPresentation Current { get; private set; }
        public ParticleSystem StreamPrefab, ImpactPrefab;
        public InkFlightProfile FlightProfile;
        public InkFlightPresentation Flight { get; private set; }
        Camera _camera;
        readonly List<InkMuzzleEmitter> _muzzles = new(16);
        private readonly Queue<InkImpactEffect> _pool = new();
        private readonly List<(InkImpactEffect effect, float until)> _effects = new(96);
        private void Awake()
        {
            Current = this;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            Flight = new InkFlightPresentation(transform, FlightProfile != null ? FlightProfile.FlightPrefab : StreamPrefab, FlightProfile);
            _camera = Camera.main;
            for (int i = 0; i < 96; i++)
            {
                var effect = Instantiate(ImpactPrefab, transform).GetComponent<InkImpactEffect>();
                effect.Stop(); effect.gameObject.SetActive(false); _pool.Enqueue(effect);
            }
        }
        public void Spawn(InkShot shot)
        {
            if (Flight == null) return;
            shot.Configuration ??= WeaponConfigService.Current.ForShot(shot.HeroId, shot.ConfigurationRevision);
            double now = PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : shot.Born;
            if (!Flight.Spawn(shot, now)) return;
            if (PrototypePlayer.ByOwner.TryGetValue(shot.Shooter, out var player)) player.PredictShotFeedback(shot);
        }
        public void Impact(InkImpact impact)
        {
            if (impact.Damage > 0 && PrototypePlayer.ByOwner.TryGetValue(impact.Shooter, out var shooter)) shooter.ConfirmHit(impact);
            Flight?.Complete(impact);
            if (Flight == null || !impact.Hit || _pool.Count == 0) return;
            var effect = _pool.Dequeue(); effect.gameObject.SetActive(true);
            var normal = impact.Normal.sqrMagnitude > .01f ? impact.Normal.normalized : Vector3.up;
            effect.transform.SetPositionAndRotation(impact.Position + normal * .02f, Quaternion.LookRotation(normal));
            effect.Play(PrototypeArena.TeamColor(impact.Team), InkImpactEffect.Seed(impact));
            _effects.Add((effect, Time.time + InkImpactEffect.RecycleTimeout));
        }
        private void LateUpdate()
        {
            RecycleImpacts();
            if (Flight == null || PrototypeMatch.Current == null) return;
            double now = PrototypeMatch.Current.NetworkManager.ServerTime.Time;
            if (_camera == null) _camera = Camera.main;
            Flight.Update(now, _camera);
        }
        private void RecycleImpacts()
        {
            for (int i = _effects.Count - 1; i >= 0; i--)
                if (Time.time >= _effects[i].until || !_effects[i].effect.IsAlive)
                {
                    var effect = _effects[i].effect; effect.Stop(); effect.gameObject.SetActive(false);
                    _pool.Enqueue(effect); _effects.RemoveAt(i);
                }
        }
        public void Clear()
        {
            Flight?.Clear(PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : double.NegativeInfinity);
            for (int i = _muzzles.Count - 1; i >= 0; i--) if (_muzzles[i] != null) _muzzles[i].Stop(); else _muzzles.RemoveAt(i);
            foreach (var pair in _effects) { pair.effect.Stop(); pair.effect.gameObject.SetActive(false); _pool.Enqueue(pair.effect); }
            _effects.Clear();
        }
        public InkMuzzleEmitter CreateMuzzle(Transform nozzle)
        {
            if (Flight == null || FlightProfile == null || FlightProfile.MuzzlePrefab == null || nozzle == null) return null;
            var ps = Instantiate(FlightProfile.MuzzlePrefab, nozzle);
            ps.transform.localPosition = Vector3.zero; ps.transform.localRotation = Quaternion.identity;
            var emitter = ps.gameObject.AddComponent<InkMuzzleEmitter>(); emitter.Initialize(FlightProfile);
            _muzzles.RemoveAll(MuzzleDestroyed); _muzzles.Add(emitter); return emitter;
        }
        static bool MuzzleDestroyed(InkMuzzleEmitter muzzle) => muzzle == null;
        private void OnDestroy() { Flight?.Dispose(); if (Current == this) Current = null; }
    }
}
