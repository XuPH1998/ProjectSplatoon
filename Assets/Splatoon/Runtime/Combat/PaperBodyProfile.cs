using UnityEngine;

namespace Splatoon.Combat
{
    [CreateAssetMenu(menuName = "喷墨对战/纸片表现")]
    public sealed class PaperBodyProfile : ScriptableObject
    {
        public GameObject CapturePrefab;
        public Vector3 CaptureCenter;
        public float CaptureHeight = 2;
        public float AnimationReferenceSpeed = 5;
        public int TextureWidth = 512, TextureHeight = 1024;
        public Material Material;
        public Mesh DisplayMesh;
        public Vector2 Size = new(.9f, 1.8f);
        public float Thickness = .04f, SurfaceOffset = .03f;
        public Vector3 CameraOffset = new(0, .9f, 0);
        public string ContentHash;
    }
}
