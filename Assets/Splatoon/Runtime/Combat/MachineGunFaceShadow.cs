using UnityEngine;

namespace Splatoon.Combat
{
    // Source MachineGun SDF orientation: head Up is face forward; Right is face right.
    [ExecuteAlways, DefaultExecutionOrder(300)]
    public sealed class MachineGunFaceShadow : MonoBehaviour
    {
        public Transform Head;
        public Renderer[] Faces;
        MaterialPropertyBlock _block;
        void LateUpdate() => Apply();
        public void Apply()
        {
            if (Head == null || Faces == null) return;
            _block ??= new MaterialPropertyBlock();
            foreach (var face in Faces)
            {
                if (face == null) continue;
                face.GetPropertyBlock(_block); Write(); face.SetPropertyBlock(_block);
                for (int i=0;i<face.sharedMaterials.Length;i++)
                { face.GetPropertyBlock(_block,i); Write(); face.SetPropertyBlock(_block,i); }
            }
        }
        void Write()
        {
            _block.SetFloat("_UseSDFShadow",1); _block.SetFloat("_UseScriptVectors",1);
            _block.SetVector("_FaceForward",Head.up); _block.SetVector("_FaceRight",Head.right);
        }
    }
}
