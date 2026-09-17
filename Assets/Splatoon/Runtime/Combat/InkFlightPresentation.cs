using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    /// <summary>Cosmetic samples of authoritative shots. No physics, paint or damage callbacks.</summary>
    public sealed class InkFlightPresentation : IDisposable
    {
        // Eight shotguns can retain three releases of eight independent pellet lanes each.
        public const int GroupLimit = 256, ParticlesPerGroup = 128, ShotLimit = 2048;
        const int SeenLimit = 8192;
        readonly AmmoRuntimeConfig _ammo;
        public const int Layer = 11;
        readonly Group[] _groups = new Group[GroupLimit];
        readonly ShotVisual[] _shots = new ShotVisual[ShotLimit];
        readonly Dictionary<GroupKey, int> _groupIds = new(GroupLimit);
        readonly HashSet<(uint round, uint id)> _seen = new(SeenLimit);
        readonly Queue<(uint round, uint id)> _seenOrder = new(SeenLimit);
        int _shotCount;
        readonly System.Collections.Generic.List<int> _activeGroups = new(GroupLimit);
        struct SampleInvariant { public uint Seed; public Vector3 Velocity, PostVelocity; public float Side, Up; }
        readonly SampleInvariant[] _sampleCache = new SampleInvariant[ShotLimit * 16];
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RibbonVertex { public Vector3 Position, Normal; public Color Color; public Vector2 UV; }
        static readonly VertexAttributeDescriptor[] RibbonLayout = {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2) };

        uint _round;
        double _clearedAt = double.NegativeInfinity;
        public int ParticleCount { get; private set; }
        public int ActiveGroups => _groupIds.Count;
        public int DroppedSamples { get; private set; }
        public int ActiveShots => _shotCount;
        public IReadOnlyList<ParticleSystem> Streams => _streams;
        readonly ParticleSystem[] _streams = new ParticleSystem[GroupLimit];

        readonly struct GroupKey : IEquatable<GroupKey>
        {
            readonly ulong shooter, action;
            readonly uint life, hero, config;
            readonly byte muzzle, pellet, team;
            public GroupKey(InkShot s, bool continuous)
            { shooter = s.Shooter; life = s.Lifecycle; hero = s.HeroRevision; config = s.ConfigurationRevision;
                muzzle = s.MuzzleIndex; pellet = s.PelletIndex; team = s.Team;
                action = continuous ? 0 : s.ActionId == 0 ? s.Id : s.ActionId; }
            public bool Equals(GroupKey o) => shooter == o.shooter && action == o.action && life == o.life && hero == o.hero && config == o.config && muzzle == o.muzzle && pellet == o.pellet && team == o.team;
            public override bool Equals(object o) => o is GroupKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(shooter, action, life, hero, config, muzzle, pellet, team);
        }
        struct ShotVisual
        {
            public InkShot Shot;
            public int Group, Count;
            public float Span, Lifetime;
            public uint Segment;
        }
        sealed class Group
        {
            public GroupKey Key;
            public readonly ParticleSystem System;
            public readonly ParticleSystem.Particle[] Particles = new ParticleSystem.Particle[ParticlesPerGroup];
            public readonly double[] Births = new double[ParticlesPerGroup];
            public readonly uint[] Segments = new uint[ParticlesPerGroup];
            public int Count;
            public bool HasShots, InUse;
            public byte Team;
            public double LastBorn = double.NegativeInfinity;
            public float DensityRemainder;
            public uint Segment;
            public readonly Mesh Ribbon;
            public bool RibbonInitialized;
            public readonly MeshRenderer RibbonRenderer;
            public readonly RibbonVertex[] Vertices = new RibbonVertex[ParticlesPerGroup * 2];
            public readonly int[] Indices = new int[(ParticlesPerGroup - 1) * 6];
            public readonly ParticleSystem.MinMaxCurve Width;
            public readonly ParticleSystem.MinMaxGradient TrailColor;
            public readonly float StartSize, SpeedMin, SpeedMax, ConeAngle;
            public Group(ParticleSystem prefab, Transform parent)
            {
                System = UnityEngine.Object.Instantiate(prefab, parent);
                System.name = "Ink flight lane"; System.gameObject.layer = Layer;
                System.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                System.transform.localScale = Vector3.one;
                System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = System.main; main.simulationSpace = ParticleSystemSimulationSpace.World;
                StartSize = main.startSize.constant;
                SpeedMin = main.startSpeed.constantMin; SpeedMax = main.startSpeed.constantMax;
                ConeAngle = System.shape.angle;
                main.maxParticles = ParticlesPerGroup; main.simulationSpeed = 0; main.gravityModifier = 0;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate; main.playOnAwake = false;
                var emission = System.emission; emission.enabled = false;
                var collision = System.collision; collision.enabled = false;
                var sub = System.subEmitters; sub.enabled = false;
                var rotation = System.rotationOverLifetime; rotation.enabled = false;
                var trails = System.trails; Width = trails.widthOverTrail; TrailColor = trails.colorOverLifetime;
                // Ribbon mode joins live particles, not a historical trail per particle. Build explicitly
                // so removing a shot cannot transfer native trail history to another particle index.
                trails.enabled = false;
                var renderer = System.GetComponent<ParticleSystemRenderer>();
                renderer.alignment = ParticleSystemRenderSpace.Velocity;
                var go = new GameObject("Ink flight ribbon"); go.layer = Layer;
                go.transform.SetParent(System.transform, false);
                Ribbon = new Mesh { name = "Pooled ink ribbon" }; Ribbon.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = Ribbon;
                RibbonRenderer = go.AddComponent<MeshRenderer>();
                var mats = renderer.sharedMaterials;
                RibbonRenderer.sharedMaterial = mats.Length > 1 ? mats[1] : mats[0];
                RibbonRenderer.shadowCastingMode = ShadowCastingMode.Off;
                System.gameObject.SetActive(false);
            }
            public void Stop()
            { System.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); RibbonRenderer.enabled = false;
                Ribbon.SetIndices(Indices, 0, 0, MeshTopology.Triangles, 0, false);
                System.gameObject.SetActive(false); InUse = false; Count = 0; }
        }
        public InkFlightPresentation(Transform parent, AmmoRuntimeConfig ammo)
        {
            _ammo = ammo ?? throw new ArgumentNullException(nameof(ammo));
            for (int i = 0; i < GroupLimit; i++) { _groups[i] = new Group(ammo.FlightPrefab, parent); _streams[i] = _groups[i].System; }
        }
        public static bool IsContinuous(WeaponRuntimeConfig w) => w.PelletCount == 1 &&
            w.FireMode != WeaponFireMode.SemiAutomatic && w.FireMode != WeaponFireMode.Charge;
        float Lifetime => _ammo.VisualLifetime;
        float Rate => _ammo.ParticlesPerBurst / _ammo.BurstInterval;
        static uint Hash(uint s) { s ^= s >> 16; s *= 0x7feb352du; s ^= s >> 15; s *= 0x846ca68bu; s ^= s >> 16; return s == 0 ? 1u : s; }
        void Remember((uint round, uint id) key)
        {
            if (!_seen.Add(key)) return;
            if (_seenOrder.Count >= SeenLimit) _seen.Remove(_seenOrder.Dequeue());
            _seenOrder.Enqueue(key);
        }
        public bool Spawn(InkShot shot, double now)
        {
            var id = (shot.Round, shot.Id);
            if (shot.Round < _round || shot.Born < _clearedAt - .001 || _seen.Contains(id)) return false;
            if (shot.Round > _round) { Clear(); _round = shot.Round; }
            Remember(id);
            var w = shot.Configuration;
            if (w == null || shot.Team < 1 || shot.Team > 2 || now - shot.Born >= Math.Min(Lifetime, w.Lifetime) || _shotCount == ShotLimit) return false;
            bool continuous = IsContinuous(w);
            var key = new GroupKey(shot, continuous);
            if (!_groupIds.TryGetValue(key, out int index))
            {
                index = -1;
                for (int i = 0; i < GroupLimit; i++) if (!_groups[i].InUse) { index = i; break; }
                if (index < 0) { DroppedSamples++; return false; }
                var free = _groups[index]; free.Key = key; free.InUse = true; free.LastBorn = double.NegativeInfinity;
                free.DensityRemainder = 0; free.Segment = 0; free.Team = shot.Team;
                free.System.gameObject.SetActive(true); free.System.Play(false);
                _groupIds.Add(key, index);
                int at = _activeGroups.BinarySearch(index); _activeGroups.Insert(~at, index);
            }
            var group = _groups[index];
            double interval = 1.0 / Math.Max(1, w.FireRate);
            if (shot.Born - group.LastBorn > Math.Max(.1, interval * 1.75)) { group.Segment++; group.DensityRemainder = 0; }
            group.LastBorn = Math.Max(group.LastBorn, shot.Born);
            float wanted = continuous ? Rate / Mathf.Max(1, w.FireRate) + group.DensityRemainder : _ammo.ParticlesPerBurst;
            int count = Mathf.Clamp(Mathf.FloorToInt(wanted), 1, 16);
            group.DensityRemainder = continuous ? wanted - Mathf.Floor(wanted) : 0;
            _shots[_shotCount] = new ShotVisual { Shot = shot, Group = index, Count = count, Segment = group.Segment,
                Lifetime = Mathf.Min(Lifetime, w.Lifetime), Span = continuous ? Mathf.Min((float)interval, .12f) : .03f };
            PrepareSamples(_shotCount, count, shot, group); _shotCount++;
            return true;
        }
        public void Complete(InkImpact impact)
        {
            Remember((impact.Round, impact.Id));
            for (int i = _shotCount - 1; i >= 0; i--) if (_shots[i].Shot.Id == impact.Id && _shots[i].Shot.Round == impact.Round) Remove(i);
        }
        void Remove(int i)
        {
            _shots[i] = _shots[--_shotCount]; _shots[_shotCount] = default;
            if (i != _shotCount) Array.Copy(_sampleCache, _shotCount * 16, _sampleCache, i * 16, 16);
        }
        void PrepareSamples(int index, int count, InkShot shot, Group group)
        {
            for (int n = 0; n < count; n++)
            {
                uint seed = Hash(shot.Seed ^ ((uint)n * 0x9e3779b9u)), random = seed;
                var sample = new SampleInvariant { Seed = seed, Velocity = shot.Velocity, PostVelocity = shot.PostCorrectionVelocity };
                if (n != 0)
                {
                    float speedRatio = Mathf.Lerp(group.SpeedMin, group.SpeedMax, InkBallistics.Random01(ref random)) / Mathf.Max(.001f, (group.SpeedMin + group.SpeedMax) * .5f);
                    float angle = InkBallistics.Random01(ref random) * Mathf.PI * 2;
                    float cone = Mathf.Sqrt(InkBallistics.Random01(ref random)) * group.ConeAngle;
                    var perturbation = Quaternion.Euler(Mathf.Sin(angle) * cone, Mathf.Cos(angle) * cone, 0);
                    sample.Velocity = Quaternion.FromToRotation(Vector3.forward, shot.Velocity.normalized) * perturbation * Vector3.forward * (shot.Velocity.magnitude * speedRatio);
                    if (shot.PostCorrectionVelocity.sqrMagnitude > 0)
                        sample.PostVelocity = Quaternion.FromToRotation(Vector3.forward, shot.PostCorrectionVelocity.normalized) * perturbation * Vector3.forward * (shot.PostCorrectionVelocity.magnitude * speedRatio);
                    sample.Side = InkBallistics.Random01(ref random) * 2 - 1;
                    sample.Up = InkBallistics.Random01(ref random) * 2 - 1;
                }
                _sampleCache[index * 16 + n] = sample;
            }
        }
        public void Update(double now, Camera camera)
        {
            using var marker = FramePerformance.Flight.Auto();
            ParticleCount = 0;
            foreach (int i in _activeGroups) { _groups[i].Count = 0; _groups[i].HasShots = false; }
            for (int i = _shotCount - 1; i >= 0; i--)
            {
                var visual = _shots[i]; var shot = visual.Shot;
                double age = now - shot.Born;
                if (age >= visual.Lifetime + visual.Span) { Remove(i); continue; }
                var group = _groups[visual.Group]; group.HasShots = true;
                for (int n = 0; n < visual.Count; n++)
                {
                    // The source emits pairs at the same instant. Staggering every individual
                    // particle makes an evenly spaced chain instead of merging and separating blobs.
                    double lag = (n / 2) * visual.Span / ((visual.Count + 1) / 2), t = age - lag;
                    if (t < 0 || t >= visual.Lifetime) continue;
                    if (group.Count == ParticlesPerGroup) { DroppedSamples++; break; }
                    var sample = _sampleCache[i * 16 + n];
                    var satellite = shot; satellite.Velocity = sample.Velocity; satellite.PostCorrectionVelocity = sample.PostVelocity;
                    Vector3 position = InkBallistics.Position(satellite, shot.Configuration, t);
                    Vector3 velocity = InkBallistics.Velocity(satellite, shot.Configuration, t);
                    if (n != 0)
                    {
                        Vector3 forward = velocity.sqrMagnitude > .0001f ? velocity.normalized : Vector3.forward;
                        Vector3 right = Vector3.Cross(Mathf.Abs(forward.y) > .95f ? Vector3.forward : Vector3.up, forward).normalized;
                        float spread = _ammo.SatelliteSpread * Mathf.Min(1, (float)t * 8);
                        position += (right * sample.Side + Vector3.Cross(forward, right) * sample.Up) * spread;
                    }
                    var p = new ParticleSystem.Particle { position = position, velocity = velocity,
                        startColor = PrototypeArena.TeamColor(shot.Team), startSize = group.StartSize,
                        startLifetime = visual.Lifetime, remainingLifetime = visual.Lifetime - (float)t,
                        rotation3D = new Vector3((float)t * 15, 0, 0), randomSeed = sample.Seed };
                    // Stable birth order is also the reference ribbon order.
                    int at = group.Count++;
                    double birth = shot.Born + lag;
                    while (at > 0 && group.Births[at - 1] > birth)
                    { group.Particles[at] = group.Particles[at - 1]; group.Births[at] = group.Births[at - 1]; group.Segments[at] = group.Segments[at - 1]; at--; }
                    group.Particles[at] = p; group.Births[at] = birth; group.Segments[at] = visual.Segment;
                }
            }
            for (int active = _activeGroups.Count - 1; active >= 0; active--)
            {
                var g = _groups[_activeGroups[active]];
                if (!g.HasShots) { _groupIds.Remove(g.Key); g.Stop(); _activeGroups.RemoveAt(active); continue; }
                g.System.SetParticles(g.Particles, g.Count);
                BuildRibbon(g, camera);
                ParticleCount += g.Count;
            }
        }
        void BuildRibbon(Group g, Camera camera)
        {
            if (g.Count < 2 || camera == null) { g.RibbonRenderer.enabled = false; return; }
            if (!g.RibbonInitialized)
            {
                g.Ribbon.SetVertexBufferParams(ParticlesPerGroup * 2, RibbonLayout);
                g.RibbonInitialized = true;
            }
            int indices = 0;
            Vector3 min = g.Particles[0].position, max = min;
            for (int i = 0; i < g.Count; i++)
            {
                var p = g.Particles[i];
                var view = (camera.transform.position - p.position).normalized;
                Vector3 tangent = i + 1 < g.Count ? g.Particles[i + 1].position - p.position : p.position - g.Particles[i - 1].position;
                Vector3 side = Vector3.Cross(tangent, view).normalized;
                if (side.sqrMagnitude < .01f) side = camera.transform.right;
                float along = i / (float)(g.Count - 1);
                float width = p.GetCurrentSize3D(g.System).x * g.Width.Evaluate(along, .5f) * .5f;
                var color = (Color)p.startColor * g.TrailColor.Evaluate(1 - p.remainingLifetime / p.startLifetime);
                for (int edge = 0; edge < 2; edge++)
                {
                    int v = i * 2 + edge;
                    var position = p.position + side * (edge == 0 ? -width : width);
                    g.Vertices[v] = new RibbonVertex { Position = position, Normal = view, UV = new Vector2(along, edge), Color = color };
                    min = Vector3.Min(min, position); max = Vector3.Max(max, position);
                }
                if (i == 0 || g.Segments[i] != g.Segments[i - 1] || Vector3.Distance(p.position, g.Particles[i - 1].position) > _ammo.MaxRibbonGap) continue;
                int a = (i - 1) * 2;
                g.Indices[indices++] = a; g.Indices[indices++] = a + 1; g.Indices[indices++] = a + 2;
                g.Indices[indices++] = a + 2; g.Indices[indices++] = a + 1; g.Indices[indices++] = a + 3;
            }
            // Keep vertex capacity stable when an impact removes particles. Shrinking vertices
            // while last frame's triangles still exist triggers native mesh bounds errors.
            g.Ribbon.SetVertexBufferData(g.Vertices, 0, 0, g.Count * 2, 0, MeshUpdateFlags.DontRecalculateBounds);
            g.Ribbon.SetIndices(g.Indices, 0, indices, MeshTopology.Triangles, 0, false);
            g.Ribbon.bounds = new Bounds((min + max) * .5f, max - min + Vector3.one * .01f);
            g.RibbonRenderer.enabled = indices > 0;
        }
        public int CopyParticles(byte team, ParticleSystem.Particle[] destination, int offset = 0)
        {
            int count = offset;
            foreach (var g in _groups) if (g.InUse && g.Team == team)
                for (int i = 0; i < g.Count && count < destination.Length; i++)
                    destination[count++] = g.Particles[i];
            return count - offset;
        }
        public void Clear(double cutoff = double.NegativeInfinity)
        {
            Array.Clear(_shots, 0, _shotCount); _shotCount = 0; ParticleCount = 0; _clearedAt = cutoff;
            _groupIds.Clear(); _seen.Clear(); _seenOrder.Clear();
            foreach (int index in _activeGroups) _groups[index].Stop();
            _activeGroups.Clear();
        }
        public void Dispose()
        {
            Clear();
            foreach (var group in _groups)
            {
                if (Application.isPlaying) { UnityEngine.Object.Destroy(group.Ribbon); UnityEngine.Object.Destroy(group.System.gameObject); }
                else { UnityEngine.Object.DestroyImmediate(group.Ribbon); UnityEngine.Object.DestroyImmediate(group.System.gameObject); }
            }
        }
    }
}
