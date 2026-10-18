#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        [Serializable] public sealed class VisualReport
        {
            public NativePhysicsInventoryReport nativePhysicsInventory;
            public string source = "normal main-camera backbuffer; actual world BoxCollider axes; submitted mesh bounds and first instance matrix of every observed draw batch; published surface mesh vertices and collider top faces";
            public string error, screenshotPath, screenshotSha256, cameraName;
            public bool complete, valid, shaderSupported = true, instancingSupported, cameraActive, cameraTargetTextureNull;
            public int colliderSamples, drawSamples, drawInstances, surfacePublishedCells, surfaceGridSamples, surfaceTopSamples;
            public int visibleSurface, visibleDynamic, screenshotWidth, screenshotHeight, screenshotNonBlackPixels, screenshotDistinctColors;
            public float[] colliderEdgeMin = { float.MaxValue, float.MaxValue, float.MaxValue };
            public float[] colliderEdgeMax = new float[3];
            public float[] drawEdgeMin = { float.MaxValue, float.MaxValue, float.MaxValue };
            public float[] drawEdgeMax = new float[3];
            public double surfaceGridMaxError, surfaceTopMaxError, screenshotCapturedAt;
        }
        sealed partial class Report { public VisualReport visual; }
        readonly List<Vector3Int> _visualCells = new();
        readonly List<Vector3> _visualSurfacePoints = new();
        BlockDynamicWorld _visualWorld;
        bool _visualPrepared, _visualCapturing, _visualAimReady;
        Vector3 _visualAim;
        double _visualAt;

        void InitializeVisual() => _report.visual = new VisualReport();
        void VisualStageChanged(string stage)
        {
            StopVisualDrawObservation();
            if (stage != "visual-proof") return;
            _report.visual = new VisualReport();
            _visualPrepared = _visualCapturing = _visualAimReady = false;
            _visualAt = Time.realtimeSinceStartupAsDouble;
        }
        void StopVisualDrawObservation()
        {
            if (_visualWorld == null) return;
            _visualWorld.CaptureGeometryEvidence = false;
            _visualWorld.GeometryDrawObserved -= VisualDraw;
            _visualWorld = null;
        }
        void DisposeVisual() => StopVisualDrawObservation();
        void VisualInput(PrototypePlayer player, PlayerSnapshot state, ref PlayerInputFrame input)
        {
            // Camera aiming uses the same ordinary look input, with no actor or camera teleport.
            if (!_visualAimReady) return;
            Vector2 look = player.Look;
            Vector3 pivot = state.Position + PrototypePlayer.CameraPivotOffset(state, player.Presentation);
            for (int i = 0; i < 4; i++)
            {
                Vector3 origin = PrototypePlayer.CameraPosition(pivot, Quaternion.Euler(look.y, look.x, 0), player.Presentation);
                Vector3 delta = _visualAim - origin;
                look = new Vector2(Mathf.Atan2(delta.x, delta.z) * Mathf.Rad2Deg,
                    Mathf.Clamp(-Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg, -65, 75));
            }
            input.Look = look;
        }
        void ObserveVisual(PrototypeMatch match, double now)
        {
            if (_stage != "visual-proof" || _report.visual.complete || _visualCapturing) return;
            if (_visualWorld != match.Blocks.Dynamic)
            {
                StopVisualDrawObservation(); _visualWorld = match.Blocks.Dynamic;
                _visualWorld.GeometryDrawObserved += VisualDraw;
                _visualWorld.CaptureGeometryEvidence = true;
            }
            if (!_visualPrepared && now - _visualAt >= .25 && match.InitialSyncComplete)
            {
                _visualPrepared = true;
                try { SampleVisualGeometry(match.Blocks); ChooseVisualAim(PrototypePlayer.Local.PresentedState); }
                catch (Exception exception) { _report.visual.error = exception.ToString(); }
            }
            // All readback, world iteration and PNG work occurs outside the timed combat stage.
            if (_visualPrepared && now - _visualAt >= 2)
            { _visualCapturing = true; StartCoroutine(CaptureVisualFrame()); }
        }
        static void IncludeEdges(float[] minimum, float[] maximum, Vector3 size)
        {
            for (int axis = 0; axis < 3; axis++)
            { minimum[axis] = Mathf.Min(minimum[axis], size[axis]); maximum[axis] = Mathf.Max(maximum[axis], size[axis]); }
        }
        void VisualDraw(Mesh mesh, Matrix4x4 matrix, int count, bool supported)
        {
            if (_stage != "visual-proof" || _report.visual.complete || mesh == null || count <= 0) return;
            var visual = _report.visual; Vector3 size = mesh.bounds.size;
            IncludeEdges(visual.drawEdgeMin, visual.drawEdgeMax, new Vector3(
                matrix.MultiplyVector(Vector3.right * size.x).magnitude,
                matrix.MultiplyVector(Vector3.up * size.y).magnitude,
                matrix.MultiplyVector(Vector3.forward * size.z).magnitude));
            visual.drawSamples++; visual.drawInstances += count; visual.shaderSupported &= supported;
            visual.instancingSupported = SystemInfo.supportsInstancing;
        }
        void SampleVisualGeometry(BlockTerrain terrain)
        {
            var visual = _report.visual;
            if (visual.nativePhysicsInventory == null) visual.nativePhysicsInventory = CaptureNativePhysicsInventory();
            foreach (var block in terrain.Dynamic.Bodies.Values)
            {
                var box = block.Collider;
                if (box == null || !box.enabled || !box.gameObject.activeInHierarchy) continue;
                // Rotated AABB extents are not cube edge lengths: transform each actual local box axis.
                IncludeEdges(visual.colliderEdgeMin, visual.colliderEdgeMax, new Vector3(
                    box.transform.TransformVector(Vector3.right * box.size.x).magnitude,
                    box.transform.TransformVector(Vector3.up * box.size.y).magnitude,
                    box.transform.TransformVector(Vector3.forward * box.size.z).magnitude));
                visual.colliderSamples++;
            }
            _visualCells.Clear(); _visualSurfacePoints.Clear();
            foreach (var pair in terrain.World.Chunks)
                for (int i = 0; i < pair.Value.Cells.Length; i++)
                {
                    var cell = pair.Value.Cells[i]; if (!cell.Occupied || !cell.SurfaceAttached) continue;
                    var coordinate = BlockWorld.CellFromIndex(pair.Key, i);
                    _visualCells.Add(coordinate); _visualSurfacePoints.Add(BlockWorld.Center(coordinate));
                }
            visual.surfacePublishedCells = _visualCells.Count;
            // Only active published chunk objects carry accepted render and collision meshes.
            foreach (var filter in terrain.GetComponentsInChildren<MeshFilter>(false))
            {
                if (filter.gameObject.layer != BlockTerrain.TerrainLayer || !filter.name.StartsWith("Block chunk ", StringComparison.Ordinal)) continue;
                var collider = filter.GetComponent<MeshCollider>(); var renderer = filter.GetComponent<MeshRenderer>();
                if (collider == null || !collider.enabled || collider.sharedMesh == null || renderer == null || !renderer.enabled) continue;
                SamplePublishedMesh(filter.sharedMesh, filter.transform);
                if (collider.sharedMesh != filter.sharedMesh) SamplePublishedMesh(collider.sharedMesh, collider.transform);
            }
            int topCandidates = 0;
            foreach (var coordinate in _visualCells)
            {
                if (terrain.World.TryGet(coordinate + Vector3Int.up, out _)) continue;
                if (++topCandidates > 256) break;
                Vector3 center = BlockWorld.Center(coordinate);
                float expectedTop = (coordinate.y + 1) * BlockWorld.CellSize;
                var origin = new Vector3(center.x, expectedTop + .3f, center.z);
                if (!Physics.Raycast(origin, Vector3.down, out var hit, .8f, 1 << BlockTerrain.TerrainLayer, QueryTriggerInteraction.Ignore) ||
                    hit.normal.y < .99f || !hit.collider.name.StartsWith("Block chunk ", StringComparison.Ordinal)) continue;
                visual.surfaceTopSamples++;
                double actualHeightAboveCellBase = hit.point.y - coordinate.y * (double)BlockWorld.CellSize;
                visual.surfaceTopMaxError = Math.Max(visual.surfaceTopMaxError, Math.Abs(actualHeightAboveCellBase - BlockWorld.CellSize));
            }
        }
        void SamplePublishedMesh(Mesh mesh, Transform transform)
        {
            if (mesh == null) return;
            var visual = _report.visual;
            foreach (var vertex in mesh.vertices)
            {
                Vector3 world = transform.TransformPoint(vertex);
                for (int axis = 0; axis < 3; axis++)
                    visual.surfaceGridMaxError = Math.Max(visual.surfaceGridMaxError,
                        Math.Abs(world[axis] - Math.Round(world[axis] / (double)BlockWorld.CellSize) * BlockWorld.CellSize));
                visual.surfaceGridSamples++;
            }
        }
        void ChooseVisualAim(PlayerSnapshot state)
        {
            Vector3 feet = PlayerMotorSimulation.HumanPosition(state);
            BlockDynamicBody nearest = null; float best = float.PositiveInfinity;
            foreach (var body in _visualWorld.Bodies.Values)
            {
                float distance = (body.Body.position - feet).sqrMagnitude;
                if (distance >= best) continue; best = distance; nearest = body;
            }
            if (nearest == null)
            {
                foreach (var point in _visualSurfacePoints)
                {
                    float distance = (point - feet).sqrMagnitude;
                    if (distance >= best) continue; best = distance; _visualAim = point; _visualAimReady = true;
                }
                return;
            }
            _visualAim = nearest.Body.position; _visualAimReady = true;
            // Prefer framing an existing nearby coating patch together with the closest real free body.
            Vector3 surface = default; best = float.PositiveInfinity;
            foreach (var point in _visualSurfacePoints)
            {
                float distance = (point - _visualAim).sqrMagnitude;
                if (distance >= best) continue; best = distance; surface = point;
            }
            if (best < 36) _visualAim = (_visualAim + surface) * .5f;
        }
        static bool VisualPointVisible(Camera camera, Vector3 point, float radius)
        {
            var viewport = camera.WorldToViewportPoint(point);
            if (viewport.z <= camera.nearClipPlane || viewport.z >= camera.farClipPlane || viewport.x <= 0 || viewport.x >= 1 || viewport.y <= 0 || viewport.y >= 1) return false;
            Vector3 direction = point - camera.transform.position; float distance = direction.magnitude;
            int mask = ~((1 << 8) | (1 << 10));
            return !Physics.Raycast(camera.transform.position, direction.normalized, out var hit,
                Mathf.Max(.001f, distance - radius), mask, QueryTriggerInteraction.Ignore);
        }
        void SampleVisualVisibility(Camera camera)
        {
            var visual = _report.visual; visual.visibleSurface = visual.visibleDynamic = 0;
            foreach (var point in _visualSurfacePoints)
                if (VisualPointVisible(camera, point, BlockWorld.CellSize * .88f)) visual.visibleSurface++;
            foreach (var body in _visualWorld.Bodies.Values)
                if (body.Collider.enabled && VisualPointVisible(camera, body.Body.position, body.Collider.bounds.extents.magnitude)) visual.visibleDynamic++;
        }
        IEnumerator CaptureVisualFrame()
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D texture = null;
            var visual = _report.visual;
            try
            {
                var camera = Camera.main;
                visual.cameraActive = camera != null && camera.isActiveAndEnabled;
                visual.cameraTargetTextureNull = camera != null && camera.targetTexture == null;
                visual.cameraName = camera != null ? camera.name : "missing";
                _report.width = Screen.width; _report.height = Screen.height;
                _report.normalMainCamera = visual.cameraActive && visual.cameraTargetTextureNull;
                if (!_report.normalMainCamera) throw new InvalidOperationException("Visual proof requires the active normal main camera and its original backbuffer.");
                SampleVisualVisibility(camera);
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null) throw new InvalidOperationException("Native backbuffer capture returned no texture.");
                visual.screenshotWidth = texture.width; visual.screenshotHeight = texture.height;
                var colors = new HashSet<int>();
                foreach (var pixel in texture.GetPixels32())
                {
                    int rgb = pixel.r << 16 | pixel.g << 8 | pixel.b;
                    if (rgb != 0) visual.screenshotNonBlackPixels++;
                    colors.Add(rgb);
                }
                visual.screenshotDistinctColors = colors.Count;
                byte[] png = texture.EncodeToPNG();
                visual.screenshotPath = Path.ChangeExtension(_output, ".visual.png");
                Directory.CreateDirectory(Path.GetDirectoryName(visual.screenshotPath));
                File.WriteAllBytes(visual.screenshotPath, png);
                visual.screenshotSha256 = Digest(png); visual.screenshotCapturedAt = Time.realtimeSinceStartupAsDouble;
            }
            catch (Exception exception)
            { visual.error = string.IsNullOrEmpty(visual.error) ? exception.ToString() : visual.error + "\n" + exception; }
            finally
            {
                if (texture != null) Destroy(texture);
                StopVisualDrawObservation();
                visual.complete = true; visual.valid = VisualEvidenceValid(visual);
                if (!visual.valid && string.IsNullOrEmpty(visual.error)) visual.error = "Visual geometry, visibility, graphics or backbuffer evidence did not meet the acceptance gates.";
                _nextWrite = Time.realtimeSinceStartupAsDouble;
                if (_match != null && _match.IsSpawned) Capture(_match);
                Write();
            }
        }
        bool VisualEvidenceValid(VisualReport visual)
        {
            if (!string.IsNullOrEmpty(visual.error) || !_report.graphics || _report.graphicsApi != "Direct3D12" ||
                _report.width != 1920 || _report.height != 1080 || !visual.cameraActive || !visual.cameraTargetTextureNull ||
                visual.colliderSamples <= 0 || visual.drawSamples <= 0 || visual.drawInstances <= 0 || !visual.shaderSupported || !visual.instancingSupported ||
                visual.screenshotWidth != 1920 || visual.screenshotHeight != 1080 || visual.screenshotNonBlackPixels <= 0 || visual.screenshotDistinctColors <= 1 ||
                visual.visibleSurface + visual.visibleDynamic <= 0) return false;
            if (_match != null && _match.IsServer && (visual.visibleSurface <= 0 || visual.visibleDynamic <= 0 || visual.surfacePublishedCells <= 0 ||
                visual.surfaceGridSamples <= 0 || visual.surfaceGridMaxError > .00001 || visual.surfaceTopSamples <= 0 || visual.surfaceTopMaxError > .00001)) return false;
            for (int axis = 0; axis < 3; axis++)
                if (Math.Abs(visual.colliderEdgeMin[axis] - BlockWorld.CellSize) > .00001 || Math.Abs(visual.colliderEdgeMax[axis] - BlockWorld.CellSize) > .00001 ||
                    Math.Abs(visual.drawEdgeMin[axis] - BlockWorld.CellSize) > .00001 || Math.Abs(visual.drawEdgeMax[axis] - BlockWorld.CellSize) > .00001) return false;
            return true;
        }
    }
}
#endif
