#if UNITY_EDITOR || DEVELOPMENT_BUILD
// All producers run on Unity's main thread.
using System;
using UnityEngine;

namespace Splatoon.Combat
{
    /// <summary>Opt-in counters only: no cache lookup, eviction, pose evaluation,
    /// collision query, full-key export or production decision is performed here.</summary>
    public static class PaperSamplingDiagnostics
    {
        public const int Schema = 1;
        const int FrameHistory = 8;
        public enum Event
        {
            TraversalLocalHit, TraversalLocalMiss, QueryGlobalHit, LiveGlobalHit,
            LiveNoop, QueryRealMiss, LiveRealMiss, HandoffHit, HintReject, DeferredRenderBake
        }
        [Serializable] public struct Counts
        {
            public long traversalRequests, localHit, localMiss, globalHit, queryGlobalHit,
                liveNoop, realMiss, queryRealMiss, handoffHit, hintReject, deferredRenderBake;

            internal void Add(Event value)
            {
                switch (value)
                {
                    case Event.TraversalLocalHit: traversalRequests++; localHit++; break;
                    case Event.TraversalLocalMiss: traversalRequests++; localMiss++; break;
                    case Event.QueryGlobalHit: globalHit++; queryGlobalHit++; break;
                    case Event.LiveGlobalHit: globalHit++; break;
                    case Event.LiveNoop: liveNoop++; break;
                    case Event.QueryRealMiss: realMiss++; queryRealMiss++; break;
                    case Event.LiveRealMiss: realMiss++; break;
                    case Event.HandoffHit: handoffHit++; break;
                    case Event.HintReject: hintReject++; break;
                    case Event.DeferredRenderBake: deferredRenderBake++; break;
                }
            }
            public static Counts Sum(Counts a, Counts b) => new Counts
            {
                traversalRequests = a.traversalRequests + b.traversalRequests,
                localHit = a.localHit + b.localHit, localMiss = a.localMiss + b.localMiss,
                globalHit = a.globalHit + b.globalHit, queryGlobalHit = a.queryGlobalHit + b.queryGlobalHit,
                liveNoop = a.liveNoop + b.liveNoop, realMiss = a.realMiss + b.realMiss,
                queryRealMiss = a.queryRealMiss + b.queryRealMiss, handoffHit = a.handoffHit + b.handoffHit,
                hintReject = a.hintReject + b.hintReject, deferredRenderBake = a.deferredRenderBake + b.deferredRenderBake
            };
            public bool IsMonotonicFrom(Counts previous)
                => traversalRequests >= previous.traversalRequests && localHit >= previous.localHit && localMiss >= previous.localMiss
                && globalHit >= previous.globalHit && queryGlobalHit >= previous.queryGlobalHit && liveNoop >= previous.liveNoop
                && realMiss >= previous.realMiss && queryRealMiss >= previous.queryRealMiss && handoffHit >= previous.handoffHit
                && hintReject >= previous.hintReject && deferredRenderBake >= previous.deferredRenderBake;
            public bool HasValidPartitions()
                => traversalRequests >= 0 && localHit >= 0 && localMiss >= 0 && globalHit >= 0 && queryGlobalHit >= 0
                && liveNoop >= 0 && realMiss >= 0 && queryRealMiss >= 0 && handoffHit >= 0 && hintReject >= 0 && deferredRenderBake >= 0
                && traversalRequests == localHit + localMiss && queryGlobalHit <= globalHit && queryRealMiss <= realMiss
                && hintReject <= realMiss - queryRealMiss;
        }
        [Serializable] public struct FrameSnapshot
        {
            public int schema, frameId;
            public uint generation;
            public bool enabled, available;
            public Counts cumulative, delta;
        }
        struct Bucket
        {
            public bool initialized;
            public int frameId;
            public uint generation;
            public Counts cumulative, delta;
        }
        static readonly Bucket[] Frames = new Bucket[FrameHistory];
        static Counts _cumulative;
        static uint _generation;
        public static bool Enabled { get; private set; }
        public static uint Generation => _generation;
        public static Counts ReadCumulative() => _cumulative;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Enabled = false; unchecked { _generation++; }
            _cumulative = default; Array.Clear(Frames, 0, Frames.Length);
        }
        // Enable once from the opt-in fixed probe. Enabling and capture/cache
        // disposal do NOT reset this stream; its cumulative values stay monotonic.
        public static void Enable() => Enabled = true;
        static ref Bucket Current()
        {
            int frame = Time.frameCount;
            ref var slot = ref Frames[(frame & int.MaxValue) % Frames.Length];
            if (!slot.initialized || slot.frameId != frame || slot.generation != _generation)
                slot = new Bucket { initialized = true, frameId = frame, generation = _generation, cumulative = _cumulative };
            return ref slot;
        }
        // Call in every probe LateUpdate before any stage/finished early return,
        // so retained zero-event frames have an explicit available zero bucket.
        public static void TouchFrame()
        {
            if (!Enabled) return;
            Current();
        }
        public static void Record(Event value)
        {
            if (!Enabled) return;
            ref var slot = ref Current();
            _cumulative.Add(value); slot.delta.Add(value); slot.cumulative = _cumulative;
        }
        // Read by the *completed* FrameRecord.frameId. Do not subtract a current
        // next-Update cumulative value: next-frame FixedUpdate may already run.
        // Render events after LateUpdate update their ORIGINAL Time.frameCount bucket.
        public static FrameSnapshot ReadFrame(int frameId)
        {
            ref var slot = ref Frames[(frameId & int.MaxValue) % Frames.Length];
            bool available = Enabled && slot.initialized && slot.frameId == frameId && slot.generation == _generation;
            return new FrameSnapshot { schema = Schema, frameId = frameId, generation = _generation,
                enabled = Enabled, available = available, cumulative = available ? slot.cumulative : default,
                delta = available ? slot.delta : default };
        }
    }
}
#endif
