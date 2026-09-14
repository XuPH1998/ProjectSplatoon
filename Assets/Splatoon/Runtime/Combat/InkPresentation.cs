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
        [Tooltip("飞行墨水视觉尺寸")] public float BlobSize = .17f;
        [Tooltip("补充墨流密度的每发粒子数")] public int BlobsPerShot = 2;
        [Tooltip("移动时每米补充的视觉粒子；不增加权威墨弹")] public float BlobsPerMeter = 10;
        private readonly Dictionary<uint, InkShot> _shots = new(256);
        private readonly Dictionary<uint, int> _blobCounts = new(256);
        private readonly Dictionary<ulong, (Vector3 position, double born, float remainder)> _emitters = new();
        private readonly List<uint> _expired = new(256);
        private readonly ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[1024];
        private ParticleSystem[] _streams;
        private readonly Queue<InkImpactEffect> _pool = new();
        private readonly List<(InkImpactEffect effect, float until)> _effects = new(96);
        private void Awake()
        {
            Current = this;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            _streams = new[] { Instantiate(StreamPrefab, transform), Instantiate(StreamPrefab, transform) };
            foreach (var stream in _streams)
            {
                var emission = stream.emission; emission.enabled = false;
                var collision = stream.collision; collision.enabled = false;
                var sub = stream.subEmitters; sub.enabled = false;
                var main = stream.main; main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = 1024; main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate; main.startSpeed = 0; main.gravityModifier = 0;
                stream.Play();
            }
            for (int i = 0; i < 96; i++)
            {
                var effect = Instantiate(ImpactPrefab, transform).GetComponent<InkImpactEffect>();
                effect.Stop(); effect.gameObject.SetActive(false); _pool.Enqueue(effect);
            }
        }
        public void Spawn(InkShot shot)
        {
            if (_streams == null || _shots.ContainsKey(shot.Id)) return;
            _shots.Add(shot.Id, shot);
            float extra = 0;
            if (_emitters.TryGetValue(shot.Shooter, out var previous) && shot.Born - previous.born < .1)
                extra = previous.remainder + Vector3.Distance(previous.position, shot.Origin) * BlobsPerMeter;
            int added = Mathf.FloorToInt(extra);
            _blobCounts[shot.Id] = Mathf.Clamp(BlobsPerShot + added, 1, 32);
            _emitters[shot.Shooter] = (shot.Origin, shot.Born, extra - added);
            if (PrototypePlayer.ByOwner.TryGetValue(shot.Shooter, out var player)) player.PredictShotFeedback(shot);
        }
        public void Impact(InkImpact impact)
        {
            if (impact.Damage > 0 && PrototypePlayer.ByOwner.TryGetValue(impact.Shooter, out var shooter)) shooter.ConfirmHit(impact);
            _shots.Remove(impact.Id);
            _blobCounts.Remove(impact.Id);
            if (_streams == null || !impact.Hit || _pool.Count == 0) return;
            var effect = _pool.Dequeue(); effect.gameObject.SetActive(true);
            var normal = impact.Normal.sqrMagnitude > .01f ? impact.Normal.normalized : Vector3.up;
            effect.transform.SetPositionAndRotation(impact.Position + normal * .02f, Quaternion.LookRotation(normal));
            effect.Play(PrototypeArena.TeamColor(impact.Team), InkImpactEffect.Seed(impact));
            _effects.Add((effect, Time.time + InkImpactEffect.RecycleTimeout));
        }
        private void LateUpdate()
        {
            RecycleImpacts();
            if (_streams == null || PrototypeMatch.Current == null) return;
            double now = PrototypeMatch.Current.NetworkManager.ServerTime.Time;
            _expired.Clear();
            for (byte team = 1; team <= 2; team++)
            {
                int count = 0;
                foreach (var pair in _shots)
                {
                    var shot = pair.Value; if (shot.Team != team) continue;
                    var w = LubanConfigService.Current.Tables.TbHero.Get(shot.HeroId); double age = now - shot.Born;
                    if (age > w.Lifetime + .05) { _expired.Add(pair.Key); continue; }
                    int blobs = _blobCounts[pair.Key];
                    for (int n = 0; n < blobs && count < _particles.Length; n++)
                    {
                        double t = System.Math.Max(0, System.Math.Min(w.Lifetime, age - n * .018 / blobs));
                        _particles[count++] = new ParticleSystem.Particle { position = InkBallistics.Position(shot, w, t), startColor = PrototypeArena.TeamColor(team), startSize = BlobSize, remainingLifetime = 1, startLifetime = 1, randomSeed = shot.Seed + (uint)n, velocity = Vector3.zero };
                    }
                }
                _streams[team - 1].SetParticles(_particles, count);
            }
            foreach (uint id in _expired) { _shots.Remove(id); _blobCounts.Remove(id); }
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
            _shots.Clear();
            _blobCounts.Clear(); _emitters.Clear();
            if (_streams != null) foreach (var stream in _streams) stream.Clear();
            foreach (var pair in _effects) { pair.effect.Stop(); pair.effect.gameObject.SetActive(false); _pool.Enqueue(pair.effect); }
            _effects.Clear();
        }
        private void OnDestroy() { if (Current == this) Current = null; }
    }
}
