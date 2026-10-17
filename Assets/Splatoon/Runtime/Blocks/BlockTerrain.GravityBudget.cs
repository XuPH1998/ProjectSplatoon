using UnityEngine;

namespace Splatoon.Blocks
{
    public sealed partial class BlockTerrain
    {
        int _gravityWorkBudgetFrame = -1, _gravityFrameChecks;
        public int GravityChecksThisFrame => _gravityWorkBudgetFrame == Time.frameCount ? _gravityFrameChecks : 0;
        public long GravityFrameBudgetDeferrals { get; private set; }

        // Every production gravity preparation/scan/motion entry shares this ledger.
        // Rejected or aborted drafts still consumed actual CPU, so do not roll it back.
        bool TrySpendGravityFrameWork(int count)
        {
            if (!_publicationTimingOpen || _stagePublicationWindow == null) return true;
            if (_gravityWorkBudgetFrame != Time.frameCount)
            { _gravityWorkBudgetFrame = Time.frameCount; _gravityFrameChecks = 0; }
            int limit = _commands.Count != 0 ? BlockPublicationPolicy.GravityChecksWithCommands : BlockPublicationPolicy.GravityChecksAlone;
            // As with publication, a fresh frame admits one bounded native unit:
            // an atomic PhysX call cannot be preempted. Later units share the
            // existing total terrain clock rather than receiving a fresh 3ms.
            if ((long)_gravityFrameChecks + count > limit || _gravityFrameChecks != 0 &&
                TerrainSynchronousMilliseconds >= _stagePublicationWindow.Policy.FrameSynchronousMilliseconds)
            { GravityFrameBudgetDeferrals++; return false; }
            _gravityFrameChecks += count; return true;
        }
    }
}
