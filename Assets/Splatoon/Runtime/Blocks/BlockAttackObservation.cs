#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Splatoon.Prototype;

namespace Splatoon.Blocks
{
    /// <summary>Opt-in wall clock observations of real authoritative block impacts.</summary>
    public static class BlockAttackObservation
    {
        public readonly struct Collision
        {
            public readonly uint Round, Entity;
            public readonly ulong Owner;
            public readonly string Kind;
            public readonly int Count;
            public readonly double Milliseconds;
            public Collision(BlockAttackToken token, uint entity, string kind, int count, long contact)
            {
                Round = token.Credit.Round; Owner = token.Credit.Owner; Entity = entity;
                Kind = kind; Count = count;
                Milliseconds = (Stopwatch.GetTimestamp() - contact) * 1000d / Stopwatch.Frequency;
            }
        }
        public readonly struct Blast
        {
            public readonly long Sequence, PhysicsTick, Started;
            public readonly uint NetworkTick, Round, Entity, Ordinal;
            public readonly ulong Owner;
            public readonly double ServerTime;
            public readonly string Kind;
            public Blast(BlockAttackToken token, uint entity, uint ordinal, long sequence)
            {
                var match = PrototypeMatch.Current;
                Sequence = sequence; Started = Stopwatch.GetTimestamp();
                PhysicsTick = (long)Math.Round(UnityEngine.Time.fixedTimeAsDouble / UnityEngine.Time.fixedDeltaTime);
                NetworkTick = (uint)match.NetworkManager.ServerTime.Tick;
                ServerTime = match.NetworkManager.ServerTime.Time;
                Round = token.Credit.Round; Owner = token.Credit.Owner; Entity = entity; Ordinal = ordinal;
                Kind = token.Credit.Kind.ToString();
            }
            public double ElapsedMilliseconds => Started == 0 ? 0 : (Stopwatch.GetTimestamp() - Started) * 1000d / Stopwatch.Frequency;
        }
        static readonly Dictionary<(BlockAttackToken token, uint entity), long> Contacts = new();
        static long _blastSequence;
        public static event Action<Collision> CollisionEffective;
        public static event Action<Blast, int> BlastQueued;
        public static event Action<Blast, int> BlastCompleted;

        public static void Contact(BlockAttackToken token, uint entity)
        {
            if (CollisionEffective == null || token == null || entity == 0) return;
            Contacts[(token, entity)] = Stopwatch.GetTimestamp();
        }
        public static long ContactStamp(BlockAttackToken token, uint entity)
            => token != null && Contacts.TryGetValue((token, entity), out long stamp) ? stamp : 0;
        public static void Effective(BlockAttackToken token, uint entity, string kind, int count, long stamp)
        {
            if (stamp != 0 && count > 0) CollisionEffective?.Invoke(new Collision(token, entity, kind, count, stamp));
        }
        public static Blast BeginBlast(BlockAttackToken token, uint entity, uint ordinal)
            => BlastQueued != null || BlastCompleted != null ? new Blast(token, entity, ordinal, ++_blastSequence) : default;
        public static void Queued(Blast observation, int removed)
        { if (observation.Started != 0) BlastQueued?.Invoke(observation, removed); }
        public static void Completed(Blast observation, int removed)
        { if (observation.Started != 0) BlastCompleted?.Invoke(observation, removed); }
        public static void Clear() => Contacts.Clear();
    }
}
#endif
