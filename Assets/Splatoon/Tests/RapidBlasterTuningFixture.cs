#if UNITY_EDITOR
using System.IO;
using SimpleJSON;
namespace Splatoon.Tests
{
    public static class RapidBlasterTuningFixture
    {
        public const string AssetPath = "Assets/GameResource/Weapons/RocketLauncherGirl/RocketLauncherGirlWeaponConfig.asset";
        public static void Apply(int heroId, JSONNode row, bool referenceFrames = false)
        {
            if (heroId != 5) return;
            var tuning = JSONNode.Parse(File.ReadAllText("Tools/ValidationData/WeaponAssets/RapidBlaster-Tuning.json"));
            var values = tuning["values"];
            if (referenceFrames) WeaponTimeFixture.ToReferenceFrames(values);
            foreach (var key in values.Keys) row[key] = values[key];
            if (referenceFrames) foreach (var key in tuning["heroValues"].Keys) row[key] = tuning["heroValues"][key];
        }
    }
}
#endif
