using UnityEngine;

namespace Splatoon.Prototype
{
    [DisallowMultipleComponent, RequireComponent(typeof(BoxCollider))]
    public sealed class HeroChangeZone : MonoBehaviour
    {
        [Tooltip("1：粉队；2：蓝队"), Range(1, 2)] public byte Team = 1;
        BoxCollider _box;
        public BoxCollider Volume => _box != null ? _box : _box = GetComponent<BoxCollider>();

        void Reset() => Volume.isTrigger = true;
        void OnValidate() => Volume.isTrigger = true;

        // Query the simulation position, not the rendered body or trigger-event history.
        public bool Contains(byte team, Vector3 position)
        {
            var box = Volume;
            if (team < 1 || team > 2 || team != Team || !isActiveAndEnabled ||
                box == null || !box.enabled || !box.isTrigger) return false;
            Vector3 scale = transform.lossyScale;
            if (Mathf.Abs(scale.x) < .000001f || Mathf.Abs(scale.y) < .000001f || Mathf.Abs(scale.z) < .000001f) return false;
            Vector3 local = transform.InverseTransformPoint(position) - box.center;
            Vector3 half = box.size * .5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        void OnDrawGizmos()
        {
            var box = Volume;
            if (box == null) return;
            var matrix = Gizmos.matrix; var color = Gizmos.color;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = PrototypeArena.TeamColor(Team);
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = matrix; Gizmos.color = color;
        }
    }
}
