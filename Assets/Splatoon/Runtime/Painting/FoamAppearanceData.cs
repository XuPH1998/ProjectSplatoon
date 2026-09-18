using UnityEngine;

namespace Splatoon.Painting
{
    public struct FoamUpdateMetrics
    {
        public uint Revision;
        public bool Install;
        public int DirtyChunks, ColliderRebuilds, NormalOnlyUpdates, OwnerUploads;
        public double TotalMs, DepositMs, RoundMs, RelaxMs, OwnershipMs, MeshMs, ColliderMs, FeedbackMs;
    }

    public struct FoamSurfaceChange
    {
        public FoamChunk Chunk;
        public Vector3 GrowthPosition, LossPosition;
        public float GrowthWeight, LossWeight;
        public byte GrowthTeam, LossTeam;
    }

    // Immutable per-stamp lobes. Normalization over reachable node areas remains in the authority.
    public readonly struct FoamLobes
    {
        readonly Vector2 _a, _b;
        readonly float _ra, _rb;
        static float Next(ref uint state) { state=InkShapeAtlas.Hash(state+0x9e3779b9u);return (state&0x00ffffff)/16777216f; }
        public FoamLobes(uint seed)
        {
            float angle=Next(ref seed)*Mathf.PI*2;
            _a=new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*Mathf.Lerp(.22f,.38f,Next(ref seed));
            angle+=Mathf.Lerp(1.8f,3.8f,Next(ref seed));
            _b=new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*Mathf.Lerp(.28f,.45f,Next(ref seed));
            _ra=Mathf.Lerp(.52f,.68f,Next(ref seed));_rb=Mathf.Lerp(.42f,.58f,Next(ref seed));
        }
        static float Dome(Vector2 p,float radius)
        {
            float q=p.sqrMagnitude/(radius*radius);
            float t=Mathf.Clamp01(1-q);
            // Broad rounded cap with a smooth shoulder, no hard flat plateau.
            return t*t*(3-2*t);
        }
        public float Weight(Vector2 point)=>.60f*Dome(point,1)+.25f*Dome(point-_a,_ra)+.15f*Dome(point-_b,_rb);
    }
}
