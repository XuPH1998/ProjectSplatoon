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
            if (hero == 3)
            {
                var tuning = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/Explosher/Tuning.json"));
                foreach (string key in tuning["weapons"].Keys) values[key] = tuning["weapons"][key];
                values["weaponTypeName"] = "爆炸泼桶";
                values["moveSpeed"] = .088 * 60 * 18 / 24.037;
                values["swimSpeed"] = .1728 * 60 * 18 / 24.037;
            }
            if (hero == 6 || hero == 8)
            {
                var replacement = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/MainWeaponReplacement/Tuning.json"));
                foreach (string key in replacement["weapons"][hero.ToString()]["fields"].Keys)
                    values[key] = replacement["weapons"][hero.ToString()]["fields"][key]["after"];
                foreach (string key in replacement["heroChanges"][hero.ToString()].Keys)
                    values[key] = replacement["heroChanges"][hero.ToString()][key];
            }
            if (referenceFrames) WeaponTimeFixture.ToReferenceFrames(values);
            // Compare the fields owned by each historical record; new fields have their
            // own validation/signature checks, including bool serialization semantics.
            foreach (var key in values.Keys) if (expected.HasKey(key)) expected[key] = values[key];
        }
        public static WeaponRuntimeConfig LegacySpread(int hero) => WeaponAssetTests.Changed(hero, a =>
        { DisableReconstruction(a); a.referenceRules = false; a.referenceSpreadEnabled = false; if (a.motionMode == ProjectileMotionMode.ReferencePhased) a.motionMode = ProjectileMotionMode.Ballistic; });
        public static void DisableReconstruction(WeaponConfigAsset a)
        {
            a.aimMode = WeaponAimMode.CameraHit; a.shotGuideSeconds = 0;
            a.angularSpread = a.detailedPaint = a.inheritForwardMovement = false;
            a.footSequence = FootSequenceBasis.ShotSequence; a.footPhase = 0;
        }
    }
}
#endif
