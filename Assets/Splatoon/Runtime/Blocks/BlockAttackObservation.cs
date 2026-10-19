#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Diagnostics;
using Splatoon.Prototype;

namespace Splatoon.Blocks
{
    /// <summary>Opt-in wall clock observations of real authoritative block impacts.</summary>
    public static partial class BlockAttackObservation
    {
        public readonly struct Collision
        {
            public readonly Source Source;
            public readonly uint Round, Entity, Ordinal, Epoch, Revision, SurfaceRevision, NetworkTick;
            public readonly ulong Owner;
            public readonly string Kind;
            public readonly int Count;
            public readonly double Milliseconds, ServerTime;
            public readonly bool Auxiliary, HadContact;
            public Collision(BlockAttackToken token, uint entity, string kind, int count, long contact,
                uint ordinal = 0, bool auxiliary = false, bool hadContact = false)
            {
                Source = token.ObservationSource; Ordinal = ordinal; Auxiliary = auxiliary; HadContact = hadContact;
                Round = token.Credit.Round; Owner = token.Credit.Owner; Entity = entity;
                var terrain = BlockTerrain.Current;
                Epoch = terrain?.FixedBlocks?.Epoch ?? 0;
                Revision = terrain?.FixedBlocks?.Revision ?? terrain?.World?.Revision ?? 0;
                SurfaceRevision = terrain?.SurfacePaints?.Revision ?? 0;
                Clock(out double now, out uint tick); ServerTime = now; NetworkTick = tick;
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
        static long _contactEpoch;
        static long _blastSequence;
        public static event Action<Collision> CollisionEffective;
        public static event Action<Blast, int> BlastQueued;
        public static event Action<Blast, int> BlastCompleted;

        public static void Contact(BlockAttackToken token, uint entity)
        {
            if ((CollisionEffective == null && !HasActualObservers) || token == null || entity == 0) return;
            token.ObservationContactEntity = entity;
            token.ObservationContactEpoch = _contactEpoch;
            token.ObservationContactStamp = Stopwatch.GetTimestamp();
        }
        public static long ContactStamp(BlockAttackToken token, uint entity)
        {
            if (token == null || token.ObservationContactEpoch != _contactEpoch || token.ObservationContactEntity != entity) return 0;
            long stamp = token.ObservationContactStamp;
            token.ObservationContactStamp = 0; token.ObservationContactEntity = 0;
            return stamp;
        }
        // Main contacts measure contact-to-commit. Independent auxiliary pulses start at
        // their own submission entry, never at an earlier impact from the same payment.
        public static long StartEffect(BlockAttackToken token, uint entity)
            => StartEffect(token, entity, out _);
        public static long StartEffect(BlockAttackToken token, uint entity, out bool hadContact)
        {
            hadContact = false;
            if (CollisionEffective == null && !HasActualObservers) return 0;
            long stamp = ContactStamp(token, entity);
            hadContact = stamp != 0;
            return stamp != 0 ? stamp : Stopwatch.GetTimestamp();
        }
        public readonly struct ContactScope : IDisposable
        {
            readonly BlockAttackToken _token;
            readonly long _stamp;
            internal ContactScope(BlockAttackToken token, uint entity)
            { _token = token; Contact(token, entity); _stamp = token?.ObservationContactStamp ?? 0; }
            public void Dispose()
            {
                if (_token != null && _token.ObservationContactStamp == _stamp)
                { _token.ObservationContactStamp = 0; _token.ObservationContactEntity = 0; }
            }
        }
        public static ContactScope BeginContact(BlockAttackToken token, uint entity) => new(token, entity);
        public static void Effective(BlockAttackToken token, uint entity, string kind, int count, long stamp,
            uint ordinal = 0, bool auxiliary = false, bool hadContact = false)
        {
            if (stamp != 0 && count > 0)
                try { CollisionEffective?.Invoke(new Collision(token, entity, kind, count, stamp, ordinal, auxiliary, hadContact)); }
                catch (Exception exception) { ObserverFault(exception); }
        }
        public static Blast BeginBlast(BlockAttackToken token, uint entity, uint ordinal)
            => BlastQueued != null || BlastCompleted != null ? new Blast(token, entity, ordinal, ++_blastSequence) : default;
        public static void Queued(Blast observation, int removed)
        { if (observation.Started != 0) try { BlastQueued?.Invoke(observation, removed); } catch (Exception exception) { ObserverFault(exception); } }
        public static void Completed(Blast observation, int removed)
        { if (observation.Started != 0) try { BlastCompleted?.Invoke(observation, removed); } catch (Exception exception) { ObserverFault(exception); } }
        public static void Clear() { _contactEpoch++; _actualContext = default; }
    }
}
#endif
