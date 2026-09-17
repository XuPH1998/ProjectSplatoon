#if UNITY_EDITOR
using UnityEditor;

namespace Splatoon.Editor
{
    public static class FramePerformanceBuild
    {
        // Device profiling requires a Player. Preserve the project's saved quality settings.
        public static void BuildWindows()
        {
            bool previous = PlayerSettings.enableFrameTimingStats;
            try { PlayerSettings.enableFrameTimingStats = true; PrototypeBuilder.BuildWindows(); }
            finally { PlayerSettings.enableFrameTimingStats = previous; }
        }
    }
}
#endif
