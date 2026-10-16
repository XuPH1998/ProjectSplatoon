#if UNITY_EDITOR
using System.Linq;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Config;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class SplooshSummerTests
    {
        const string Root = "Assets/GameResource/Characters/SplooshGirl";
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void ModelMigrationKeepsOtherHeroTablesUnchanged(int hero)
        {
            var baseline = SimpleJSON.JSONNode.Parse(System.IO.File.ReadAllText("Tools/ValidationData/SplooshGirlSummer/tbhero-before.json"));
            var current = SimpleJSON.JSONNode.Parse(System.IO.File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/tbhero.json"));
            var expected = baseline.Children.Single(r => r["id"].AsInt == hero);
            var actual = current.Children.Single(r => r["id"].AsInt == hero);
            // Preserve the complete model-era row, with only separately documented later changes.
            // Docs/SpecialWeaponParameters.md: ordinary 0.21 HP/frame, swimming 1.75 HP/frame.
            expected["healthRecoverRate"] = 0.21 * 60;
            expected["swimHealthRecoverRate"] = 1.75 * 60;
            // Docs/SubWeapons.md: the new independent catalog keeps these original default choices.
            string[] defaults = { "SuctionBomb", "SplashWall", "PointSensor", "BurstBomb", "Torpedo", "Autobomb", "Sprinkler" };
            string type = defaults[hero - 1];
            expected["subWeaponConfigPath"] = "Assets/GameResource/SubWeapons/" + type + "/" + type + ".asset";
            if (hero == 6)
            {
                var replacement = SimpleJSON.JSONNode.Parse(System.IO.File.ReadAllText("Tools/ValidationData/MainWeaponReplacement/Tuning.json"))["heroChanges"]["6"];
                CollectionAssert.AreEquivalent(new[] { "weaponTypeName", "moveSpeed", "swimSpeed" }, replacement.Linq.Select(pair => pair.Key).ToArray(), "Approved replacement overlay has only these fields");
                foreach (string key in replacement.Keys)
                {
                    if (replacement[key].IsNumber)
                    {
                        Assert.That(actual[key].AsDouble, Is.EqualTo(replacement[key].AsDouble).Within(.00001), "Approved replacement value: " + key);
                        expected[key] = actual[key]; // Normalize only the independently checked float serialization.
                    }
                    else expected[key] = replacement[key];
                }
            }
            CollectionAssert.AreEquivalent(expected.Linq.Select(pair => pair.Key).ToArray(), actual.Linq.Select(pair => pair.Key).ToArray(), "Complete model row schema plus the explicit new catalog field");
            foreach (string key in expected.Keys)
                Assert.That(actual[key].ToString(), Is.EqualTo(expected[key].ToString()), "Frozen model migration field or approved overlay: " + key);
        }
        [Test]
        public void SummerModelHasIndependentAvatarAndCompleteSkinBinding()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/SplooshGirlVisual.prefab");
            var view = prefab.GetComponent<InkCharacterView>();
            Assert.That(AssetDatabase.GetAssetPath(view.Animator.avatar), Is.EqualTo(Root + "/Models/SummerCuteness.fbx"));
            Assert.That(view.Animator.avatar.isValid && view.Animator.avatar.isHuman, Is.True);
            Assert.That(view.Animator.applyRootMotion, Is.False);
            Assert.That(AssetDatabase.GetAssetPath(view.Animator.runtimeAnimatorController), Is.EqualTo(Root + "/Animations/SplooshGirlCombat.controller"));
            var controller = (AnimatorController)view.Animator.runtimeAnimatorController;
            var tree = (BlendTree)controller.layers[0].stateMachine.states.Single(s=>s.state.name=="Locomotion").state.motion;
            for(int i=1;i<tree.children.Length;i++) Assert.That(tree.children[i].timeScale, Is.EqualTo(view.Profile.WalkPlayback[i-1]));
            Assert.That(view.WeaponSocket.parent, Is.EqualTo(view.Animator.GetBoneTransform(HumanBodyBones.RightHand)));
            Assert.That(view.LeftWeaponSocket.parent, Is.EqualTo(view.Animator.GetBoneTransform(HumanBodyBones.LeftHand)));
            Assert.That(view.TeamMarker.transform.localPosition.y, Is.EqualTo(.025f).Within(.0001f));
            foreach (var skin in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Assert.That(skin.bones.All(b => b != null), Is.True, skin.name);
                Assert.That(skin.sharedMaterials.All(m => m != null && m.shader != null), Is.True, skin.name);
                foreach (var w in skin.sharedMesh.boneWeights)
                    Assert.That(w.weight0 + w.weight1 + w.weight2 + w.weight3, Is.EqualTo(1).Within(.001f), skin.name);
            }
            var capture = view.Profile.Paper.CapturePrefab.GetComponent<PaperCaptureRig>();
            Assert.That(capture.Animator.avatar, Is.EqualTo(view.Animator.avatar));
            Assert.That(view.Profile.Paper.ContentHash, Has.Length.EqualTo(64));
        }
        [Test]
        public void SummerStandingBodyFitsTheMeasuredSourceHeight()
        {
            HeroMigrationTests.Load();
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/SplooshGirlVisual.prefab"));
            try
            {
                var view = go.GetComponent<InkCharacterView>(); view.Animator.Rebind(); view.Animator.enabled = false;
                float low = float.PositiveInfinity, high = float.NegativeInfinity;
                foreach (var skin in go.GetComponentsInChildren<SkinnedMeshRenderer>().Where(s => s.enabled))
                {
                    var mesh = new Mesh();
                    try
                    {
                        skin.BakeMesh(mesh);
                        foreach (var p in mesh.vertices)
                        { float y = go.transform.InverseTransformPoint(skin.transform.TransformPoint(p)).y; low = Mathf.Min(low,y); high = Mathf.Max(high,y); }
                    }
                    finally { Object.DestroyImmediate(mesh); }
                }
                Assert.That(low, Is.InRange(-.01f,.01f));
                Assert.That(high-low, Is.EqualTo(1.5f).Within(.025f));
                Assert.That(GameplayConfig.GetHero(8).StandingHeight, Is.EqualTo(high-low).Within(.025f));
            }
            finally { Object.DestroyImmediate(go); LubanConfigService.Current.Reset(); }
        }
    }
}
#endif
