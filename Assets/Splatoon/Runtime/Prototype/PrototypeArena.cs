using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed class PrototypeArena : MonoBehaviour
    {
        public static PrototypeArena Current { get; private set; }
        [Tooltip("不可涂色区域：掩体和边界的碰撞盒，不计入比分")] public BoxCollider[] Unpaintable;
        [Tooltip("涂色网格材质，使用顶点颜色显示归属")] public Material PaintMaterial;
        public PaintGrid Grid { get; private set; }
        private Mesh _mesh;
        private Color32[] _colors;
        private bool _dirty;
        public static readonly Color Orange = new(1f, .30f, .055f);
        public static readonly Color Blue = new(.08f, .42f, 1f);
        public static Color TeamColor(byte team) => team == 1 ? Orange : Blue;
        private void Awake()
        {
            Current = this;
            Physics.SyncTransforms();
            Grid = new PaintGrid(64, .5f, IsBlocked);
            BuildMesh();
        }
        private bool IsBlocked(Vector3 p)
        {
            foreach (var box in Unpaintable)
            { var b = box.bounds; if (p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z) return true; }
            return false;
        }
        private void BuildMesh()
        {
            int count = Grid.Cells.Length;
            var vertices = new Vector3[count * 4]; var triangles = new int[count * 6];
            _colors = new Color32[count * 4];
            for (int i = 0; i < count; i++)
            {
                Vector3 c = Grid.Center(i); int v = i * 4, t = i * 6;
                vertices[v] = c + new Vector3(-.25f,0,-.25f); vertices[v+1] = c + new Vector3(.25f,0,-.25f);
                vertices[v+2] = c + new Vector3(-.25f,0,.25f); vertices[v+3] = c + new Vector3(.25f,0,.25f);
                triangles[t] = v; triangles[t+1] = v+2; triangles[t+2] = v+1;
                triangles[t+3] = v+2; triangles[t+4] = v+3; triangles[t+5] = v+1;
                RefreshCell(i);
            }
            _mesh = new Mesh { name = "涂色网格" }; _mesh.MarkDynamic();
            _mesh.vertices = vertices; _mesh.triangles = triangles; _mesh.colors32 = _colors;
            _mesh.RecalculateBounds();
            var surface = new GameObject("涂色表面", typeof(MeshFilter), typeof(MeshRenderer));
            surface.transform.SetParent(transform, false);
            surface.GetComponent<MeshFilter>().sharedMesh = _mesh;
            surface.GetComponent<MeshRenderer>().sharedMaterial = PaintMaterial;
        }
        public void RefreshCell(int index)
        {
            byte cell = Grid.Cells[index];
            Color color = cell == 1 ? Orange : cell == 2 ? Blue : new Color(.37f, .41f, .43f);
            // A slight checker gives an obvious metre scale without drawing thousands of objects.
            if ((index % 64 + index / 64) % 2 == 0) color *= .96f;
            color.a = 1;
            for (int n = 0; n < 4; n++) _colors[index * 4 + n] = color;
            _dirty = true;
        }
        private void LateUpdate() { if (_dirty) { _mesh.colors32 = _colors; _dirty = false; } }
        public static Vector3 Spawn(byte team, int slot) => new(slot == 0 ? -4 : 4, .1f, team == 1 ? -13 : 13);
        private void OnDestroy() { if (Current == this) Current = null; if (_mesh != null) Destroy(_mesh); }
    }
}
