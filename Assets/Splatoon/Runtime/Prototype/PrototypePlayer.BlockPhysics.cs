namespace Splatoon.Prototype
{
    public sealed partial class PrototypePlayer
    {
        // Simulate owns this scope. Restores, owner prediction and reconciliation
        // run outside it, including prediction performed by a host player.
        internal bool IsAuthorityBlockPushStep => IsServer && _insideAuthorityStep &&
            PrototypeMatch.Current != null && PrototypeMatch.Current.IsServer;
    }
}
