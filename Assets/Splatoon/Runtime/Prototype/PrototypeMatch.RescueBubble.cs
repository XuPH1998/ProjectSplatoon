using System.Collections.Generic;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        readonly Queue<(PrototypePlayer player, PlayerInputFrame input)> _bubbleInteractions = new();
        public void QueueBubbleInteraction(PrototypePlayer player, PlayerInputFrame input)
        {
            if (IsServer && _bubbleInteractions.Count < 256 && State.Value.Phase != MatchPhase.Finished)
                _bubbleInteractions.Enqueue((player, input));
        }
        void ResolveBubbleInteractions(double now)
        {
            // Expire the entire roster first, independent of player iteration/packet arrival order.
            foreach (var player in Players) player.ExpireBubble(now);
            while (_bubbleInteractions.Count > 0)
            {
                var request = _bubbleInteractions.Dequeue();
                if (request.player != null && request.player.IsSpawned) request.player.ResolveBubbleInteraction(request.input, now);
            }
        }
    }
}
