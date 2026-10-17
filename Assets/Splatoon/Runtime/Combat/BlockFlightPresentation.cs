using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splatoon.Combat
{
    /// <summary>Bounded opaque cube instances. Server shot/contact events own every trajectory.</summary>
    public sealed class BlockFlightPresentation : IDisposable
    {
        public const int ShotLimit = 2048, DebrisLimit = 512, InstanceBatch = 1023;
        public const int Layer = 0;
        const int SeenLimit = 8192, SegmentLimit = 17;
        struct Visual { public InkShot Shot; public int Segments; }
        struct Fragment { public Vector3 Origin, Velocity, Spin; public Quaternion Rotation; public double Born; public float Lifetime, Size; public byte Team; }
        readonly Visual[] _shots = new Visual[ShotLimit];
        readonly InkBounce[] _segments = new InkBounce[ShotLimit * SegmentLimit];
        readonly Fragment[] _fragments = new Fragment[DebrisLimit];
        readonly Dictionary<(uint round, uint id), int> _active = new(ShotLimit);
        readonly Dictionary<(uint round, uint id), InkBounce> _pending = new(384);
        readonly HashSet<(uint round, uint id)> _seen = new(SeenLimit);
        readonly Queue<(uint round, uint id)> _seenOrder = new(SeenLimit);
        readonly Matrix4x4[][] _matrices = { new Matrix4x4[InstanceBatch], new Matrix4x4[InstanceBatch] };
        readonly int[] _counts = new int[2];
        readonly Material[] _materials = new Material[2];
        readonly AudioSource[] _voices = new AudioSource[12];
        readonly Transform _parent;
        readonly Mesh _cube;
        int _shotCount, _fragmentCount, _voice;
        uint _round;
        double _cutoff = double.NegativeInfinity;
        Camera _camera;
        bool _disposed;
        public int ActiveShots => _shotCount;
        public int DebrisCount => _fragmentCount;
        public int CubeCount { get; private set; }
        public int Dropped { get; private set; }
        public Mesh CubeMesh => _cube;

        public BlockFlightPresentation(Transform parent = null)
        {
            _parent = parent;
            _cube = CreateCube();
            // A serialized instancing material keeps the instanced shader variant in Players.
            var template = Resources.Load<Material>("BlockProjectileInstanced");
            if (template == null) throw new InvalidOperationException("Missing Resources/BlockProjectileInstanced material");
            for (int i = 0; i < _materials.Length; i++)
            {
                _materials[i] = new Material(template) { name = "Block projectile team " + (i + 1), enableInstancing = true };
                _materials[i].SetColor("_TeamColor", PrototypeArena.TeamColor((byte)(i + 1)));
            }
        }
        static Mesh CreateCube()
        {
            var vertices = new Vector3[24]; var normals = new Vector3[24]; var uv = new Vector2[24]; var triangles = new int[36];
            var faces = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int face = 0; face < faces.Length; face++)
            {
                var n = faces[face]; var u = Vector3.Cross(Mathf.Abs(n.y) > .5f ? Vector3.forward : Vector3.up, n); var v = Vector3.Cross(n, u);
                int start = face * 4;
                for (int corner = 0; corner < 4; corner++)
                {
                    var p = new Vector2(corner == 1 || corner == 2 ? 1 : 0, corner >= 2 ? 1 : 0);
                    vertices[start + corner] = n * .5f + u * (p.x - .5f) + v * (p.y - .5f); normals[start + corner] = n; uv[start + corner] = p;
                }
                int index = face * 6;
                triangles[index] = start; triangles[index + 1] = start + 1; triangles[index + 2] = start + 2;
                triangles[index + 3] = start; triangles[index + 4] = start + 2; triangles[index + 5] = start + 3;
            }
            return new Mesh { name = "Instanced block projectile cube", vertices = vertices, normals = normals, uv = uv, triangles = triangles, bounds = new Bounds(Vector3.zero, Vector3.one) };
        }
        void Remember((uint round, uint id) key)
        {
            if (!_seen.Add(key)) return;
            if (_seenOrder.Count >= SeenLimit) _seen.Remove(_seenOrder.Dequeue());
            _seenOrder.Enqueue(key);
        }
        public bool Spawn(InkShot shot, double now, bool restore = false)
        {
            var key = (shot.Round, shot.Id); var w = shot.Configuration;
            if (_disposed || shot.Round < _round || (!restore && shot.Born < _cutoff - .001) || _seen.Contains(key)) return false;
            bool hasPending = _pending.TryGetValue(key, out var pending);
            if (shot.Round > _round) { Clear(_cutoff); _round = shot.Round; }
            Remember(key);
            double delay = WeaponSimulation.UsesBubbleMesh(w) ? BubbleFlightPresentation.RenderDelay : 0;
            if (w == null || shot.Team < 1 || shot.Team > 2 || now >= shot.Born + w.Lifetime + delay) return false;
            if (_shotCount == ShotLimit) { Dropped++; return false; }
            int index = _shotCount++; _shots[index] = new Visual { Shot = shot, Segments = 1 }; _segments[index * SegmentLimit] = InkBounce.Initial(shot); _active.Add(key, index);
            _pending.Remove(key);
            if (hasPending) Bounce(pending);
            return true;
        }
        public void Bounce(InkBounce bounce)
        {
            var key = (bounce.Round, bounce.Id);
            if (_disposed || bounce.Round < _round || bounce.Time < _cutoff || _seen.Contains(key) && !_active.ContainsKey(key)) return;
            if (!_active.TryGetValue(key, out int index))
            {
                if (_pending.TryGetValue(key, out var old) && old.Sequence >= bounce.Sequence) return;
                if (_pending.Count < 384) _pending[key] = bounce;
                return;
            }
            var visual = _shots[index];
            if (!WeaponSimulation.UsesBubbleMesh(visual.Shot.Configuration) || WeaponSimulation.IsFloatingBubble(visual.Shot.Configuration) || bounce.Sequence > 16 || visual.Segments == SegmentLimit) return;
            int start = index * SegmentLimit, at = visual.Segments;
            for (int i = 0; i < visual.Segments; i++)
            {
                if (_segments[start + i].Sequence == bounce.Sequence) return;
                if (_segments[start + i].Sequence > bounce.Sequence) { at = i; break; }
            }
            for (int i = visual.Segments; i > at; i--) _segments[start + i] = _segments[start + i - 1];
            _segments[start + at] = bounce; visual.Segments++; _shots[index] = visual;
            if (at == visual.Segments - 1) Play(visual.Shot.Configuration.Ammo.BubbleBounceAudio, bounce.Position, .09f, bounce.Sequence);
        }
        public bool TryPosition(uint round, uint id, double time, out Vector3 position)
        {
            position = default;
            if (!_active.TryGetValue((round, id), out int index)) return false;
            position = Position(index, time, out _); return true;
        }
        Vector3 Position(int index, double time, out Vector3 velocity)
        {
            var visual = _shots[index]; var shot = visual.Shot;
            if (!WeaponSimulation.UsesBubbleMesh(shot.Configuration))
            {
                double age = Math.Max(0, time - shot.Born); velocity = InkBallistics.Velocity(shot, shot.Configuration, age); return InkBallistics.Position(shot, shot.Configuration, age);
            }
            var segment = _segments[index * SegmentLimit];
            for (int i = 1; i < visual.Segments && _segments[index * SegmentLimit + i].Time <= time; i++) segment = _segments[index * SegmentLimit + i];
            velocity = segment.VelocityAt(time, shot); return segment.PositionAt(time, shot);
        }
        public void Complete(InkImpact impact)
        {
            if (impact.ContinuesProjectile || _disposed) return;
            var key = (impact.Round, impact.Id); Remember(key); _pending.Remove(key);
            if (_active.TryGetValue(key, out int index))
            {
                var shot = _shots[index].Shot;
                if (WeaponSimulation.UsesBubbleMesh(shot.Configuration)) Play(shot.Configuration.Ammo.BubblePopAudio, impact.Position, .17f, impact.Id);
                Remove(index);
            }
        }
        void Remove(int index)
        {
            var key = (_shots[index].Shot.Round, _shots[index].Shot.Id); _active.Remove(key);
            int last = --_shotCount;
            if (index != last)
            {
                _shots[index] = _shots[last]; Array.Copy(_segments, last * SegmentLimit, _segments, index * SegmentLimit, SegmentLimit);
                _active[(_shots[index].Shot.Round, _shots[index].Shot.Id)] = index;
            }
            _shots[last] = default;
        }
        public void Impact(InkImpact impact)
        {
            if (impact.Hit) Scatter(impact.Position, impact.Normal, impact.Team, InkImpactEffect.Seed(impact), impact.Time, 8, .07f, 2.7f, .38f, false);
        }
        public void Explosion(InkExplosionEvent explosion, float radius)
        {
            Complete(new InkImpact { Round = explosion.Round, Id = explosion.ShotId, Position = explosion.Position, Normal = explosion.Normal, Time = explosion.Time });
            Burst(explosion.Position, explosion.Normal, explosion.Team, explosion.Seed, explosion.Time, radius);
        }
        public void Burst(Vector3 position, Vector3 normal, byte team, uint seed, double time, float radius)
        {
            int count = Mathf.Clamp(Mathf.CeilToInt(radius * 18), 16, 64);
            Scatter(position, normal, team, seed == 0 ? 1u : seed, time, count, .11f, Mathf.Clamp(radius * 3, 3, 9), .7f, true);
        }
        void Play(AudioClip clip, Vector3 position, float volume, uint seed)
        {
            if (clip == null || !Application.isPlaying) return;
            int at = _voice++ % _voices.Length;
            if (_voices[at] == null)
            {
                var go = new GameObject("Block projectile contact audio"); if (_parent != null) go.transform.SetParent(_parent, false);
                var source = go.AddComponent<AudioSource>(); source.playOnAwake = false; source.spatialBlend = 1;
                source.rolloffMode = AudioRolloffMode.Linear; source.minDistance = 2; source.maxDistance = 24; _voices[at] = source;
            }
            var voice = _voices[at]; voice.transform.position = position; voice.pitch = .96f + seed % 5 * .02f;
            voice.clip = clip; voice.volume = volume; voice.Play();
        }
        void Scatter(Vector3 origin, Vector3 normal, byte team, uint seed, double born, int count, float size, float speed, float lifetime, bool sphere)
        {
            if (_disposed || team < 1 || team > 2) return;
            normal = normal.sqrMagnitude > .01f ? normal.normalized : Vector3.up; var frame = Quaternion.FromToRotation(Vector3.up, normal);
            for (int i = 0; i < count; i++)
            {
                if (_fragmentCount == DebrisLimit) { Dropped += count - i; break; }
                float angle = (i + InkBallistics.Random01(ref seed)) * Mathf.PI * 2 / count;
                float y = sphere ? InkBallistics.Random01(ref seed) * 2 - 1 : .18f + InkBallistics.Random01(ref seed) * .82f;
                float horizontal = Mathf.Sqrt(Mathf.Max(0, 1 - y * y)); var direction = frame * new Vector3(Mathf.Cos(angle) * horizontal, y, Mathf.Sin(angle) * horizontal);
                var spin = new Vector3(InkBallistics.Random01(ref seed), InkBallistics.Random01(ref seed), InkBallistics.Random01(ref seed)) * 540;
                _fragments[_fragmentCount++] = new Fragment { Origin = origin + normal * .025f, Velocity = direction * (speed * Mathf.Lerp(.45f, 1, InkBallistics.Random01(ref seed))),
                    Rotation = Quaternion.Euler(spin), Spin = spin, Born = born, Lifetime = lifetime * Mathf.Lerp(.7f, 1, InkBallistics.Random01(ref seed)), Size = size * Mathf.Lerp(.5f, 1, InkBallistics.Random01(ref seed)), Team = team };
            }
        }
        public static int CubesPerShot(WeaponRuntimeConfig weapon) => WeaponSimulation.IsExplosher(weapon) ? 4 : WeaponSimulation.IsCharge(weapon) ? 3 : 1;
        public static float ShotSize(InkShot shot)
        {
            var w = shot.Configuration;
            if (WeaponSimulation.UsesBubbleMesh(w)) return Mathf.Clamp(w.CollisionRadius * 2, .12f, .45f);
            if (WeaponSimulation.IsBlaster(w)) return .16f;
            if (WeaponSimulation.IsExplosher(w)) return .11f;
            if (WeaponSimulation.IsCharge(w)) return Mathf.Lerp(.065f, .11f, Mathf.Clamp01(shot.Charge));
            return w.PelletCount > 1 ? .065f : .09f;
        }
        static Vector3 ShotScale(InkShot shot, int cubes)
        {
            var size = shot.Configuration.Ammo?.BlockProjectileSize ?? Vector3.zero;
            if (size.Equals(Vector3.zero)) return Vector3.one * ShotSize(shot);
            // Sizes describe the whole group in its trajectory frame, including the existing gaps.
            if (cubes == 4) return new Vector3(size.x / 2.05f, size.y / 2.05f, size.z);
            return new Vector3(size.x, size.y, size.z / (1 + (cubes - 1) * 1.08f));
        }
        public void Update(double now, Camera camera)
        {
            if (_disposed) return;
            using var marker = FramePerformance.Flight.Auto();
            _camera = camera; _counts[0] = _counts[1] = 0; CubeCount = 0;
            for (int i = _shotCount - 1; i >= 0; i--)
            {
                var shot = _shots[i].Shot; var w = shot.Configuration;
                double delay = WeaponSimulation.UsesBubbleMesh(w) ? BubbleFlightPresentation.RenderDelay : 0;
                if (now >= shot.Born + w.Lifetime + delay) { Remove(i); continue; }
                if (now < shot.Born) continue;
                double time = Math.Max(shot.Born, now - delay); var position = Position(i, time, out var velocity);
                var forward = velocity.sqrMagnitude > .0001f ? velocity.normalized : Vector3.forward;
                var rotation = Quaternion.LookRotation(forward, Mathf.Abs(forward.y) > .95f ? Vector3.forward : Vector3.up);
                if (WeaponSimulation.IsExplosher(w) || WeaponSimulation.UsesBubbleMesh(w)) rotation *= Quaternion.Euler((float)(time - shot.Born) * 100, 0, shot.Seed % 90);
                int cubes = CubesPerShot(w); var scale = ShotScale(shot, cubes);
                for (int cube = 0; cube < cubes; cube++)
                {
                    var offset = cubes == 4 ? new Vector3((cube % 2 - .5f) * scale.x * 1.05f, (cube / 2 - .5f) * scale.y * 1.05f, 0) : new Vector3(0, 0, (cube - (cubes - 1) * .5f) * scale.z * 1.08f);
                    Add(shot.Team, position + rotation * offset, rotation, scale);
                }
            }
            for (int i = _fragmentCount - 1; i >= 0; i--)
            {
                var fragment = _fragments[i]; float age = (float)(now - fragment.Born);
                if (age >= fragment.Lifetime) { _fragments[i] = _fragments[--_fragmentCount]; _fragments[_fragmentCount] = default; continue; }
                if (age < 0) continue;
                var position = InkBallistics.Position(fragment.Origin, fragment.Velocity, 9.81f, age);
                float shrink = Mathf.Clamp01((fragment.Lifetime - age) / .1f);
                Add(fragment.Team, position, fragment.Rotation * Quaternion.Euler(fragment.Spin * age), Vector3.one * (fragment.Size * shrink));
            }
            Flush(0); Flush(1);
        }
        void Add(byte team, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            int at = team - 1; _matrices[at][_counts[at]++] = Matrix4x4.TRS(position, rotation, scale); CubeCount++;
            if (_counts[at] == InstanceBatch) Flush(at);
        }
        void Flush(int team)
        {
            int count = _counts[team]; if (count == 0) return;
            if (_camera != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                if (SystemInfo.supportsInstancing) Graphics.DrawMeshInstanced(_cube, 0, _materials[team], _matrices[team], count, null, ShadowCastingMode.Off, false, Layer, _camera, LightProbeUsage.Off);
                else for (int i = 0; i < Math.Min(64, count); i++) Graphics.DrawMesh(_cube, _matrices[team][i], _materials[team], Layer, _camera, 0, null, false, false, false);
            }
            _counts[team] = 0;
        }
        public void Clear(double cutoff = double.NegativeInfinity)
        {
            Array.Clear(_shots, 0, _shotCount); Array.Clear(_fragments, 0, _fragmentCount); _shotCount = _fragmentCount = CubeCount = Dropped = 0;
            _active.Clear(); _pending.Clear(); _seen.Clear(); _seenOrder.Clear(); _cutoff = cutoff;
            foreach (var voice in _voices) if (voice != null) voice.Stop();
        }
        public void Dispose()
        {
            if (_disposed) return; Clear(); _disposed = true;
            HeroViewBinder.Destroy(_cube); foreach (var material in _materials) HeroViewBinder.Destroy(material);
            foreach (var voice in _voices) if (voice != null) HeroViewBinder.Destroy(voice.gameObject);
        }
    }
}
