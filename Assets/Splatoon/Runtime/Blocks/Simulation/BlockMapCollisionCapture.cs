using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Splatoon.Blocks.Simulation
{
    /// <summary>Captures authored geometry once after loading/generation, before content-signature agreement.
    /// Original Unity colliders remain owned by the scene. This adapter does not create or modify them.</summary>
    public static class BlockMapCollisionCapture
    {
        static readonly int[] BoxTriangles =
        { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 };

        public static BlockMapCollisionData Capture(Transform root, Allocator allocator = Allocator.Persistent)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            return Capture(root.GetComponentsInChildren<Collider>(true), allocator);
        }

        public static BlockMapCollisionData Capture(IEnumerable<Collider> colliders, Allocator allocator = Allocator.Persistent)
        {
            if (colliders == null) throw new ArgumentNullException(nameof(colliders));
            var sources = new List<Collider>(); var unique = new HashSet<Collider>();
            foreach (var source in colliders)
                if (source != null && unique.Add(source)) sources.Add(source);
            sources.Sort((a, b) => string.CompareOrdinal(SourceKey(a), SourceKey(b)));
            var builder = new BlockMapCollisionBuilder();
            foreach (var source in sources)
            {
                builder.AddSignatureData(Signature(source));
                if (!source.enabled || !source.gameObject.activeInHierarchy || source.isTrigger) continue;
                var material = source.sharedMaterial;
                float sf = material != null ? material.staticFriction : .6f;
                float df = material != null ? material.dynamicFriction : .6f;
                if (source is BoxCollider box) AddBox(builder, box, sf, df);
                else if (source is MeshCollider meshCollider) AddMesh(builder, meshCollider, sf, df);
                else throw new NotSupportedException($"Unsupported static map collider {source.GetType().Name} at {SourceKey(source)}. Add a bake adapter before accepting this map.");
            }
            return builder.Build(allocator);
        }

        static void AddBox(BlockMapCollisionBuilder builder, BoxCollider source, float sf, float df)
        {
            var matrix = source.transform.localToWorldMatrix; Vector3 half = source.size * .5f;
            var vertices = new float3[8];
            for (int side = 0; side < 2; side++)
            {
                int offset = side * 4; float z = side == 0 ? -half.z : half.z;
                vertices[offset] = matrix.MultiplyPoint3x4(source.center + new Vector3(-half.x, -half.y, z));
                vertices[offset + 1] = matrix.MultiplyPoint3x4(source.center + new Vector3(half.x, -half.y, z));
                vertices[offset + 2] = matrix.MultiplyPoint3x4(source.center + new Vector3(half.x, half.y, z));
                vertices[offset + 3] = matrix.MultiplyPoint3x4(source.center + new Vector3(-half.x, half.y, z));
            }
            float3 x = vertices[1] - vertices[0], y = vertices[3] - vertices[0], zAxis = vertices[4] - vertices[0];
            if (math.lengthsq(x) < 1e-16f || math.lengthsq(y) < 1e-16f || math.lengthsq(zAxis) < 1e-16f)
                throw new InvalidOperationException($"Map box has a collapsed transform: {SourceKey(source)}");
            float3 nx = math.normalize(x), ny = math.normalize(y), nz = math.normalize(zAxis);
            if (math.abs(math.dot(nx, ny)) > 1e-5f || math.abs(math.dot(nx, nz)) > 1e-5f || math.abs(math.dot(ny, nz)) > 1e-5f)
                throw new NotSupportedException($"Sheared map BoxCollider needs an explicit collision mesh before baking: {SourceKey(source)}");
            if (SingleAxis(x) && SingleAxis(y) && SingleAxis(zAxis))
            {
                float3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                foreach (var vertex in vertices) { min = math.min(min, vertex); max = math.max(max, vertex); }
                builder.AddBox((min + max) * .5f, (max - min) * .5f, staticFriction: sf, dynamicFriction: df);
            }
            else builder.AddConvex(vertices, BoxTriangles, staticFriction: sf, dynamicFriction: df);
        }

        static bool SingleAxis(float3 vector)
        {
            float maximum = math.cmax(math.abs(vector));
            if (maximum < 1e-8f) return false;
            // Only discard transform roundoff; a genuinely rotated/thin box remains a convex solid.
            return math.csum(math.select(new int3(0), new int3(1), math.abs(vector) > maximum * 1e-7f)) == 1;
        }

        static void AddMesh(BlockMapCollisionBuilder builder, MeshCollider source, float sf, float df)
        {
            var mesh = source.sharedMesh;
            if (mesh == null) throw new InvalidOperationException($"Map MeshCollider has no mesh: {SourceKey(source)}");
            if (!mesh.isReadable) throw new InvalidOperationException($"Map collision bake requires readable mesh data: {SourceKey(source)}");
            var local = mesh.vertices; var vertices = new float3[local.Length]; var transform = source.transform.localToWorldMatrix;
            for (int i = 0; i < local.Length; i++) vertices[i] = transform.MultiplyPoint3x4(local[i]);
            var triangles = mesh.triangles;
            if (source.convex) builder.AddConvex(vertices, triangles, staticFriction: sf, dynamicFriction: df);
            else for (int i = 0; i < triangles.Length; i += 3)
                builder.AddTriangle(vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]], staticFriction: sf, dynamicFriction: df);
        }

        static byte[] Signature(Collider source)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(SourceKey(source)); writer.Write(source.GetType().FullName ?? "");
            writer.Write(source.enabled); writer.Write(source.isTrigger); writer.Write(source.gameObject.activeSelf);
            writer.Write(source.gameObject.activeInHierarchy); writer.Write(source.gameObject.layer); writer.Write(source.contactOffset);
            var matrix = source.transform.localToWorldMatrix; for (int i = 0; i < 16; i++) writer.Write(matrix[i]);
            var material = source.sharedMaterial; writer.Write(material != null);
            writer.Write(material != null ? material.staticFriction : .6f); writer.Write(material != null ? material.dynamicFriction : .6f);
            writer.Write(material != null ? material.bounciness : 0);
            writer.Write(material != null ? (int)material.frictionCombine : (int)PhysicsMaterialCombine.Average);
            writer.Write(material != null ? (int)material.bounceCombine : (int)PhysicsMaterialCombine.Average);
            if (source is BoxCollider box)
            { BlockMapCollisionBuilder.Write(writer, box.center); BlockMapCollisionBuilder.Write(writer, box.size); }
            else if (source is MeshCollider meshCollider)
            {
                writer.Write(meshCollider.convex); writer.Write((int)meshCollider.cookingOptions);
                var mesh = meshCollider.sharedMesh; writer.Write(mesh != null);
                if (mesh != null)
                {
                    if (!mesh.isReadable) throw new InvalidOperationException($"Map collision source is not readable: {SourceKey(source)}");
                    writer.Write(mesh.vertexCount); foreach (var v in mesh.vertices) BlockMapCollisionBuilder.Write(writer, v);
                    // Index topology must invalidate a bake even when the same vertices are reused.
                    var indices = mesh.triangles; writer.Write(indices.Length); foreach (int index in indices) writer.Write(index);
                    writer.Write(mesh.subMeshCount);
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    { writer.Write((int)mesh.GetTopology(sub)); writer.Write((long)mesh.GetIndexStart(sub)); writer.Write((long)mesh.GetIndexCount(sub)); writer.Write((long)mesh.GetBaseVertex(sub)); }
                }
            }
            else if (source is SphereCollider sphere)
            { BlockMapCollisionBuilder.Write(writer, sphere.center); writer.Write(sphere.radius); }
            else if (source is CapsuleCollider capsule)
            { BlockMapCollisionBuilder.Write(writer, capsule.center); writer.Write(capsule.radius); writer.Write(capsule.height); writer.Write(capsule.direction); }
            writer.Flush(); return stream.ToArray();
        }

        static string SourceKey(Collider source)
        {
            var components = source.GetComponents<Collider>(); int component = Array.IndexOf(components, source);
            return Path(source.transform) + "/Collider:" + component;
        }
        static string Path(Transform value)
            => value.parent != null ? Path(value.parent) + "/" + value.name + ":" + value.GetSiblingIndex() : value.name + ":" + value.GetSiblingIndex();
    }
}
