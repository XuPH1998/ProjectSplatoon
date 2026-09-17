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
        public const int VersionPoolLimit = 12;
        public int VersionPoolCount => _flights.Count;
        public int ParticleCount { get; private set; }
        public int ActiveGroups { get; private set; }
        public int ActiveShots { get; private set; }
        public int DroppedSamples { get; private set; }
        // Conservative gate for the two gameplay-owned ink layers. Unknown filters still render.
        public bool HasCompositeContent(int layerMask)
        {
            if (_explosionEffects.Count > 0) return true;
            if (layerMask == 1 << InkFlightPresentation.Layer)
            {
                foreach (var version in _flights)
                    if (version.Flight.ActiveShots > 0 || version.Flight.ParticleCount > 0) return true;
                foreach (var muzzle in _muzzles) if (muzzle != null && muzzle.HasLiveParticles) return true;
                return false;
            }
            if (layerMask != 1 << 9) return true;
            if (_effects.Count > 0) return true;
            foreach (var player in PrototypePlayer.ByOwner.Values)
            {
                var swim = player != null && player.CharacterView != null ? player.CharacterView.SwimEffect : null;
                if (swim != null && swim.IsAlive(true)) return true;
            }
            return false;
        }
        // 仅用于诊断采样；逐帧表现更新不枚举此迭代器。
        public IEnumerable<ParticleSystem> Streams
        {
            get { foreach (var version in _flights) foreach (var stream in version.Flight.Streams) yield return stream; }
        }
        sealed class FlightVersion
        {
            public AmmoRuntimeConfig Ammo;
            public InkFlightPresentation Flight;
        }
        double _cutoff = double.NegativeInfinity;
        uint _round;
        readonly HashSet<(uint round, uint id)> _completed = new(8192);
        readonly Queue<(uint round, uint id)> _completionOrder = new(8192);
        Camera _camera;
        readonly List<FlightVersion> _flights = new(VersionPoolLimit);
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
            _camera = Camera.main;
            for (int i = 0; ImpactPrefab != null && i < 96; i++)
            {
                var effect = Instantiate(ImpactPrefab, transform).GetComponent<InkImpactEffect>();
                effect.Stop(); effect.gameObject.SetActive(false); _pool.Enqueue(effect);
            }
        }
        public void Spawn(InkShot shot)
        {
            if (shot.Round < _round || shot.Born < _cutoff - .001 || _completed.Contains((shot.Round, shot.Id))) return;
            _round = shot.Round;
            if (_shotAmmo.ContainsKey((shot.Round, shot.Id))) return;
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
            if (_completed.Add((impact.Round, impact.Id)))
            {
                if (_completionOrder.Count >= 8192) _completed.Remove(_completionOrder.Dequeue());
                _completionOrder.Enqueue((impact.Round, impact.Id));
            }
            FindFlight(ammo)?.Complete(impact);
            if (ammo != null && ammo.HasExplosion) { _shotAmmo.Remove((impact.Round, impact.Id)); return; }
            _shotAmmo.Remove((impact.Round, impact.Id));
            if (!impact.Hit || _pool.Count == 0) return;
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
            if (PrototypeMatch.Current == null) return;
            double now = PrototypeMatch.Current.NetworkManager.ServerTime.Time;
            if (_camera == null) _camera = Camera.main;
            UpdateFlights(now, _camera);
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
            ClearFlights(cutoff);
            for (int i = _muzzles.Count - 1; i >= 0; i--) if (_muzzles[i] != null) _muzzles[i].Stop(); else _muzzles.RemoveAt(i);
            foreach (var pair in _effects) { pair.effect.Stop(); pair.effect.gameObject.SetActive(false); _pool.Enqueue(pair.effect); }
            _effects.Clear();
            foreach (var pair in _explosionEffects) if (pair.effect != null) { pair.effect.SetActive(false); if (!_explosionPool.TryGetValue(pair.prefab, out var pool)) _explosionPool[pair.prefab] = pool = new Queue<GameObject>(); if (pool.Count < 16) pool.Enqueue(pair.effect); else Destroy(pair.effect); }
            _explosionEffects.Clear(); _seenExplosions.Clear();
        }
        public void ClearFlights(double cutoff = double.NegativeInfinity)
        {
            _cutoff = cutoff;
            foreach (var version in _flights) version.Flight.Clear(cutoff);
            _shotAmmo.Clear(); _completed.Clear(); _completionOrder.Clear();
            ParticleCount = ActiveGroups = ActiveShots = DroppedSamples = 0;
        }
        public void UpdateFlights(double now, Camera camera)
        {
            ParticleCount = ActiveGroups = ActiveShots = DroppedSamples = 0;
            foreach (var version in _flights)
            {
                version.Flight.Update(now, camera);
                ParticleCount += version.Flight.ParticleCount; ActiveGroups += version.Flight.ActiveGroups;
                ActiveShots += version.Flight.ActiveShots; DroppedSamples += version.Flight.DroppedSamples;
            }
            PruneFlights();
        }
        public int CopyParticles(byte team, ParticleSystem.Particle[] destination)
        {
            int count = 0;
            foreach (var version in _flights) count += version.Flight.CopyParticles(team, destination, count);
            return count;
        }
        InkFlightPresentation FindFlight(AmmoRuntimeConfig ammo)
        {
            if (ammo == null) return null;
            foreach (var version in _flights) if (version.Ammo.SameValues(ammo)) return version.Flight;
            return null;
        }
        bool HasLiveMuzzle(AmmoRuntimeConfig ammo)
        {
            foreach (var muzzle in _muzzles)
                if (muzzle != null && muzzle.Ammo.SameValues(ammo) && muzzle.HasLiveParticles) return true;
            return false;
        }
        void PruneFlights(AmmoRuntimeConfig replacing = null)
        {
            for (int i = _flights.Count - 1; i >= 0; i--)
            {
                var version = _flights[i];
                if (version.Flight.ActiveShots > 0 || HasLiveMuzzle(version.Ammo) || WeaponConfigService.Current.UsesAmmo(version.Ammo, replacing)) continue;
                version.Flight.Dispose(); _flights.RemoveAt(i);
            }
        }
        // 在提交调参事务之前预留完整版本，容量不足时不更改当前配置。
        public bool TryPrepareAmmo(AmmoRuntimeConfig ammo, AmmoRuntimeConfig replacing = null)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return true;
            if (FindFlight(ammo) != null) return true;
            PruneFlights(replacing);
            if (_flights.Count >= VersionPoolLimit) return false;
            if (ammo?.FlightPrefab == null) return false;
            _flights.Add(new FlightVersion { Ammo = ammo, Flight = new InkFlightPresentation(transform, ammo) });
            return true;
        }
        InkFlightPresentation ForAmmo(AmmoRuntimeConfig ammo) => TryPrepareAmmo(ammo) ? FindFlight(ammo) : null;
        public InkMuzzleEmitter CreateMuzzle(Transform nozzle, WeaponRuntimeConfig weapon)
        {
            var ammo = weapon?.Ammo;
            if (nozzle == null || ammo?.MuzzlePrefab == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
            var ps = Instantiate(ammo.MuzzlePrefab, nozzle);
            ps.transform.localPosition = Vector3.zero; ps.transform.localRotation = Quaternion.identity;
            var emitter = ps.gameObject.AddComponent<InkMuzzleEmitter>(); emitter.Initialize(ammo);
            ps.gameObject.SetActive(true);
            _muzzles.RemoveAll(MuzzleDestroyed); _muzzles.Add(emitter); return emitter;
        }
        static bool MuzzleDestroyed(InkMuzzleEmitter muzzle) => muzzle == null;
        private void OnDestroy()
        {
            foreach (var version in _flights) version.Flight.Dispose();
            _flights.Clear();
            foreach (var muzzle in _muzzles) if (muzzle != null) HeroViewBinder.Destroy(muzzle.gameObject);
            foreach (var e in _explosionEffects) if (e.effect != null) Destroy(e.effect);
            foreach (var pool in _explosionPool.Values) while (pool.Count > 0) { var go = pool.Dequeue(); if (go != null) Destroy(go); }
            if (Current == this) Current = null;
        }
    }
}
