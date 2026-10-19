using Unity.Profiling;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Allocation-free counters shared by the opt-in Player probe and profiler.</summary>
    public static class FramePerformance
    {
        public static readonly ProfilerMarker PaintCpu = new("Splatoon.Paint.Cpu");
        public static readonly ProfilerMarker PaintSubmit = new("Splatoon.Paint.Submit");
        public static readonly ProfilerMarker Flight = new("Splatoon.Flight.Update");
        public static readonly ProfilerMarker Muzzle = new("Splatoon.Muzzle.Emit");
        public static readonly ProfilerMarker PaperSample = new("Splatoon.Paper.Sample");
        public static readonly ProfilerMarker PaperPoseEvaluate = new("Splatoon.Paper.PoseEvaluate");
        public static readonly ProfilerMarker PaperSkinBake = new("Splatoon.Paper.SkinBake");
        public static readonly ProfilerMarker PaperMeshRead = new("Splatoon.Paper.MeshRead");
        public static readonly ProfilerMarker PaperContour = new("Splatoon.Paper.Contour");
        public static readonly ProfilerMarker PaperHitMesh = new("Splatoon.Paper.HitMesh");
        public static readonly ProfilerMarker PaperHitMeshRestore = new("Splatoon.Paper.HitMeshRestore");
        public static readonly ProfilerMarker PaperHitColliderCook = new("Splatoon.Paper.HitColliderCook");
        public static readonly ProfilerMarker PaperRender = new("Splatoon.Paper.Render");
        public static readonly ProfilerMarker Checkpoint = new("Splatoon.Paint.CheckpointCopy");
        // Reading this field initializes all markers before the probe opens its recorders.
        internal static readonly string[] MarkerNames = { "Splatoon.Paint.Cpu", "Splatoon.Paint.Submit", "Splatoon.Flight.Update",
            "Splatoon.Muzzle.Emit", "Splatoon.Paper.Sample", "Splatoon.Paper.PoseEvaluate", "Splatoon.Paper.SkinBake",
            "Splatoon.Paper.MeshRead", "Splatoon.Paper.Contour", "Splatoon.Paper.HitMesh", "Splatoon.Paper.HitMeshRestore", "Splatoon.Paper.HitColliderCook",
            "Splatoon.Paper.Render", "Splatoon.Paint.CheckpointCopy" };
        public static long PaintStamps, PaintDraws, PaintCopies, PaintSubmissions, MuzzleParticles;
        public static long CompositePasses, SkippedComposites;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            PaintStamps = PaintDraws = PaintCopies = PaintSubmissions = MuzzleParticles = 0;
            CompositePasses = SkippedComposites = 0;
        }
    }
}
