using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Every rented instance starts at its authored pose with empty particle/trail history.</summary>
    public sealed class SubWeaponVisualPool : IDisposable
    {
        sealed class Item
        {
            public GameObject Source, Object;
            public Transform[] Parts;
            public Vector3[] Positions, Scales;
            public Quaternion[] Rotations;
            public bool[] Active;
            public Renderer[] Renderers;
            public Material[][] Materials;
        }
        readonly Dictionary<GameObject, Stack<Item>> _free = new();
        readonly Dictionary<GameObject, Item> _all = new();
        readonly Transform _parent;
        bool _shuttingDown;
        public void BeginShutdown() => _shuttingDown = true;
        public SubWeaponVisualPool(Transform parent) => _parent = parent;
        public GameObject Rent(GameObject source, Transform parent)
        {
            if (source == null) return null;
            Item item;
            if (_free.TryGetValue(source, out var stack) && stack.Count > 0) item = stack.Pop();
            else
            {
                var go = UnityEngine.Object.Instantiate(source, _parent); go.SetActive(false);
                var parts = go.GetComponentsInChildren<Transform>(true);
                item = new Item { Source = source, Object = go, Parts = parts, Positions = new Vector3[parts.Length], Rotations = new Quaternion[parts.Length], Scales = new Vector3[parts.Length], Active = new bool[parts.Length] };
                for (int i = 0; i < parts.Length; i++) { item.Positions[i] = parts[i].localPosition; item.Rotations[i] = parts[i].localRotation; item.Scales[i] = parts[i].localScale; item.Active[i] = parts[i].gameObject.activeSelf; }
                item.Renderers = go.GetComponentsInChildren<Renderer>(true); item.Materials = new Material[item.Renderers.Length][];
                for (int i=0;i<item.Renderers.Length;i++) item.Materials[i]=item.Renderers[i].sharedMaterials;
                _all.Add(go, item);
            }
            item.Object.SetActive(false); item.Object.transform.SetParent(parent, false);
            for (int i = 1; i < item.Parts.Length; i++)
            { var t = item.Parts[i]; t.SetLocalPositionAndRotation(item.Positions[i], item.Rotations[i]); t.localScale = item.Scales[i]; t.gameObject.SetActive(item.Active[i]); }
            item.Object.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity); item.Object.transform.localScale = Vector3.one;
            ResetEffects(item.Object);
            for (int i=0;i<item.Renderers.Length;i++) { var renderer=item.Renderers[i]; renderer.sharedMaterials=item.Materials[i]; renderer.SetPropertyBlock(null); renderer.enabled=true; }
            item.Object.SetActive(true); return item.Object;
        }
        public void Return(GameObject go)
        {
            if (_shuttingDown || go == null || !_all.TryGetValue(go, out var item)) return;
            go.SetActive(false); ResetEffects(go); go.transform.SetParent(_parent, false);
            go.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity); go.transform.localScale = Vector3.one;
            if (!_free.TryGetValue(item.Source, out var stack)) _free[item.Source] = stack = new Stack<Item>();
            stack.Push(item);
        }
        static void ResetEffects(GameObject go)
        {
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (var trail in go.GetComponentsInChildren<TrailRenderer>(true)) { trail.emitting = false; trail.Clear(); }
        }
        public void Dispose()
        { _shuttingDown=true; foreach (var item in _all.Values) if (item.Object != null) Destroy(item.Object); _all.Clear(); _free.Clear(); }
        public static void Destroy(UnityEngine.Object obj) { if (obj == null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj); }
    }

    public sealed class SubWeaponVisual
    {
        public readonly GameObject Root;
        public readonly SubWeaponRuntimeConfig Config;
        public GameObject Model { get; private set; }
        public LineRenderer Trail { get; private set; }
        readonly List<(Vector3 point, double at)> _trail = new(32);
        public bool Visible { get; private set; }
        public bool PersistentVisible => _loop != null && _loop.activeInHierarchy;
        readonly SubWeaponVisualPool _pool;
        GameObject _source, _loop, _warning;
        Transform _rotor;
        LineRenderer _range, _fuse, _line;
        readonly MaterialPropertyBlock _properties = new();
        readonly List<ParticleSystem> _particles = new();
        bool _isGhost, _heldMode;
        SubEntityPhase _phase = (SubEntityPhase)255;
        public SubWeaponVisual(SubWeaponRuntimeConfig config, SubWeaponVisualPool pool, Transform parent, SubEntityState state, bool ghost = false, bool held = false)
        {
            Config = config; _pool = pool; _isGhost = ghost; _heldMode = held;
            Root = new GameObject(ghost ? "Sub placement preview" : "Sub visual " + state.Id);
            Root.transform.SetParent(parent, false); Root.transform.SetPositionAndRotation(state.Position, SubWeaponMotion.Rotation(state));
            if (!ghost && !held && state.Type != SubWeaponType.InkMine && state.Phase != SubEntityPhase.Line)
            {
                Trail = Line(Root.transform, config.Visuals.trailMaterial != null ? config.Visuals.trailMaterial : config.Common.effectMaterial, Mathf.Max(.01f,config.Visuals.trailWidth),true);
                Trail.gameObject.name="Timestamped flight trail";Trail.positionCount=0;Trail.enabled=false;
            }
        }
        public static LineRenderer Line(Transform parent, Material material, float width, bool world = false)
        {
            var go = new GameObject("Sub effect line"); go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>(); line.sharedMaterial = material; line.widthMultiplier = width;
            line.useWorldSpace = world; line.numCapVertices = 3; line.numCornerVertices = 2; line.textureMode = LineTextureMode.Stretch; return line;
        }
        public static void Ring(LineRenderer line, Vector3 center, float radius, Vector3 normal, float fraction = 1)
        {
            var rotation = Quaternion.FromToRotation(Vector3.up, normal); int count = Mathf.Max(2, Mathf.CeilToInt(64 * fraction) + 1);
            line.positionCount = count;
            for (int i = 0; i < count; i++) { float a = i * Mathf.PI * 2 * fraction / (count - 1); line.SetPosition(i, center + rotation * new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius); }
        }
        void Tint(GameObject go, Color color, float flow = 0, double age = 0)
        {
            if (go == null) return;
            _properties.Clear(); _properties.SetColor("_BaseColor", color); _properties.SetFloat("_Flow", flow); _properties.SetFloat("_Phase", (float)age);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true)) if (_isGhost || renderer.gameObject.name != "Dark")
            {
                if(renderer is ParticleSystemRenderer){var main=renderer.GetComponent<ParticleSystem>().main;main.startColor=color;_properties.SetColor("_BaseColor",Color.white);}
                else _properties.SetColor("_BaseColor",color);
                renderer.SetPropertyBlock(_properties);
            }
        }
        GameObject Loop(GameObject source)
        {
            var go = _pool.Rent(source, Root.transform); if (go == null) return null;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) { ps.Play(true); _particles.Add(ps); }
            return go;
        }
        void SetModel(GameObject source)
        {
            if (_source == source) return;
            _pool.Return(Model); _source = source; Model = _pool.Rent(source, Root.transform); _rotor = null;
            if (Model != null)
            {
                foreach (var t in Model.GetComponentsInChildren<Transform>()) if (t.name == Config.TypeVisuals.rotorName) _rotor = t;
                if (_isGhost) foreach (var r in Model.GetComponentsInChildren<Renderer>()) r.sharedMaterial = Config.Visuals.previewMaterial != null ? Config.Visuals.previewMaterial : Config.Common.effectMaterial;
            }
        }
        public void Present(SubEntityState s, double now, byte observerTeam, bool valid = true)
        {
            Root.transform.SetPositionAndRotation(s.Position, SubWeaponMotion.Rotation(s));
            Visible = _isGhost || s.Type != SubWeaponType.InkMine || observerTeam == s.Team || s.Phase == SubEntityPhase.Warning;
            Root.SetActive(Visible); if (!Visible) return;
            var c = Config; double age = Math.Max(0, now - s.Changed);
            bool child = s.Phase == SubEntityPhase.Droplet || s.Phase == SubEntityPhase.Spray;
            bool deployed = !_heldMode && (s.Phase == SubEntityPhase.Active || s.Type == SubWeaponType.InkMine);
            var source = child && c.TypeVisuals.dropletPrefab != null ? c.TypeVisuals.dropletPrefab : deployed && c.TypeVisuals.deployedPrefab != null ? c.TypeVisuals.deployedPrefab :
                s.Type == SubWeaponType.Torpedo && (s.Phase == SubEntityPhase.Seeking || s.Phase == SubEntityPhase.Transforming) && c.TypeVisuals.trackingPrefab != null ? c.TypeVisuals.trackingPrefab : c.Common.entityPrefab;
            if(_heldMode)source=c.Common.heldPrefab!=null?c.Common.heldPrefab:c.Common.entityPrefab;
            if (s.Phase != SubEntityPhase.Line) SetModel(source);
            Color color = valid ? PrototypeArena.TeamColor(s.Team) : new Color(1,.25f,.15f);
            if (_isGhost) color.a = Mathf.Clamp01(c.Visuals.previewAlpha);
            if (Model != null)
            {
                float scale = child && c.TypeVisuals.dropletPrefab == null ? .2f : 1;
                if (s.Type == SubWeaponType.SplashWall && !deployed) scale = .15f;
                if (s.Type == SubWeaponType.SplashWall && deployed) scale = 1;
                Model.transform.localScale = Vector3.one * scale;
                if (s.Type == SubWeaponType.SplashWall && deployed)
                {
                    float expanded = _isGhost ? 1 : Mathf.Clamp01((float)(age / Math.Max(.001, c.Wall.expand)));
                    Model.transform.localScale = new Vector3(c.Wall.size.x, c.Wall.size.y * Mathf.Max(.02f, expanded), c.Wall.size.z);
                }
                if (s.Type == SubWeaponType.Torpedo && s.Phase == SubEntityPhase.Transforming) Model.transform.localScale *= .65f + .35f * Mathf.Clamp01((float)(age / Math.Max(.001, c.Torpedo.transform)));
                if (s.Type == SubWeaponType.Torpedo && s.Phase == SubEntityPhase.Seeking && s.Velocity.sqrMagnitude > .001f) Model.transform.rotation = Quaternion.LookRotation(s.Velocity);
                if (s.Phase == SubEntityPhase.Warning) Model.transform.localScale *= 1 + .12f * Mathf.Sin((float)now * 30);
                Tint(Model, color);
                if (_rotor != null) _rotor.localRotation = Quaternion.Euler(0, (float)(age * c.Sprinkler.rotationSpeed), 0);
            }
            if (Trail != null)
            {
                bool flying = !deployed && s.Phase != SubEntityPhase.Warning && s.Phase != SubEntityPhase.Transforming;
                if(_trail.Count>0 && now<_trail[_trail.Count-1].at-.001)_trail.Clear();
                while(_trail.Count>0 && now-_trail[0].at>c.Visuals.trailLifetime)_trail.RemoveAt(0);
                if(flying && (_trail.Count==0 || Vector3.Distance(_trail[_trail.Count-1].point,s.Position)>.01f))_trail.Add((s.Position,now));
                Trail.positionCount=_trail.Count;for(int i=0;i<_trail.Count;i++)Trail.SetPosition(i,_trail[i].point);
                Trail.enabled=_trail.Count>1;
                var tail=color;tail.a=0;Trail.startColor=tail;Trail.endColor=color;
                Trail.widthMultiplier = c.Visuals.trailWidth * (child ? .65f : 1);
            }
            if (_phase != s.Phase)
            {
                _phase = s.Phase;
                if (deployed && !_isGhost && _loop == null) _loop = Loop(c.TypeVisuals.persistentPrefab);
            }
            if (_loop != null)
            {
                _loop.SetActive(deployed);
                if (s.Type == SubWeaponType.SplashWall)
                {
                    _loop.transform.localScale = new Vector3(c.Wall.size.x, c.Wall.size.y * Mathf.Clamp01((float)(age / Math.Max(.001,c.Wall.expand))), c.Wall.size.z);
                    Tint(_loop, color, c.TypeVisuals.wallFlow, age);
                }
                else
                {
                    Tint(_loop, color);
                    foreach (var ps in _particles)
                    {
                        if (ps == null) continue;
                        var main = ps.main; var particleColor = color; particleColor.a = s.Type == SubWeaponType.ToxicMist ? .3f : .75f; main.startColor = particleColor;
                        if (s.Type == SubWeaponType.ToxicMist)
                        { var shape = ps.shape; shape.radius = c.Mist.radius * .85f; main.startSize = c.Mist.radius * .9f; main.startLifetime=2.5f; var emission = ps.emission; emission.rateOverTime = c.TypeVisuals.mistEmission;
                            var r=ps.GetComponent<ParticleSystemRenderer>();r.GetPropertyBlock(_properties);_properties.SetVector("_EffectCenter",s.Position);_properties.SetFloat("_EffectRadius",c.Mist.radius);r.SetPropertyBlock(_properties); }
                        if (s.Type == SubWeaponType.Sprinkler)
                        { var emission = ps.emission; emission.rateOverTime = age < c.Sprinkler.phases.x ? 30 : age < c.Sprinkler.phases.x + c.Sprinkler.phases.y ? 18 : 9;
                            main.startLifetime=.18f;main.startSpeed=c.Sprinkler.dropletSpeed*.5f;main.startSize=.14f;
                            var shape=ps.shape;shape.shapeType=ParticleSystemShapeType.Cone;shape.angle=5;shape.radius=.04f;
                            _loop.transform.localPosition=Vector3.up*.15f;_loop.transform.localRotation=Quaternion.Euler(0,(float)(age*c.Sprinkler.rotationSpeed),0)*Quaternion.LookRotation(new Vector3(1,.2f,0)); }
                    }
                }
            }
            bool range = deployed && (s.Type == SubWeaponType.PointSensor || s.Type == SubWeaponType.ToxicMist || s.Type == SubWeaponType.InkMine) ||
                s.Type == SubWeaponType.Autobomb && s.Phase == SubEntityPhase.Grounded || _isGhost && (s.Type == SubWeaponType.Autobomb || s.Type == SubWeaponType.Torpedo);
            if (range)
            {
                _range ??= Line(Root.transform, c.Common.effectMaterial, .035f);
                _range.enabled = true; _range.startColor = _range.endColor = color;
                float radius = s.Type == SubWeaponType.ToxicMist ? c.Mist.radius : s.Type == SubWeaponType.InkMine ? c.Mine.triggerRadius : s.Type == SubWeaponType.Autobomb || s.Type == SubWeaponType.Torpedo ? c.Tracking.radius : c.Mark.radius;
                Ring(_range, Vector3.up * .025f, radius, Vector3.up);
            }
            else if (_range != null) _range.enabled = false;
            float progress = s.Type == SubWeaponType.SplatBomb ? Mathf.Clamp01((float)(s.GroundAge / Math.Max(.001,c.Splat.groundFuse))) :
                s.FuseAt > s.Changed ? Mathf.Clamp01((float)((now - s.Changed) / (s.FuseAt - s.Changed))) : s.Type == SubWeaponType.InkMine && now < s.NextActionAt ? Mathf.Clamp01((float)((now - s.Born) / Math.Max(.001,c.Mine.arm))) : 0;
            bool arming = s.Type == SubWeaponType.InkMine && now < s.NextActionAt;
            bool warning = !_isGhost && (arming || progress > 0 && s.FuseAt > now || s.Type == SubWeaponType.SplatBomb && progress > 0 || s.Phase == SubEntityPhase.Warning);
            if (warning)
            {
                if (_warning == null) _warning = Loop(c.TypeVisuals.warningPrefab);
                if (_warning != null) { _warning.SetActive(true); Tint(_warning, Color.Lerp(color, Color.white, progress)); }
                _fuse ??= Line(Root.transform, c.Common.effectMaterial, .045f);
                _fuse.enabled = true; _fuse.startColor = _fuse.endColor = Color.Lerp(color, Color.white, progress);
                Ring(_fuse, Vector3.up * .15f, .38f, Vector3.up, Mathf.Max(.02f, 1-progress));
            }
            else { if (_warning != null) _warning.SetActive(false); if (_fuse != null) _fuse.enabled = false; }
            if(s.Type==SubWeaponType.PointSensor && deployed && !_isGhost)
            {
                _fuse ??= Line(Root.transform,c.Common.effectMaterial,.05f);_fuse.enabled=true;
                var scan=color;scan.a=.8f;_fuse.startColor=_fuse.endColor=scan;
                Ring(_fuse,Vector3.up*.03f,c.Mark.radius*Mathf.Repeat((float)age*.8f,1),Vector3.up);
            }
            if (s.Phase == SubEntityPhase.Line)
            {
                _line ??= Line(Root.transform, c.Visuals.trailMaterial != null ? c.Visuals.trailMaterial : c.Common.effectMaterial, Mathf.Max(.04f,c.Visuals.trailWidth));
                _line.positionCount = 2; _line.SetPosition(0, Vector3.zero); _line.SetPosition(1, Root.transform.InverseTransformPoint(s.End));
                color.a *= Mathf.Clamp01((float)((s.Expires-now)/.25)); _line.startColor = _line.endColor = color;
            }
        }
        public LineRenderer DetachTrail(Transform parent)
        {
            if(Trail==null||Trail.positionCount<2)return null;
            var trail=Trail;Trail=null;trail.transform.SetParent(parent,true);return trail;
        }
        public void Release()
        { _pool.Return(Model); _pool.Return(_loop); _pool.Return(_warning); SubWeaponVisualPool.Destroy(Root); }
    }
}
