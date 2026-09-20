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
            Assert.That(current.Children.Single(r=>r["id"].AsInt==hero).ToString(), Is.EqualTo(baseline.Children.Single(r=>r["id"].AsInt==hero).ToString()));
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
