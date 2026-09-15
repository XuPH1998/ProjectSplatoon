using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public sealed class SplatlingFeedback : MonoBehaviour
    {
        AudioSource _audio; AudioClip _tone;
        ParticleSystem _charge;
        public void Present(PlayerSnapshot state, cfg.HeroConfig config, Transform muzzle, Material material, float dt)
        {
            if(!Application.isPlaying)return;
            bool active=state.Health>0&&!state.Swimming&&!state.CompactBody&&SplatlingSimulation.Charging(state);
            if(_audio==null)
            {
                _audio=gameObject.AddComponent<AudioSource>();_audio.playOnAwake=false;_audio.loop=true;_audio.volume=0;_audio.spatialBlend=1;_audio.maxDistance=18;
                const int rate=24000;var samples=new float[rate];
                for(int i=0;i<rate;i++)samples[i]=(Mathf.Sin(i*2*Mathf.PI*110/rate)+.25f*Mathf.Sin(i*2*Mathf.PI*330/rate))*.18f;
                _tone=AudioClip.Create("Splatling spin",rate,1,rate,false);_tone.SetData(samples,0);_audio.clip=_tone;
            }
            _audio.pitch=Mathf.Lerp(.6f,2.4f,WeaponSimulation.ChargeRatio(state,config));
            _audio.volume=Mathf.MoveTowards(_audio.volume,active?.2f:0,dt*2);
            if(active&&!_audio.isPlaying)_audio.Play();else if(!active&&_audio.volume<=0)_audio.Stop();
            if(_charge==null&&muzzle!=null&&material!=null)
            {
                var go=new GameObject("Charge feedback");go.transform.SetParent(muzzle,false);_charge=go.AddComponent<ParticleSystem>();
                _charge.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=_charge.main;main.playOnAwake=false;main.loop=true;main.startLifetime=.12f;main.startSpeed=.05f;main.startSize=.035f;main.maxParticles=12;
                var emission=_charge.emission;emission.rateOverTime=24;var shape=_charge.shape;shape.shapeType=ParticleSystemShapeType.Sphere;shape.radius=.035f;
                go.GetComponent<ParticleSystemRenderer>().sharedMaterial=material;
            }
            if(_charge!=null)
            {
                var main=_charge.main;main.startColor=PrototypeArena.TeamColor(state.Team);
                if(active&&!_charge.isPlaying)_charge.Play();else if(!active&&_charge.isPlaying)_charge.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
        void OnDisable(){if(_audio!=null)_audio.Stop();if(_charge!=null)_charge.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);}
        void OnDestroy(){if(_tone!=null)Destroy(_tone);}
    }
}
