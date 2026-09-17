using System;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Painting;

namespace Splatoon.Networking
{
    public enum NetworkTrafficKind { Input, Shots, Impacts, LivePaint, SnapshotJournal, SnapshotData, PlayerDelta, Count }
    public static class NetworkTrafficCounters
    {
        static readonly long[] Bytes = new long[(int)NetworkTrafficKind.Count];
        static readonly long[] Messages = new long[(int)NetworkTrafficKind.Count];
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset() { Array.Clear(Bytes, 0, Bytes.Length); Array.Clear(Messages, 0, Messages.Length); }
        public static void Record(NetworkTrafficKind kind, int bytes)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Bytes[(int)kind] += bytes; Messages[(int)kind]++;
#endif
        }
        public static long GetBytes(NetworkTrafficKind kind) => Bytes[(int)kind];
        public static long GetMessages(NetworkTrafficKind kind) => Messages[(int)kind];
    }
    internal static class NetworkBatchTraffic<T>
    {
        public static readonly NetworkTrafficKind Kind = typeof(T) == typeof(PlayerInputFrame) ? NetworkTrafficKind.Input :
            (typeof(T) == typeof(InkShot) || typeof(T) == typeof(InkBubbleState)) ? NetworkTrafficKind.Shots : typeof(T) == typeof(InkImpact) || typeof(T) == typeof(InkExplosionEvent) || typeof(T) == typeof(InkBounce) ? NetworkTrafficKind.Impacts :
            typeof(T) == typeof(PaintStamp) ? NetworkTrafficKind.LivePaint : throw new InvalidOperationException("Unclassified network batch.");
    }
}
