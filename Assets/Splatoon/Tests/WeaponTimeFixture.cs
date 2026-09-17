#if UNITY_EDITOR
using System;
using SimpleJSON;

namespace Splatoon.Tests
{
    // Compatibility for pinned reference-frame test data, never used by gameplay.
    public static class WeaponTimeFixture
    {
        public static int ReferenceFrames(double seconds) => checked((int)Math.Round(seconds * 60));
        public static readonly (string frames, string seconds)[] Fields =
        {
            ("blasterRepeatFrames", "blasterRepeatSeconds"),
            ("blasterPostFrames", "blasterPostSeconds"),
            ("startFrames", "startSeconds"),
            ("emergeStartFrames", "emergeStartSeconds"),
            ("inkRecoverLockFrames", "inkRecoverLockSeconds"),
            ("burstRecoveryFrames", "burstRecoverySeconds"),
            ("straightFrames", "straightSeconds"),
            ("brakeFrames", "brakeSeconds"),
            ("spreadRecoverFrames", "landingSpreadRecoverSeconds"),
            ("damageReduceStartFrames", "damageReduceStartSeconds"),
            ("damageReduceEndFrames", "damageReduceEndSeconds"),
            ("chargeFrames", "chargeSeconds"),
            ("semiBufferFrames", "semiBufferSeconds"),
            ("splatlingMinChargeFrames", "splatlingMinChargeSeconds"),
            ("splatlingFirstChargeFrames", "splatlingFirstChargeSeconds"),
            ("splatlingFirstShootFrames", "splatlingFirstShootSeconds"),
            ("splatlingFullShootFrames", "splatlingFullShootSeconds"),
            ("splatlingPostFrames", "splatlingPostSeconds"),
        };
        public static void ToReferenceFrames(JSONNode row)
        {
            foreach (var (frames, seconds) in Fields)
                if (row.HasKey(seconds)) { row[frames] = row[seconds].AsDouble * 60; row.Remove(seconds); }
        }
        public static void ToSeconds(JSONNode row)
        {
            foreach (var (frames, seconds) in Fields)
                if (row.HasKey(frames)) { row[seconds] = row[frames].AsDouble / 60; row.Remove(frames); }
        }
    }
}
#endif
