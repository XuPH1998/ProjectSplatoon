#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Diagnostics;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Blocks
{
    public static partial class BlockAttackObservation
    {
        public enum ActualOperation : byte { System, Build, Direct, Blast }
        public readonly struct Source
        {
            public readonly uint Round, Life, Equipment, ConfigRevision;
            public readonly ulong Owner, Action;
            public readonly byte Team;
            public readonly PaintAttackKind Kind;
            public readonly int Hero, SubWeaponId;
            public Source(uint round, ulong owner, uint life, uint equipment, ulong action, byte team,
                PaintAttackKind kind, int hero, int subWeaponId, uint configRevision)
            {
                Round = round; Owner = owner; Life = life; Equipment = equipment; Action = action;
                Team = team; Kind = kind; Hero = hero; SubWeaponId = subWeaponId; ConfigRevision = configRevision;
            }
        }
        public readonly struct Payment
        {
            public readonly Source Source;
            public readonly double Material, ServerTime;
            public readonly uint NetworkTick;
            public Payment(Source source, double material, double serverTime, uint networkTick)
            { Source = source; Material = material; ServerTime = serverTime; NetworkTick = networkTick; }
        }
        public struct ActualCounts
        {
            public int NewFixed, NewPaint, EnemyPaintReplaced, EnemyFixedRemoved, FriendlyFixedRemoved,
                EnemyPaintRemoved, FriendlyPaintRemoved, Converted, Relocated;
        }
        public readonly struct ActualResult
        {
            public readonly Source Source;
            public readonly ActualOperation Operation;
            public readonly uint Entity, Ordinal, Epoch, Round, Revision, SurfaceRevision, NetworkTick;
            public readonly double ServerTime, Milliseconds;
            public readonly ActualCounts Counts;
            public readonly bool HadChanges, Auxiliary, HadContact;
            internal ActualResult(Context context, BlockFixedGridWorld world, ActualCounts counts, bool changed)
            {
                Source = context.Source; Operation = context.Operation; Entity = context.Entity; Ordinal = context.Ordinal;
                Epoch = world.Epoch; Round = world.Round; Revision = world.Revision; SurfaceRevision = world.Surfaces.Revision;
                Clock(out double now, out uint tick); ServerTime = now; NetworkTick = tick;
                Counts = counts; HadChanges = changed;
                Auxiliary = context.Auxiliary; HadContact = context.HadContact;
                Milliseconds = context.ContactStamp == 0 ? 0 : (Stopwatch.GetTimestamp() - context.ContactStamp) * 1000d / Stopwatch.Frequency;
            }
        }
        internal readonly struct Context
        {
            internal readonly Source Source;
            internal readonly ActualOperation Operation;
            internal readonly uint Entity, Ordinal;
            internal readonly long ContactStamp;
            internal readonly bool Auxiliary, HadContact;
            internal Context(Source source, uint entity, uint ordinal, ActualOperation operation, bool auxiliary, long contactStamp, bool hadContact)
            { Source = source; Entity = entity; Ordinal = ordinal; Operation = operation; Auxiliary = auxiliary; ContactStamp = contactStamp; HadContact = hadContact; }
        }
        [ThreadStatic] static Context _actualContext;
        static Action<Payment> _paymentAccepted;
        static Action<ActualResult> _actualResultCommitted;
        static Delegate[] _paymentObservers = Array.Empty<Delegate>(), _actualObservers = Array.Empty<Delegate>();
        public static int ObserverFaultCount { get; private set; }
        public static string LastObserverError { get; private set; }
        public static bool HasActualObservers => _actualObservers.Length != 0;
        internal static Context CurrentContext => _actualContext;
        // Subscription is infrequent; publishing never allocates an invocation array.
        public static event Action<Payment> PaymentAccepted
        {
            add { _paymentAccepted += value; _paymentObservers = _paymentAccepted?.GetInvocationList() ?? Array.Empty<Delegate>(); }
            remove { _paymentAccepted -= value; _paymentObservers = _paymentAccepted?.GetInvocationList() ?? Array.Empty<Delegate>(); }
        }
        public static event Action<ActualResult> ActualResultCommitted
        {
            add { _actualResultCommitted += value; _actualObservers = _actualResultCommitted?.GetInvocationList() ?? Array.Empty<Delegate>(); }
            remove { _actualResultCommitted -= value; _actualObservers = _actualResultCommitted?.GetInvocationList() ?? Array.Empty<Delegate>(); }
        }
        internal static void ObserverFault(Exception exception)
        { ObserverFaultCount++; LastObserverError = exception.ToString(); }
        static void Clock(out double serverTime, out uint networkTick)
        {
            var match = PrototypeMatch.Current;
            var manager = match != null ? match.NetworkManager : null;
            if (manager != null && manager.IsListening)
            { serverTime = manager.ServerTime.Time; networkTick = (uint)manager.ServerTime.Tick; }
            else { serverTime = UnityEngine.Time.unscaledTimeAsDouble; networkTick = 0; }
        }
        internal static void AcceptPayment(BlockAttackToken token, PlayerSnapshot state, ulong action, double material, uint configRevision)
        {
            var credit = token.Credit;
            int subWeapon = state.SubWeaponId;
            // Preserve original identity even when observation starts later. Optional
            // catalogue resolution must never introduce a gameplay prerequisite.
            if (subWeapon <= 0 && (_paymentObservers.Length != 0 || HasActualObservers))
                try { subWeapon = SubWeaponConfigService.Current.Resolve(state.HeroId, subWeapon); }
                catch (Exception exception) { ObserverFault(exception); }
            token.ObservationSource = new Source(credit.Round, credit.Owner, state.Revision, state.HeroRevision, action,
                credit.Team, credit.Kind, state.HeroId, subWeapon, configRevision);
            var observers = _paymentObservers;
            if (observers.Length == 0) return;
            try
            {
                Clock(out double now, out uint tick);
                var payment = new Payment(token.ObservationSource, material, now, tick);
                foreach (Action<Payment> observer in observers)
                    try { observer(payment); } catch (Exception exception) { ObserverFault(exception); }
            }
            catch (Exception exception) { ObserverFault(exception); }
        }
        public readonly struct ActualScope : IDisposable
        {
            readonly Context _previous;
            readonly bool _enabled;
            internal ActualScope(BlockAttackToken token, uint entity, uint ordinal, ActualOperation operation, bool auxiliary, long contactStamp, bool hadContact)
            {
                _enabled = HasActualObservers; _previous = default;
                if (!_enabled) return;
                _previous = _actualContext;
                _actualContext = new Context(token?.ObservationSource ?? default, entity, ordinal, operation, auxiliary, contactStamp, hadContact);
            }
            public void Dispose() { if (_enabled) _actualContext = _previous; }
        }
        public static ActualScope BeginActualResult(BlockAttackToken token, uint entity, uint ordinal, ActualOperation operation,
            bool auxiliary = false, long contactStamp = 0, bool hadContact = false)
            => new(token, entity, ordinal, operation, auxiliary, contactStamp, hadContact);
        internal static void PublishActual(Context context, BlockFixedGridWorld world, ActualCounts counts, bool changed)
        {
            var observers = _actualObservers;
            if (observers.Length == 0) return;
            try
            {
                var result = new ActualResult(context, world, counts, changed);
                foreach (Action<ActualResult> observer in observers)
                    try { observer(result); } catch (Exception exception) { ObserverFault(exception); }
            }
            catch (Exception exception) { ObserverFault(exception); }
        }
    }
}
#endif
