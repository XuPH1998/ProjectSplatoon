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
        readonly Dictionary<ushort, InkFlightPresentation> _flights = new();
        readonly Dictionary<(uint round, uint id), AmmoRuntimeConfig> _shotAmmo = new();
        readonly List<InkMuzzleEmitter> _muzzles = new(16);
        private readonly Queue<InkImpactEffect> _pool = new();
        private readonly List<(InkImpactEffect effect, float until)> _effects = new(96);
        readonly List<(GameObject effect, float until, GameObject prefab)> _explosionEffects = new(32);
        readonly Dictionary<GameObject, Queue<GameObject>> _explosionPool = new();
        readonly HashSet<(uint round, uint id)> _seenExplosions = new();
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
            if (shot.Configuration?.Ammo != null) _shotAmmo[(shot.Round, shot.Id)] = shot.Configuration.Ammo;
            var flight = ForAmmo(shot.Configuration?.Ammo);
            double now = PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : shot.Born;
            if (flight == null || !flight.Spawn(shot, now)) return;
            if (PrototypePlayer.ByOwner.TryGetValue(shot.Shooter, out var player)) player.PredictShotFeedback(shot);
        }
        public void Impact(InkImpact impact)
        {
            if (impact.Damage > 0 && PrototypePlayer.ByOwner.TryGetValue(impact.Shooter, out var shooter)) shooter.ConfirmHit(impact);
            _shotAmmo.TryGetValue((impact.Round, impact.Id), out var ammo);
            (ammo != null ? FlightFor(ammo) : Flight)?.Complete(impact);
            if (ammo != null && ammo.HasExplosion) { _shotAmmo.Remove((impact.Round, impact.Id)); return; }
            _shotAmmo.Remove((impact.Round, impact.Id));
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
            for (int i = _explosionEffects.Count - 1; i >= 0; i--) if (Time.time >= _explosionEffects[i].until)
            { var item = _explosionEffects[i]; if (item.effect != null) { item.effect.SetActive(false); if (!_explosionPool.TryGetValue(item.prefab, out var pool)) _explosionPool[item.prefab] = pool = new Queue<GameObject>(); if (pool.Count < 16) pool.Enqueue(item.effect); else Destroy(item.effect); } _explosionEffects.RemoveAt(i); }
            if (Flight == null || PrototypeMatch.Current == null) return;
            double now = PrototypeMatch.Current.NetworkManager.ServerTime.Time;
            if (_camera == null) _camera = Camera.main;
            Flight.Update(now, _camera);
            foreach (var flight in _flights.Values) if (!ReferenceEquals(flight, Flight)) flight.Update(now, _camera);
        }
        public void Explosion(InkExplosionEvent explosion)
        {
            if (!_seenExplosions.Add((explosion.Round, explosion.ShotId))) return;
            if (PrototypeMatch.Current == null || explosion.Round != PrototypeMatch.Current.State.Value.Round) return;
            var ammo = WeaponConfigService.Current.ForShot(explosion.HeroId, explosion.ConfigurationRevision)?.Ammo;
            if (ammo == null || !ammo.HasExplosion) return;
            var rotation = explosion.Normal.sqrMagnitude > .01f ? Quaternion.LookRotation(explosion.Normal) : Quaternion.identity;
            GameObject go;
            if (_explosionPool.TryGetValue(ammo.ExplosionPrefab, out var pool) && pool.Count > 0) { go = pool.Dequeue(); go.transform.SetPositionAndRotation(explosion.Position, rotation); go.SetActive(true); }
            else go = Instantiate(ammo.ExplosionPrefab, explosion.Position, rotation, transform);
            go.name = "Ink explosion";
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) { ps.useAutoRandomSeed = false; ps.randomSeed = explosion.Seed == 0 ? 1u : explosion.Seed; ps.Play(true); }
            _explosionEffects.Add((go, Time.time + 3f, ammo.ExplosionPrefab));
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
            var cutoff = PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : double.NegativeInfinity;
            foreach (var flight in _flights.Values) flight.Clear(cutoff);
            Flight?.Clear(cutoff); _shotAmmo.Clear();
            for (int i = _muzzles.Count - 1; i >= 0; i--) if (_muzzles[i] != null) _muzzles[i].Stop(); else _muzzles.RemoveAt(i);
            foreach (var pair in _effects) { pair.effect.Stop(); pair.effect.gameObject.SetActive(false); _pool.Enqueue(pair.effect); }
            _effects.Clear();
            foreach (var pair in _explosionEffects) if (pair.effect != null) { pair.effect.SetActive(false); if (!_explosionPool.TryGetValue(pair.prefab, out var pool)) _explosionPool[pair.prefab] = pool = new Queue<GameObject>(); if (pool.Count < 16) pool.Enqueue(pair.effect); else Destroy(pair.effect); }
            _explosionEffects.Clear(); _seenExplosions.Clear();
        }
        InkFlightPresentation ForAmmo(AmmoRuntimeConfig ammo)
        {
            if (ammo == null) return Flight;
            if (_flights.TryGetValue(ammo.AmmoId, out var existing)) return existing;
            var profile = ammo.FlightProfile ?? FlightProfile;
            var prefab = ammo.FlightPrefab != null ? ammo.FlightPrefab : profile != null ? profile.FlightPrefab : StreamPrefab;
            if (prefab == null) return null;
            var created = new InkFlightPresentation(transform, prefab, profile);
            _flights[ammo.AmmoId] = created; return created;
        }
        InkFlightPresentation FlightFor(AmmoRuntimeConfig ammo) => ForAmmo(ammo);
        public InkMuzzleEmitter CreateMuzzle(Transform nozzle, WeaponRuntimeConfig weapon = null)
        {
            if (nozzle == null) return null;
            var ammo = weapon?.Ammo;
            var profile = ammo?.FlightProfile ?? FlightProfile;
            var prefab = ammo?.MuzzlePrefab != null ? ammo.MuzzlePrefab : profile?.MuzzlePrefab;
            if (prefab == null) return null;
            var ps = Instantiate(prefab, nozzle);
            ps.transform.localPosition = Vector3.zero; ps.transform.localRotation = Quaternion.identity;
            var emitter = ps.gameObject.AddComponent<InkMuzzleEmitter>(); emitter.Initialize(profile);
            _muzzles.RemoveAll(MuzzleDestroyed); _muzzles.Add(emitter); return emitter;
        }
        static bool MuzzleDestroyed(InkMuzzleEmitter muzzle) => muzzle == null;
        private void OnDestroy() { Flight?.Dispose(); foreach (var f in _flights.Values) if (!ReferenceEquals(f, Flight)) f.Dispose(); foreach (var e in _explosionEffects) if (e.effect != null) Destroy(e.effect); foreach (var pool in _explosionPool.Values) while (pool.Count > 0) { var go = pool.Dequeue(); if (go != null) Destroy(go); } if (Current == this) Current = null; }
    }
}
