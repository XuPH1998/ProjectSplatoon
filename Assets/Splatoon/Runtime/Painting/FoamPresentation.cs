using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace Splatoon.Painting
{
    // A bounded local observer of installed terrain. No gameplay or network callbacks from particles.
    public sealed class FoamPresentation : MonoBehaviour
    {
        static readonly ProfilerMarker Marker=new("Splatoon.Foam.Presentation");
        struct Group { public ParticleSystem Particles; public float Until; }
        Group[] _groups;
        FoamTerrainWorld _world;
        FoamAppearanceProfile _profile;
        Camera _camera;
        public int ActiveGroups { get; private set; }
        public int PeakGroups { get; private set; }
        public int DroppedGroups { get; private set; }
        public int PlayedGroups { get; private set; }
        public double LastUpdateMs { get; private set; }

        public void Initialize(FoamTerrainWorld world,FoamAppearanceProfile profile)
        {
            _world=world;_profile=profile;
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null||profile==null||profile.BubbleMesh==null||profile.BubbleMaterial==null)
            {enabled=false;return;}
            _camera=Camera.main;_groups=new Group[Mathf.Clamp(profile.MaxGroups,0,24)];
            for(int i=0;i<_groups.Length;i++)
            {
                var go=new GameObject("Foam feedback "+i);go.transform.SetParent(transform,false);
                var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=ps.main;main.playOnAwake=false;main.loop=false;main.duration=.45f;
                main.maxParticles=8;main.simulationSpace=ParticleSystemSimulationSpace.World;
                main.startSpeed=0;main.startLifetime=.45f;main.gravityModifier=.35f;
                main.cullingMode=ParticleSystemCullingMode.AlwaysSimulate;
                var emission=ps.emission;emission.enabled=false;
                var shape=ps.shape;shape.enabled=false;
                var collision=ps.collision;collision.enabled=false;
                var lights=ps.lights;lights.enabled=false;
                var size=ps.sizeOverLifetime;size.enabled=true;
                size.size=new ParticleSystem.MinMaxCurve(1, new AnimationCurve(new Keyframe(0,.4f),new Keyframe(.25f,1),new Keyframe(1,0)));
                var renderer=go.GetComponent<ParticleSystemRenderer>();renderer.renderMode=ParticleSystemRenderMode.Mesh;
                renderer.mesh=profile.BubbleMesh;renderer.sharedMaterial=profile.BubbleMaterial;
                renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
                renderer.enableGPUInstancing=false;
                _groups[i].Particles=ps;
            }
            world.SurfaceChanged+=OnSurfaceChanged;world.SurfaceReset+=Clear;
        }
        void OnSurfaceChanged(FoamSurfaceChange change)
        {
            using var marker=Marker.Auto();
            if(_groups==null||!isActiveAndEnabled)return;
            if(_camera==null)_camera=Camera.main;
            if(_camera==null||Time.unscaledTimeAsDouble<change.Chunk.NextFeedbackTime)return;
            bool growth=change.GrowthWeight>=change.LossWeight;
            var position=growth?change.GrowthPosition:change.LossPosition;
            float distance=Vector3.Distance(_camera.transform.position,position);
            float near=Mathf.Max(0,_profile.FeedbackDistance.x),far=Mathf.Max(near,_profile.FeedbackDistance.y);
            if(distance>far)return;
            int slot=-1;
            for(int i=0;i<_groups.Length;i++)if(_groups[i].Until<=Time.time){slot=i;break;}
            if(slot<0){DroppedGroups++;return;}
            change.Chunk.NextFeedbackTime=Time.unscaledTimeAsDouble+Mathf.Max(.1f,_profile.ChunkInterval);
            int count=Mathf.Clamp(_profile.ParticlesPerGroup,1,8);if(distance>near)count=Mathf.Max(1,count/2);
            float lifetime=Mathf.Clamp(_profile.Lifetime,.05f,.45f);
            uint seed=InkShapeAtlas.Hash(_world.Revision^(uint)Mathf.RoundToInt(position.x*100)^(uint)Mathf.RoundToInt(position.z*7919));
            var ps=_groups[slot].Particles;ps.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);ps.Play(false);
            Color color=Prototype.PrototypeArena.TeamColor(growth?change.GrowthTeam:change.LossTeam);
            color=Color.Lerp(color,Color.white,.10f);
            for(int i=0;i<count;i++)
            {
                seed=InkShapeAtlas.Hash(seed+(uint)i+1);float angle=(seed&65535)*(Mathf.PI*2/65536);
                float variation=((seed>>16)&65535)/65535f;
                var tangent=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));
                ps.Emit(new ParticleSystem.EmitParams
                {
                    position=position+Vector3.up*.035f+tangent*.06f,
                    velocity=tangent*Mathf.Lerp(.15f,.45f,variation)+Vector3.up*(growth?.5f:.25f),
                    startSize=Mathf.Lerp(.035f,.085f,variation),startLifetime=lifetime*(.7f+.3f*variation),
                    startColor=color,randomSeed=seed==0?1:seed,applyShapeToPosition=false
                },1);
            }
            _groups[slot].Until=Time.time+lifetime;PlayedGroups++;
            CountActive();
        }
        void CountActive()
        {
            ActiveGroups=0;for(int i=0;i<_groups.Length;i++)if(_groups[i].Until>Time.time)ActiveGroups++;
            PeakGroups=Mathf.Max(PeakGroups,ActiveGroups);
        }
        void LateUpdate()
        {
            long start=System.Diagnostics.Stopwatch.GetTimestamp();using var marker=Marker.Auto();
            if(_groups!=null)CountActive();
            LastUpdateMs=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000d/System.Diagnostics.Stopwatch.Frequency;
        }
        public void Clear()
        {
            if(_groups==null)return;
            for(int i=0;i<_groups.Length;i++){_groups[i].Particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);_groups[i].Until=0;}
            if(_world!=null)foreach(var patch in _world.PatchValues)foreach(var chunk in patch.Chunks)chunk.NextFeedbackTime=0;
            ActiveGroups=0;
        }
        void OnDestroy()
        {
            if(_world!=null){_world.SurfaceChanged-=OnSurfaceChanged;_world.SurfaceReset-=Clear;}
        }
    }
}
