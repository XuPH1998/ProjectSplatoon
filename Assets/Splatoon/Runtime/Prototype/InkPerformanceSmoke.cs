using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Unity.Profiling;

namespace Splatoon.Prototype
{
    // Enabled only by the explicit development-only inkperf smoke case.
    public static class InkPerformanceSmoke
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        static PrototypeMatch _match;
        static readonly List<float> Frames=new(16384);
        static ProfilerRecorder _allocations;
        static RenderTexture _target;
        static readonly UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest RenderRequest=new();
        static double _begin;
        static long _gcBytes,_gcPeak;
        static int _pendingPeak,_bufferedPeak,_journalPeak,_transferPeak;
        static bool _reported;
        public static void Tick(PrototypeMatch match)
        {
            if(match.State.Value.PlayerCount<4)return;
            if(_match!=match)
            {
                _match=match;Frames.Clear();_gcBytes=_gcPeak=0;_reported=false;_begin=Time.realtimeSinceStartupAsDouble;
                _pendingPeak=_bufferedPeak=_journalPeak=_transferPeak=0;
                _allocations.Dispose();_allocations=ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC Allocated In Frame");
                if(SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    _target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32){name="Ink performance frame"};_target.Create();RenderRequest.destination=_target;
                }
                Debug.Log("[INK-PERF] Four-player capture starting; development fixture replenishes ink only.");
            }
            if(match.IsServer) foreach(var player in match.Players)
            {
                var state=player.Snapshot.Value;
                if(state.Ink<90){state.Ink=100;player.Snapshot.Value=state;}
            }
            double age=Time.realtimeSinceStartupAsDouble-_begin;
            // Hidden Windows windows may skip their backbuffer. Render an actual 720p scene
            // every measured frame so the benchmark includes ink materials and particles.
            if(!_reported&&_target!=null&&Camera.main!=null)UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(Camera.main,RenderRequest);
            if(age<5||_reported)return;
            Frames.Add(Time.unscaledDeltaTime*1000);
            _pendingPeak=Math.Max(_pendingPeak,match.DiagnosticPendingStamps);_bufferedPeak=Math.Max(_bufferedPeak,match.DiagnosticBufferedStamps);
            _journalPeak=Math.Max(_journalPeak,match.DiagnosticJournalStamps);_transferPeak=Math.Max(_transferPeak,match.DiagnosticTransfers);
            if(_allocations.Valid){long gc=_allocations.LastValue;_gcBytes+=gc;_gcPeak=Math.Max(_gcPeak,gc);}
            if(age<45)return;
            _reported=true;bool valid=_allocations.Valid;_allocations.Dispose();ReleaseTarget();
            float[] times=Frames.OrderBy(x=>x).ToArray();float At(float percentile)=>times[Math.Min(times.Length-1,(int)(times.Length*percentile))];
            string result=FormattableString.Invariant($"{{\"frames\":{times.Length},\"p50Ms\":{At(.5f):F3},\"p95Ms\":{At(.95f):F3},\"p99Ms\":{At(.99f):F3},\"maxMs\":{times[^1]:F3},\"gcRecorderValid\":{valid.ToString().ToLowerInvariant()},\"gcTotalBytes\":{_gcBytes},\"gcMaxFrameBytes\":{_gcPeak},\"paintRtBytes\":{Painting.PaintSurface.AllocatedBytes}}}");
            result=result.TrimEnd('}')+FormattableString.Invariant($",\"pendingPeak\":{_pendingPeak},\"bufferedPeak\":{_bufferedPeak},\"journalPeak\":{_journalPeak},\"transferPeak\":{_transferPeak},\"finalSequence\":{match.AppliedPaintSequence}}}");
            string label=match.IsServer?"host":"client-"+match.NetworkManager.LocalClientId;
            string output = Path.Combine(Application.dataPath, "..", "Reports", "InkPerformance");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"ink-performance-"+label+".json"),result);Debug.Log("[INK-PERF] "+result);
        }
        static void ReleaseTarget(){if(_target==null)return;_target.Release();UnityEngine.Object.Destroy(_target);_target=null;RenderRequest.destination=null;}
        public static void Reset(){_allocations.Dispose();ReleaseTarget();_match=null;Frames.Clear();}
#endif
    }
}
