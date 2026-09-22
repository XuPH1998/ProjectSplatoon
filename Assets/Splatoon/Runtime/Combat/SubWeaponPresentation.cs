using System;
using System.Collections.Generic;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Timestamped lifecycle playback. All damage and collision ownership remains on the host.</summary>
    public sealed class SubWeaponPresentation : MonoBehaviour
    {
        sealed class View
        {
            public SubWeaponVisual Visual;
            public SubEntityState State;
            public readonly List<SubEntityState> Samples = new(16);
            public SubWeaponTarget Target;
            public double Ends = double.PositiveInfinity;
            public bool HadFlight;
        }
        sealed class PredictedThrow
        {
            public SubWeaponService.Entity Entity;
            public SubWeaponVisual Visual;
            public uint Release;
            public double At;
            public bool Stopped;
        }
        sealed class EffectView
        {
            public GameObject Object;
            public LineRenderer Line;
            public double Born, Until;
            public Color Color;
            public bool Pooled;
        }
        public PrototypeMatch Match;
        readonly Dictionary<uint, View> _views = new();
        readonly Dictionary<(uint round, ulong owner, uint life, uint hero, uint action), PredictedThrow> _predicted = new();
        readonly List<(uint round, ulong owner, uint life, uint hero, uint action)> _predictionRemoval = new();
        readonly Dictionary<uint, uint> _removed = new();
        readonly HashSet<uint> _events = new(), _effectEvents = new(), _seen = new();
        readonly List<uint> _release = new();
        readonly List<EffectView> _effects = new();
        readonly List<SubEffectEvent> _pendingEffects = new();
        readonly List<Vector3> _previewPoints = new(512);
        readonly TpsAimSolver _aim = new();
        SubWeaponVisualPool _pool;
        SubWeaponVisual _ghost, _held;
        SubWeaponRuntimeConfig _heldConfig;
        LineRenderer _preview;
        AudioSource _audio;
        uint _snapshotWatermark;
        double _renderNow;
        public readonly HashSet<SubWeaponType> ObservedTypes = new();
        public readonly HashSet<SubWeaponType> ObservedFlights = new();
        public readonly HashSet<SubWeaponType> ObservedPersistent = new();
        public readonly HashSet<(ulong owner, SubWeaponType type)> ObservedFlightOwners = new();
        public float MaximumAnchorError { get; private set; }
        public int PredictionCount => _predicted.Count;
        public IReadOnlyCollection<uint> EntityIds => _views.Keys;
        public IEnumerable<SubEntityState> States { get { foreach (var v in _views.Values) if (double.IsPositiveInfinity(v.Ends)) yield return v.State; } }
        public SubPreviewResult LastPreview { get; private set; }
        public const double RemoteDelay = .1;
        bool Host => Match != null && Match.IsServer;
        byte ObserverTeam => PrototypePlayer.Local != null ? PrototypePlayer.Local.PresentedState.Team : (byte)1;
        double NetworkNow() => Match != null && Match.IsSpawned ? Match.NetworkManager.ServerTime.Time : Time.timeAsDouble;
        static (uint, ulong, uint, uint, uint) Key(SubEntityState s) => (s.Round, s.Owner, s.Life, s.HeroRevision, s.Action);
        SubWeaponVisualPool Pool => _pool ??= new SubWeaponVisualPool(transform);
        static bool IsMoving(SubEntityState s) => s.Phase == SubEntityPhase.Flying || s.Phase == SubEntityPhase.Seeking || s.Phase == SubEntityPhase.Droplet || s.Phase == SubEntityPhase.Spray || s.Phase == SubEntityPhase.Grounded && s.Type != SubWeaponType.Autobomb;
        double DisplayTime(SubEntityState s, double now) => Host || PrototypePlayer.Local != null && s.Owner == PrototypePlayer.Local.PlayerId ? now : now - RemoteDelay;
        public int Count(ulong owner, SubWeaponType type)
        {
            int n = 0; foreach (var v in _views.Values) if (v.State.Owner == owner && v.State.Type == type && v.State.ParentId == 0 && double.IsPositiveInfinity(v.Ends)) n++;
            foreach (var p in _predicted.Values) if (p.Entity.State.Owner == owner && p.Entity.State.Type == type) n++;
            return n;
        }
        public void PredictThrow(PrototypePlayer player, PlayerSnapshot s, SubLaunchSolution launch)
        {
            uint round = Match != null ? Match.State.Value.Round : 0;
            var state = new SubEntityState { Round = round, Owner = player.PlayerId, Life = s.Revision, HeroRevision = s.HeroRevision, Action = s.SubAction,
                Hero = s.HeroId, SubWeaponId = SubWeaponConfigService.Current.Resolve(s.HeroId,s.SubWeaponId), ConfigRevision = SubWeaponConfigService.Current.RevisionById(SubWeaponConfigService.Current.Resolve(s.HeroId,s.SubWeaponId)), Team = s.Team, Yaw = s.Yaw, Charge = s.SubCharge };
            var key = Key(state); if (_predicted.ContainsKey(key) || !launch.Valid) return;
            foreach (var v in _views.Values) if (v.State.ParentId == 0 && Key(v.State) == key) return;
            var config = PlayerLoadout.SubWeapon(s);
            var entity = SubWeaponMotion.Create(launch, config, state, s.SubUsedAt);
            var visual = new SubWeaponVisual(config, Pool, transform, entity.State);
            visual.Present(entity.State, s.SubUsedAt, ObserverTeam);
            _predicted.Add(key, new PredictedThrow { Entity = entity, Visual = visual, Release = s.SubConsumedRelease, At = s.SubUsedAt });
            Play(config.Common.useAudio, .2f);
        }
        public void PredictThrow(PrototypePlayer player, PlayerSnapshot s) => PredictThrow(player, s, SubWeaponService.SolveLaunch(player, s, PlayerLoadout.SubWeapon(s), s.SubCharge));
        public void Lifecycle(SubLifecycleEvent e)
        {
            if (!_events.Add(e.Sequence)) return;
            var state = e.State;
            if (e.Kind == SubLifecycleKind.Remove)
            {
                if (_removed.TryGetValue(state.Id, out var previous) && previous >= state.Version) return;
                var v = Upsert(state);
                _removed[state.Id] = state.Version;
                if (v != null) { v.Ends = Math.Min(v.Ends, state.SampledAt); v.Target?.gameObject.SetActive(false); }
                return;
            }
            Upsert(state);
        }
        View Upsert(SubEntityState state)
        {
            if (_removed.TryGetValue(state.Id, out var removed) && removed >= state.Version) return null;
            if (!_views.TryGetValue(state.Id, out var v))
            {
                var config = SubWeaponConfigService.Current.ForEntityId(state.SubWeaponId>0?state.SubWeaponId:SubWeaponConfigService.Current.DefaultId(state.Hero), state.ConfigRevision);
                SubWeaponVisual visual = null;
                if (state.ParentId == 0 && _predicted.TryGetValue(Key(state), out var prediction))
                { visual = prediction.Visual; _predicted.Remove(Key(state)); }
                bool adopted = visual != null;
                visual ??= new SubWeaponVisual(config, Pool, transform, state);
                v = new View { State = state, Visual = visual };
                _views.Add(state.Id, v);
                if (!adopted && state.ParentId == 0 && NetworkNow() - state.Born < .5) Play(config.Common.useAudio, .2f);
                if (!Host && state.Health > 0)
                {
                    var go = new GameObject("Sub collision proxy " + state.Id); go.transform.SetParent(transform, false);
                    v.Target = go.AddComponent<SubWeaponTarget>(); v.Target.Initialize(null, state, config);
                }
            }
            if (state.Version < v.State.Version && state.SampledAt >= v.State.SampledAt) return v;
            int index = v.Samples.FindIndex(s => s.SampledAt > state.SampledAt || s.SampledAt == state.SampledAt && s.Version >= state.Version);
            if (index < 0) v.Samples.Add(state);
            else if (v.Samples[index].SampledAt == state.SampledAt && v.Samples[index].Version == state.Version) v.Samples[index] = state;
            else v.Samples.Insert(index, state);
            if (v.Samples.Count > 64) v.Samples.RemoveAt(0);
            if (state.SampledAt >= v.State.SampledAt && state.Version >= v.State.Version)
            { v.State = state; v.Target?.UpdateState(state, v.Visual.Config, state.SampledAt); }
            ObservedTypes.Add(state.Type); return v;
        }
        public void Apply(IReadOnlyList<SubEntityState> states, uint watermark = uint.MaxValue)
        {
            if (watermark != uint.MaxValue && watermark < _snapshotWatermark) return;
            if (watermark != uint.MaxValue) _snapshotWatermark = watermark;
            _seen.Clear(); double sampledAt = 0;
            foreach (var state in states) { _seen.Add(state.Id); sampledAt = Math.Max(sampledAt, state.SampledAt); Upsert(state); }
            if (sampledAt == 0) sampledAt = NetworkNow();
            foreach (var pair in _views)
            {
                var v = pair.Value;
                if (!_seen.Contains(pair.Key) && v.State.Version <= watermark && double.IsPositiveInfinity(v.Ends))
                { v.Ends = sampledAt; _removed[pair.Key] = watermark; v.Target?.gameObject.SetActive(false); }
            }
        }
        void Update()
        {
            if (Match == null || !Match.IsSpawned) return;
            if (Match.State.Value.Phase == Splatoon.Networking.MatchPhase.Finished) { Clear(); return; }
            PresentAt(NetworkNow(), Time.deltaTime);
        }
        public void PresentAt(double now, float dt)
        {
            _renderNow = now;
            _predictionRemoval.Clear();
            foreach (var pair in _predicted)
            {
                var p = pair.Value; var local = PrototypePlayer.Local;
                if (local == null || !local.IsSpawned) { _predictionRemoval.Add(pair.Key); continue; }
                var authority = local.Snapshot.Value;
                bool rejected = authority.Revision != p.Entity.State.Life || authority.HeroRevision != p.Entity.State.HeroRevision ||
                    authority.SubConsumedRelease >= p.Release && authority.SubPhase == SubWeaponPhase.Idle && authority.SubAction < p.Entity.State.Action;
                if (rejected) { _predictionRemoval.Add(pair.Key); continue; }
                float step = 1f / GameplayConfig.Global.SimulationRate;
                while (p.At + step <= now && !p.Stopped)
                {
                    p.At += step; AdvancePrediction(p, step);
                    if (p.At >= p.Entity.State.Expires) p.Stopped = true;
                }
                p.Entity.State.FuseAt = p.Entity.Fuse; p.Entity.State.NextActionAt = p.Entity.NextAction; p.Entity.State.GroundAge = p.Entity.GroundAge;
                p.Visual.Present(p.Entity.State, now, ObserverTeam);
                if (IsMoving(p.Entity.State) && p.Visual.Visible) { ObservedFlights.Add(p.Entity.State.Type); ObservedFlightOwners.Add((p.Entity.State.Owner,p.Entity.State.Type)); }
            }
            foreach (var key in _predictionRemoval) { _predicted[key].Visual.Release(); _predicted.Remove(key); }
            _release.Clear();
            foreach (var pair in _views)
            {
                var v = pair.Value; double time = DisplayTime(v.State, now);
                if (time < v.State.Born) { v.Visual.Root.SetActive(false); continue; }
                if (time >= v.Ends || time >= v.State.Expires)
                {
                    if (!v.HadFlight && v.State.ParentId == 0 && v.State.Type != SubWeaponType.InkMine) FlightTrace(v, now);
                    if (v.Visual.PersistentVisible) EndEffect(v, now);
                    _release.Add(pair.Key); continue;
                }
                var s = DisplayState(v, time); v.Visual.Present(s, time, ObserverTeam);
                if (v.Visual.Model != null)
                    MaximumAnchorError = Mathf.Max(MaximumAnchorError, Vector3.Distance(v.Visual.Model.transform.position, s.Position));
                if (v.Visual.Visible && IsMoving(s)) { v.HadFlight = true; ObservedFlights.Add(s.Type); ObservedFlightOwners.Add((s.Owner,s.Type)); }
                if (v.Visual.PersistentVisible) ObservedPersistent.Add(s.Type);
            }
            foreach (uint id in _release) Release(id,true);
            for (int i = _pendingEffects.Count - 1; i >= 0; i--)
            {
                var e = _pendingEffects[i]; double display = Host ? now : now - RemoteDelay;
                if (_views.TryGetValue(e.EntityId, out var view)) display = DisplayTime(view.State, now);
                if (display < e.At) continue;
                SpawnEffect(e, now); _pendingEffects.RemoveAt(i);
            }
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                var effect = _effects[i];
                if (now >= effect.Until) { if (effect.Pooled) Pool.Return(effect.Object); else SubWeaponVisualPool.Destroy(effect.Object); _effects.RemoveAt(i); continue; }
                if (effect.Line != null) { var color = effect.Color; color.a *= Mathf.Clamp01((float)((effect.Until-now)/(effect.Until-effect.Born))); effect.Line.startColor = effect.Line.endColor = color; }
            }
        }
        void AdvancePrediction(PredictedThrow p, float dt)
        {
            var e = p.Entity;
            if (e.Config.Type == SubWeaponType.AngleShooter)
            {
                float remaining = Mathf.Min(e.Config.Angle.speed * dt, e.Config.Angle.range - e.Travelled);
                for (int n = 0; n < 12 && remaining > .0001f; n++)
                {
                    float before = e.Travelled;
                    bool hit = SubWeaponMotion.LineStep(e, _aim, remaining, out var h); remaining -= e.Travelled-before;
                    if (!hit) break;
                    if (h.Collider.GetComponentInParent<PrototypePlayer>() != null || h.Collider.GetComponent<SubWeaponTarget>() != null) { p.Stopped = true; break; }
                    SubWeaponMotion.ReflectLine(e,h); if (e.State.Explosions > e.Config.Angle.reflections) { p.Stopped = true; break; }
                }
                if (e.Travelled >= e.Config.Angle.range) p.Stopped = true; return;
            }
            if (!e.Attached && e.State.Phase != SubEntityPhase.Active)
            {
                var contact = SubWeaponMotion.Advance(e, _aim, p.At, dt, out _);
                if (contact == SubMotionContact.Explode) { p.Stopped = true; return; }
                if (e.Config.Type == SubWeaponType.Autobomb && e.State.Phase == SubEntityPhase.Grounded) { p.Stopped = true; return; }
            }
            if (e.Attached && e.Attachment != null) { e.State.Position = e.Attachment.TransformPoint(e.LocalPosition); e.State.Normal = e.Attachment.TransformDirection(e.LocalNormal); }
            if (SubWeaponMotion.FuseDue(e, p.At, dt) && !SubWeaponMotion.NextFizzyBurst(e, p.At)) p.Stopped = true;
        }
        SubEntityState DisplayState(View v, double time)
        {
            var samples = v.Samples; if (samples.Count == 0) return v.State;
            int index = 0; while (index + 1 < samples.Count && samples[index + 1].SampledAt <= time) index++;
            var s = samples[index];
            if (index + 1 < samples.Count && samples[index + 1].Phase == s.Phase)
            {
                var next = samples[index + 1]; float t = Mathf.Clamp01((float)((time-s.SampledAt)/Math.Max(.00001,next.SampledAt-s.SampledAt)));
                s.Position = Vector3.Lerp(s.Position,next.Position,t); s.Velocity = Vector3.Lerp(s.Velocity,next.Velocity,t); s.Normal = Vector3.Slerp(s.Normal,next.Normal,t); s.Yaw = Mathf.LerpAngle(s.Yaw,next.Yaw,t);
                s.GroundAge = s.GroundAge + (next.GroundAge-s.GroundAge)*t; return s;
            }
            float elapsed = Mathf.Clamp((float)(time-s.SampledAt),0,.1f);
            if (!IsMoving(s) || elapsed <= 0) return s;
            if (s.Type == SubWeaponType.Autobomb && s.Phase == SubEntityPhase.Seeking || s.Type == SubWeaponType.AngleShooter)
            { s.Position += s.Velocity * elapsed; return s; }
            var e = new SubWeaponService.Entity(s,v.Visual.Config) { Fuse=s.FuseAt, NextAction=s.NextActionAt, GroundAge=s.GroundAge };
            float step = 1f/GameplayConfig.Global.SimulationRate;
            for (float t = 0; t + step <= elapsed + .00001f; t += step)
            {
                if (s.Phase == SubEntityPhase.Spray)
                { var delta = SubWeaponService.FlightStep(ref e.State.Velocity,new SubFlight{gravity=e.Config.Sprinkler.dropletGravity},step); e.State.Position += delta; }
                else
                {
                    var contact=SubWeaponMotion.Advance(e,_aim,s.SampledAt+t+step,step,out _);
                    // Bounces and repeated floor contacts continue through the entire display interval.
                    if(contact!=SubMotionContact.None&&contact!=SubMotionContact.Bounce)break;
                }
            }
            return e.State;
        }
        public void PresentHeld(PrototypePlayer player, PlayerSnapshot s)
        {
            bool holding = s.SubPhase != SubWeaponPhase.Idle && s.Health > 0;
            if (!holding) { if (_held != null) _held.Root.SetActive(false); if (_ghost != null) _ghost.Root.SetActive(false); if (_preview != null) _preview.enabled=false; return; }
            var c = PlayerLoadout.SubWeapon(s);
            if (_heldConfig != c)
            { _held?.Release(); _ghost?.Release(); _held = _ghost = null; _heldConfig = c; if (_preview != null) SubWeaponVisualPool.Destroy(_preview.gameObject); _preview=null; }
            var launch = SubWeaponService.SolveLaunch(player,s,c,s.SubCharge);
            var identity = new SubEntityState { Owner=player.PlayerId, Hero=s.HeroId, Team=s.Team, Type=c.Type, Yaw=s.Yaw, Charge=s.SubCharge, Normal=Vector3.up, Position=launch.Position };
            LastPreview = SubWeaponMotion.Preview(launch,c,identity,_previewPoints,_aim);
            _held ??= new SubWeaponVisual(c,Pool,transform,identity,held:true);
            var hand=identity;SubWeaponService.LaunchGeometry(player,s,c,s.SubCharge,out hand.Position,out _);
            _held.Present(hand,NetworkNow(),ObserverTeam); _held.Root.transform.localScale=Vector3.one*.65f;
            _preview ??= SubWeaponVisual.Line(transform,c.Visuals.previewMaterial!=null?c.Visuals.previewMaterial:c.Common.effectMaterial,Mathf.Max(.01f,c.Visuals.previewWidth),true);
            _preview.enabled=true; _preview.positionCount=_previewPoints.Count;
            for(int i=0;i<_previewPoints.Count;i++)_preview.SetPosition(i,_previewPoints[i]);
            bool valid=launch.Valid && s.Ink>=c.Common.inkCost;
            var color=valid?PrototypeArena.TeamColor(s.Team):Color.yellow; color.a=LastPreview.Uncertain?.45f:.8f;
            _preview.startColor=_preview.endColor=color;
            var end=identity;end.Position=LastPreview.Position;end.Normal=LastPreview.Normal;end.Phase=SubEntityPhase.Active;
            _ghost ??= new SubWeaponVisual(c,Pool,transform,end,true); _ghost.Present(end,NetworkNow(),ObserverTeam,valid);
        }
        public int Preview(ulong owner, byte team, Vector3 position, Vector3 velocity, SubWeaponRuntimeConfig c, float charge, Vector3[] points)
        {
            var launch=new SubLaunchSolution { Position=position,Velocity=velocity,Normal=Vector3.up };
            var result=SubWeaponMotion.Preview(launch,c,new SubEntityState{Owner=owner,Team=team,Charge=charge,Yaw=velocity.sqrMagnitude>.001f?Quaternion.LookRotation(velocity).eulerAngles.y:0},_previewPoints,_aim);
            int count=Mathf.Min(points.Length,_previewPoints.Count);for(int i=0;i<count;i++)points[i]=_previewPoints[Mathf.RoundToInt(i*(_previewPoints.Count-1f)/Mathf.Max(1,count-1))];return count;
        }
        public void Effect(SubEffectEvent e) { if (_effectEvents.Add(e.Sequence)) _pendingEffects.Add(e); }
        void SpawnEffect(SubEffectEvent e,double now)
        {
            var c=SubWeaponConfigService.Current.ForEntityId(e.SubWeaponId>0?e.SubWeaponId:SubWeaponConfigService.Current.DefaultId(e.Hero),e.ConfigRevision);ObservedTypes.Add(c.Type);
            var color=PrototypeArena.TeamColor(e.Team);var line=SubWeaponVisual.Line(transform,c.Common.effectMaterial,.065f,true);
            SubWeaponVisual.Ring(line,e.Position,Mathf.Max(.15f,e.Radius),e.Normal.sqrMagnitude>.001f?e.Normal:Vector3.up);
            _effects.Add(new EffectView{Object=line.gameObject,Line=line,Born=now,Until=now+Math.Max(.08,c.Visuals.impactLifetime),Color=color});
            var source=e.Kind==SubEffectKind.Destroyed?c.Visuals.endPrefab:c.Visuals.impactPrefab;
            var fx=Pool.Rent(source,transform);
            if(fx!=null)
            {
                fx.transform.position=e.Position;fx.transform.localScale=Vector3.one*Mathf.Max(.4f,e.Radius*.35f);
                foreach(var ps in fx.GetComponentsInChildren<ParticleSystem>()){var main=ps.main;main.startColor=color;ps.Play(true);}
                _effects.Add(new EffectView{Object=fx,Pooled=true,Born=now,Until=now+Math.Max(.3,c.Visuals.impactLifetime)});
            }
            Play(c.Common.effectAudio,.15f);
        }
        void FlightTrace(View v,double now)
        {
            if(v.Samples.Count<2)return;
            var line=SubWeaponVisual.Line(transform,v.Visual.Config.Visuals.trailMaterial!=null?v.Visual.Config.Visuals.trailMaterial:v.Visual.Config.Common.effectMaterial,Mathf.Max(.05f,v.Visual.Config.Visuals.trailWidth),true);
            line.positionCount=v.Samples.Count;for(int i=0;i<v.Samples.Count;i++)line.SetPosition(i,v.Samples[i].Position);
            _effects.Add(new EffectView{Object=line.gameObject,Line=line,Born=now,Until=now+.12,Color=PrototypeArena.TeamColor(v.State.Team)});
            ObservedFlights.Add(v.State.Type); ObservedFlightOwners.Add((v.State.Owner,v.State.Type));
        }
        void EndEffect(View v,double now)
        {
            var go=Pool.Rent(v.Visual.Config.Visuals.endPrefab,transform);if(go==null)return;
            go.transform.position=v.State.Position;
            foreach(var ps in go.GetComponentsInChildren<ParticleSystem>()){var main=ps.main;main.startColor=PrototypeArena.TeamColor(v.State.Team);ps.Play(true);}
            _effects.Add(new EffectView{Object=go,Pooled=true,Born=now,Until=now+.35});
        }
        void Play(AudioClip clip,float volume)
        {if(clip==null)return;if(_audio==null){_audio=gameObject.AddComponent<AudioSource>();_audio.playOnAwake=false;}_audio.PlayOneShot(clip,volume);}
        void Release(uint id,bool keepTrail=false)
        { var v=_views[id];
            if(keepTrail){var trail=v.Visual.DetachTrail(transform);if(trail!=null)_effects.Add(new EffectView{Object=trail.gameObject,Line=trail,Born=_renderNow,Until=_renderNow+v.Visual.Config.Visuals.trailLifetime,Color=PrototypeArena.TeamColor(v.State.Team)});}
            v.Visual.Release();if(v.Target!=null)SubWeaponVisualPool.Destroy(v.Target.gameObject);_views.Remove(id); }
        public bool TryPredictedVisual(ulong owner,uint action,out SubWeaponVisual visual)
        { foreach(var pair in _predicted)if(pair.Key.owner==owner&&pair.Key.action==action){visual=pair.Value.Visual;return true;}visual=null;return false; }
        public bool TryVisual(uint id,out SubWeaponVisual visual)
        {if(_views.TryGetValue(id,out var view)){visual=view.Visual;return true;}visual=null;return false;}
        public void Clear()
        {
            foreach(var p in _predicted.Values)p.Visual.Release();_predicted.Clear();
            _release.Clear();foreach(uint id in _views.Keys)_release.Add(id);foreach(uint id in _release)Release(id);
            foreach(var e in _effects){if(e.Pooled)Pool.Return(e.Object);else SubWeaponVisualPool.Destroy(e.Object);}_effects.Clear();_pendingEffects.Clear();
            _held?.Release();_ghost?.Release();_held=_ghost=null;_heldConfig=null;
            if(_preview!=null)SubWeaponVisualPool.Destroy(_preview.gameObject);_preview=null;
            _removed.Clear();ObservedTypes.Clear();ObservedFlights.Clear();ObservedPersistent.Clear();ObservedFlightOwners.Clear();MaximumAnchorError=0;
        }
        void OnDestroy(){_pool?.BeginShutdown();Clear();_pool?.Dispose();}
    }
}
