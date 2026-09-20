using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed class SubWeaponHitProxy : MonoBehaviour, ISubWeaponDamageable
    {
        public uint EntityId { get; set; }
        public byte Team { get; set; }
        public SubWeaponKind Kind { get; set; }
        public bool ReceiveSubWeaponDamage(byte attackerTeam, float damage) => PrototypeMatch.Current != null && PrototypeMatch.Current.IsServer && PrototypeMatch.Current.SubWeapons.Damage(EntityId, attackerTeam, damage);
    }

    public sealed class SubWeaponPresentation : MonoBehaviour
    {
        sealed class View { public GameObject Root; public Renderer Renderer; public SubWeaponHitProxy Proxy; public Vector3 TargetPosition; public Quaternion TargetRotation; public SubWeaponKind Kind; }
        public static SubWeaponPresentation Current { get; private set; }
        readonly Dictionary<uint, View> _active = new();
        readonly Dictionary<SubWeaponKind, Queue<GameObject>> _pool = new();
        readonly HashSet<uint> _seen = new();
        readonly List<uint> _remove = new(16);
        readonly MaterialPropertyBlock _properties = new();
        Material _pink, _blue;

        public static SubWeaponPresentation Ensure()
        {
            if (Current != null) return Current;
            var root = new GameObject("Sub weapon presentation");
            return root.AddComponent<SubWeaponPresentation>();
        }

        void Awake() { if (Current != null && Current != this) { Destroy(gameObject); return; } Current = this; }

        public void Apply(NetworkBatch<SubWeaponEntityState> states)
        {
            _seen.Clear();
            for (int i = 0; i < states.Count; i++) Apply(states[i]);
            _remove.Clear();
            foreach (var pair in _active) if (!_seen.Contains(pair.Key)) _remove.Add(pair.Key);
            for (int i = 0; i < _remove.Count; i++) Recycle(_remove[i]);
        }

        public void Apply(IReadOnlyList<SubWeaponEntityState> states)
        {
            _seen.Clear();
            for (int i = 0; i < states.Count; i++) Apply(states[i]);
            _remove.Clear();
            foreach (var pair in _active) if (!_seen.Contains(pair.Key)) _remove.Add(pair.Key);
            for (int i = 0; i < _remove.Count; i++) Recycle(_remove[i]);
        }

        void Apply(SubWeaponEntityState state)
        {
            _seen.Add(state.Id);
            if (!_active.TryGetValue(state.Id, out var view))
            {
                view = Create(state.Kind); _active.Add(state.Id, view);
                Vector3 initialPosition = state.Kind == SubWeaponKind.InkCurtain ? state.Position + state.Normal * state.Scale.y * .5f : state.Position;
                view.Root.transform.SetPositionAndRotation(initialPosition, state.Rotation);
            }
            view.TargetPosition = state.Kind == SubWeaponKind.InkCurtain ? state.Position + state.Normal * state.Scale.y * .5f : state.Position;
            view.TargetRotation = state.Rotation;
            view.Root.transform.localScale = state.Scale;
            view.Proxy.EntityId = state.Id; view.Proxy.Team = state.Team; view.Proxy.Kind = state.Kind;
            var renderer = view.Renderer;
            if (renderer != null) renderer.sharedMaterial = TeamMaterial(state.Team);
            float alpha = state.Kind == SubWeaponKind.InkMine && state.Phase == SubWeaponEntityPhase.Arming ? .28f : .82f;
            if (renderer != null)
            {
                Color color = PrototypeArena.TeamColor(state.Team);
                _properties.Clear(); _properties.SetColor("_BaseColor", new Color(color.r, color.g, color.b, alpha));
                renderer.SetPropertyBlock(_properties);
            }
        }

        View Create(SubWeaponKind kind)
        {
            if (!_pool.TryGetValue(kind, out var queue)) _pool[kind] = queue = new Queue<GameObject>();
            GameObject root = queue.Count > 0 ? queue.Dequeue() : Build(kind);
            root.SetActive(true);
            return new View { Root = root, Renderer = root.GetComponentInChildren<Renderer>(), Proxy = root.GetComponent<SubWeaponHitProxy>(), Kind = kind };
        }

        GameObject Build(SubWeaponKind kind)
        {
            PrimitiveType primitive = kind is SubWeaponKind.Torpedo or SubWeaponKind.CurlingBomb ? PrimitiveType.Capsule : PrimitiveType.Cube;
            var root = GameObject.CreatePrimitive(primitive); root.name = "Sub weapon " + kind; root.transform.SetParent(transform, false); root.layer = 8;
            var collider = root.GetComponent<Collider>(); collider.isTrigger = true;
            var proxy = root.AddComponent<SubWeaponHitProxy>();
            return root;
        }

        Material TeamMaterial(byte team)
        {
            var value = team == 1 ? _pink : _blue;
            if (value != null) return value;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            value = new Material(shader) { name = team == 1 ? "Secondary pink" : "Secondary blue", color = PrototypeArena.TeamColor(team) };
            if (team == 1) _pink = value; else _blue = value;
            return value;
        }

        void Update()
        {
            float t = 1 - Mathf.Exp(-18 * Time.deltaTime);
            foreach (var view in _active.Values)
            {
                view.Root.transform.position = Vector3.Lerp(view.Root.transform.position, view.TargetPosition, t);
                view.Root.transform.rotation = Quaternion.Slerp(view.Root.transform.rotation, view.TargetRotation, t);
            }
        }

        public void Play(SubWeaponEvent e)
        {
            if (e.Type is not (SubWeaponEventType.Triggered or SubWeaponEventType.Exploded or SubWeaponEventType.Destroyed)) return;
            InkPresentation.Current?.Impact(new InkImpact { Id = e.Id | 0x80000000u, Round = e.Round, Time = e.Time,
                Team = e.Team, Position = e.Position, Normal = e.Normal.sqrMagnitude > .01f ? e.Normal : Vector3.up, Hit = true });
        }

        void Recycle(uint id)
        {
            if (!_active.TryGetValue(id, out var view)) return;
            _active.Remove(id); view.Root.SetActive(false);
            if (!_pool.TryGetValue(view.Kind, out var queue)) _pool[view.Kind] = queue = new Queue<GameObject>();
            if (queue.Count < 16) queue.Enqueue(view.Root); else Destroy(view.Root);
        }

        public void Clear()
        { _remove.Clear(); foreach (uint id in _active.Keys) _remove.Add(id); for (int i = 0; i < _remove.Count; i++) Recycle(_remove[i]); }
        void OnDestroy()
        { if (_pink != null) Destroy(_pink); if (_blue != null) Destroy(_blue); if (Current == this) Current = null; }
    }
}
