using UnityEngine;

namespace Splatoon.Painting
{
    [CreateAssetMenu(menuName="喷墨对战/泡沫表现")]
    public sealed class FoamAppearanceProfile : ScriptableObject
    {
        public Texture2D Pores;
        [Min(.01f)] public float PoreScale=2.5f;
        [Range(0,1)] public float PoreBrightness=.12f;
        [Range(0,1)] public float NormalStrength=.22f;
        [Range(0,1)] public float SoftHighlight=.18f;
        public Vector2 DetailDistance=new(6,18);
        [Range(0,24)] public int MaxGroups=24;
        [Range(1,8)] public int ParticlesPerGroup=8;
        [Range(.05f,.45f)] public float Lifetime=.45f;
        [Min(.1f)] public float ChunkInterval=.1f;
        public Vector2 FeedbackDistance=new(8,16);
        public Mesh BubbleMesh;
        public Material BubbleMaterial;
        public void Apply(Material material)
        {
            if(material==null)return;
            material.SetTexture("_Pores",Pores);
            material.SetVector("_FoamDetail",new Vector4(PoreScale,PoreBrightness,NormalStrength,SoftHighlight));
            material.SetVector("_FoamDistance",new Vector4(DetailDistance.x,Mathf.Max(DetailDistance.x+.1f,DetailDistance.y),0,0));
        }
    }
}
