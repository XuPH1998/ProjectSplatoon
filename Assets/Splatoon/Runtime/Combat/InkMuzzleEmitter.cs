using UnityEngine;
using Splatoon.Prototype;
using Splatoon.Config;

namespace Splatoon.Combat
{
    /// <summary>Reference muzzle simulation; collision only removes cosmetic particles.</summary>
    public sealed class InkMuzzleEmitter : MonoBehaviour
    {
        ParticleSystem _system;
        public AmmoRuntimeConfig Ammo { get; private set; }
        bool _retired;
        public bool HasLiveParticles => _system != null && _system.IsAlive(true);
        double _nextBurst;
        bool _active, _continuous;
        uint _seed;
        byte _team;
        ParticleSystem.MinMaxCurve _startSize;
        public int BurstCount { get; private set; }
        public ParticleSystem System => _system;
        public void Initialize(AmmoRuntimeConfig ammo)
        {
            Ammo = ammo; _system = GetComponent<ParticleSystem>();
            _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _system.main; main.playOnAwake = false; main.maxParticles = 160;
            _startSize = main.startSize;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = _system.emission; emission.enabled = false;
            var collision = _system.collision; collision.sendCollisionMessages = false;
            // Scene solids occlude/remove drops; player hit proxies never receive particle callbacks.
            collision.collidesWith = 1 << LayerMask.NameToLayer("Default");
            var sub = _system.subEmitters; sub.enabled = false;
            _system.useAutoRandomSeed = false; _system.randomSeed = 1;
            _system.Play(false);
        }
        public void Shot(uint seed, byte team, bool continuous, double now, double born)
        {
            if (_retired || _system == null || now - born > .2) return;
            _seed = seed == 0 ? 1u : seed; _team = team;
            bool wasActive = _active && _continuous;
            _active = true; _continuous = continuous;
            if (!continuous || !wasActive) { Burst(); _nextBurst = now + Ammo.MuzzleInterval; }
        }
        public void Present(bool firing, bool allowed, double now)
        {
            if (_retired) return;
            if (!allowed) { Stop(); return; }
            if (!firing) { _active = false; return; }
            if (!_active || !_continuous || now < _nextBurst) return;
            // A delayed frame emits one current burst, not an accumulated wall of historical particles.
            Burst(); _nextBurst = now + Ammo.MuzzleInterval;
        }
        void Burst()
        {
            using var marker = FramePerformance.Muzzle.Auto();
            FramePerformance.MuzzleParticles += Ammo.MuzzleBurstCount;
            var main = _system.main; main.startColor = PrototypeArena.TeamColor(_team);
            // Changing a playing system's seed is unsupported and would disturb existing drops.
            // Each new particle receives its own seed while the system keeps simulating naturally.
            for (int i = 0; i < Ammo.MuzzleBurstCount; i++)
            {
                _seed = _seed * 1664525u + 1013904223u; if (_seed == 0) _seed = 1;
                // Unity's seeded EmitParams path retains the serialized Y/Z defaults (1 m)
                // for this imported uniform-size system. Set all three axes to its authored size.
                uint sample = _seed;
                float size = _startSize.Evaluate(0, InkBallistics.Random01(ref sample));
                _system.Emit(new ParticleSystem.EmitParams { randomSeed = _seed, startSize3D = Vector3.one * size, applyShapeToPosition = true }, 1);
            }
            BurstCount++;
        }
        public void Retire(Transform parent)
        {
            if (_retired) return;
            _retired = true; _active = false;
            transform.SetParent(parent, true);
            _system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
        void LateUpdate()
        {
            if (_retired && !HasLiveParticles) Destroy(gameObject);
        }
        public void Stop()
        {
            _active = false;
            if (_system == null) return;
            _system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); _system.Play(false);
        }
    }
}
