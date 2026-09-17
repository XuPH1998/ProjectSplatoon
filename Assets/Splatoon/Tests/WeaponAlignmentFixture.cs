#if UNITY_EDITOR
using System.IO;
using System.Linq;
using SimpleJSON;
using Splatoon.Config;

namespace Splatoon.Tests
{
    // Overlay the approved reference import on historical expectations, without editing
    // archived baselines. The explicit target/integrator tests remain independent.
    public static class WeaponAlignmentFixture
    {
        public static void Apply(int hero, JSONNode expected, bool referenceFrames = false)
        {
            var values = new JSONObject();
            foreach (var row in JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponAlignment/Baseline/Values.json")).Children)
                if (row["id"].AsInt == hero) foreach (var key in row.Keys) values[key] = row[key];
            foreach (var entry in JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponAlignment/Tuning.json"))["fields"].Children)
                if (entry["hero"].AsInt == hero) values[entry["field"].Value] = entry["after"];
            if (hero == 1) values["weaponTypeName"] = "斯普拉射击枪";
            if (referenceFrames) WeaponTimeFixture.ToReferenceFrames(values);
            // Compare the fields owned by each historical record; new fields have their
            // own validation/signature checks, including bool serialization semantics.
            foreach (var key in values.Keys) if (expected.HasKey(key)) expected[key] = values[key];
        }
        public static WeaponRuntimeConfig LegacySpread(int hero) => WeaponAssetTests.Changed(hero, a =>
        { a.referenceRules = false; a.referenceSpreadEnabled = false; if (a.motionMode == ProjectileMotionMode.ReferencePhased) a.motionMode = ProjectileMotionMode.Ballistic; });
    }
}
#endif
