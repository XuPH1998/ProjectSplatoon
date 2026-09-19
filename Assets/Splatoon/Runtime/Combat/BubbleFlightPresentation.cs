using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Pooled mesh bubbles. Only server events control contacts and termination.</summary>
    public sealed class BubbleFlightPresentation : IDisposable
    {
        public const int Capacity = 384;
        public const double RenderDelay = .1;
        sealed class Visual
        {
            public GameObject Object, Prefab;
            public Renderer Renderer;
            public InkShot Shot;
            public InkBounce[] Segments = new InkBounce[17];
            public int Count;
        }
        readonly Transform _parent;
        readonly Dictionary<(uint round, uint id), Visual> _active = new();
        readonly List<(uint round, uint id)> _expired = new(Capacity);
        readonly Stack<Visual> _pool = new();
        readonly List<(Visual visual, float born, Vector3 origin, Vector3 velocity)> _fragments = new(96);
        readonly Dictionary<(uint round, uint id), InkBounce> _pending = new();
        readonly MaterialPropertyBlock _block = new();
        readonly AudioSource[] _voices = new AudioSource[12];
        int _voice;
        public int ActiveCount => _active.Count;
        public int Dropped { get; private set; }
        public BubbleFlightPresentation(Transform parent) => _parent = parent;

        public bool Spawn(InkShot shot, double now)
        {
            var key = (shot.Round, shot.Id);
            if (_active.ContainsKey(key)) return false;
            if (_active.Count >= Capacity) { Dropped++; return false; }
            var prefab = shot.Configuration.Ammo.BubblePrefab;
            if (prefab == null || now > shot.Born + shot.Configuration.Lifetime + RenderDelay) return false;
            var v = Acquire(shot);
            v.Count = 1; v.Segments[0] = InkBounce.Initial(shot);
            v.Object.transform.position = shot.Origin; v.Object.transform.rotation = Quaternion.identity;
            v.Object.transform.localScale = Vector3.one * shot.Configuration.CollisionRadius * 2;
            _active.Add(key, v);
            if (_pending.Remove(key, out var pending)) Bounce(pending, false);
            return true;
        }
        Visual Acquire(InkShot shot)
        {
            var prefab = shot.Configuration.Ammo.BubblePrefab;
            var v = _pool.Count > 0 ? _pool.Pop() : new Visual();
            if (v.Object == null || v.Prefab != prefab)
            {
                if (v.Object != null) HeroViewBinder.Destroy(v.Object);
                v.Object = UnityEngine.Object.Instantiate(prefab, _parent); v.Prefab = prefab;
                v.Renderer = v.Object.GetComponent<Renderer>();
            }
            v.Shot = shot;
            _block.Clear(); _block.SetColor("_BaseColor", PrototypeArena.TeamColor(shot.Team));
            v.Renderer.SetPropertyBlock(_block); v.Object.SetActive(true);
            return v;
        }
        public void Bounce(InkBounce bounce, bool sound = true)
        {
            var key = (bounce.Round, bounce.Id);
            if (!_active.TryGetValue(key, out var v))
            {
                if (_pending.TryGetValue(key, out var old) && old.Sequence >= bounce.Sequence) return;
                if (_pending.Count < Capacity) _pending[key] = bounce;
                return;
            }
            if (WeaponSimulation.IsFloatingBubble(v.Shot.Configuration)) return;
            int at = v.Count;
            for (int i = 0; i < v.Count; i++)
            {
                if (v.Segments[i].Sequence == bounce.Sequence) return;
                if (v.Segments[i].Sequence > bounce.Sequence) { at = i; break; }
            }
            if (v.Count == v.Segments.Length || bounce.Sequence > 16) return;
            for (int i = v.Count; i > at; i--) v.Segments[i] = v.Segments[i - 1];
            v.Segments[at] = bounce; v.Count++;
            if (sound && at == v.Count - 1) Play(v.Shot.Configuration.Ammo.BubbleBounceAudio, bounce.Position, .09f, bounce.Sequence);
        }
        public void Complete(InkImpact impact, AmmoRuntimeConfig fallback = null)
        {
            var key = (impact.Round, impact.Id); _pending.Remove(key);
            if (!_active.Remove(key, out var v)) { Play(fallback?.BubblePopAudio, impact.Position, .17f, impact.Id); return; }
            Play(v.Shot.Configuration.Ammo.BubblePopAudio, impact.Position, .17f, impact.Id);
            if (Application.isPlaying)
                for (int i = 0; i < 4 && _fragments.Count < 96; i++)
                {
                    var fragment = Acquire(v.Shot);
                    float angle = (impact.Id % 17 + i * 4.25f) * Mathf.PI * 2 / 17;
                    var normal = impact.Normal.sqrMagnitude > .01f ? impact.Normal.normalized : Vector3.up;
                    var velocity = Quaternion.FromToRotation(Vector3.up, normal) * new Vector3(Mathf.Cos(angle), 1.3f, Mathf.Sin(angle));
                    fragment.Object.transform.position = impact.Position; fragment.Object.transform.localScale = Vector3.one * .09f;
                    _fragments.Add((fragment, Time.time, impact.Position, velocity));
                }
            Recycle(v);
        }
        public bool TryPosition(uint round, uint id, double time, out Vector3 position)
        {
            position = default;
            if (!_active.TryGetValue((round, id), out var v)) return false;
            var segment = Segment(v, time);
            position = segment.PositionAt(time, v.Shot); return true;
        }
        static InkBounce Segment(Visual v, double time)
        {
            var current = v.Segments[0];
            for (int i = 1; i < v.Count && v.Segments[i].Time <= time; i++) current = v.Segments[i];
            return current;
        }
        public void Update(double now)
        {
            for (int i = _fragments.Count - 1; i >= 0; i--)
            {
                var f = _fragments[i]; float age = Time.time - f.born;
                if (age >= .18f) { Recycle(f.visual); _fragments.RemoveAt(i); continue; }
                f.visual.Object.transform.position = f.origin + f.velocity * age + Vector3.down * (4 * age * age);
                f.visual.Object.transform.localScale = Vector3.one * (.09f * (1 - age / .18f));
            }
            _expired.Clear();
            foreach (var pair in _active)
            {
                var v = pair.Value; var w = v.Shot.Configuration;
                if (now > v.Shot.Born + w.Lifetime + RenderDelay) { _expired.Add(pair.Key); continue; }
                double time = Math.Max(v.Shot.Born, now - RenderDelay);
                var segment = Segment(v, time);
                v.Object.transform.position = segment.PositionAt(time, v.Shot);
                float squash = segment.Sequence == 0 ? 0 : Mathf.Clamp01(1 - (float)(time - segment.Time) / .12f);
                v.Object.transform.rotation = segment.Sequence == 0 ? Quaternion.identity : Quaternion.FromToRotation(Vector3.up, segment.Normal);
                float radius = WeaponSimulation.IsFloatingBubble(w) ? w.CollisionRadius : ReferenceBallistics.BubbleRadius(v.Shot, time-v.Shot.Born, (int)segment.Sequence, false);
                v.Object.transform.localScale = new Vector3(1 + .16f * squash, 1 - .27f * squash, 1 + .16f * squash) * (radius * 2);
            }
            foreach (var key in _expired) { var v = _active[key]; _active.Remove(key); Recycle(v); }
        }
        void Play(AudioClip clip, Vector3 position, float volume, uint seed)
        {
            if (clip == null || !Application.isPlaying) return;
            int i = _voice++ % _voices.Length;
            if (_voices[i] == null)
            {
                var go = new GameObject("Bubble contact audio"); go.transform.SetParent(_parent, false);
                var source = go.AddComponent<AudioSource>(); source.playOnAwake = false; source.spatialBlend = 1;
                source.rolloffMode = AudioRolloffMode.Linear; source.minDistance = 2; source.maxDistance = 24; _voices[i] = source;
            }
            var voice = _voices[i]; voice.transform.position = position; voice.pitch = .96f + seed % 5 * .02f;
            voice.clip = clip; voice.volume = volume; voice.Play();
        }
        void Recycle(Visual v) { v.Object.SetActive(false); v.Count = 0; _pool.Push(v); }
        public void Clear()
        {
            foreach (var v in _active.Values) Recycle(v);
            foreach (var f in _fragments) Recycle(f.visual);
            _fragments.Clear();
            _active.Clear(); _pending.Clear(); _expired.Clear(); Dropped = 0;
            foreach (var voice in _voices) if (voice != null) voice.Stop();
        }
        public void Dispose()
        {
            Clear(); while (_pool.Count > 0) HeroViewBinder.Destroy(_pool.Pop().Object);
            foreach (var voice in _voices) if (voice != null) HeroViewBinder.Destroy(voice.gameObject);
        }
    }
}
