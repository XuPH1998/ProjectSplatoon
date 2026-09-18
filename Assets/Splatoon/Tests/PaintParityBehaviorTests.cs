#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class PaintParityBehaviorTests
    {
        [SetUp] public void Setup()=>WeaponReferenceMeasurements.LoadTables();
        [TearDown] public void Cleanup()=>LubanConfigService.Current.Reset();
        [TestCase(1,1f)] [TestCase(3,1.4f)] [TestCase(7,1.2f)]
        public void FootStampUsesItsOwnVerifiedDepth(int hero,float expected)
        {
            string output=PaintParityTests.Root+"/FootContract/w"+hero;Directory.CreateDirectory(output);
            var w=GameplayConfig.GetWeapon(hero);
            WeaponReferenceMeasurements.Capture(hero,0,"flat",60,1,output,fullActions:true);
            var feet=File.ReadAllLines(output+$"/w{hero}-q0-flat-60.paint.csv").Skip(1).Select(s=>s.Split(','))
                .Where(v=>Math.Abs(float.Parse(v[7],CultureInfo.InvariantCulture)-w.ReferenceFootRadius)<.0001f).ToArray();
            Assert.That(feet.Length,Is.EqualTo(1));
            Assert.That(float.Parse(feet[0][13],CultureInfo.InvariantCulture),Is.EqualTo(expected).Within(.0001));
        }
        [Test] public void FootDepthIsValidatedFrozenHotReloadableAndSigned()
        {
            var asset=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(7).WeaponConfigPath));
            try
            {
                var old=asset.Snapshot();asset.referenceFootDepth=1.5f;var next=asset.Snapshot();
                Assert.That(old.ReferenceFootDepth,Is.EqualTo(1.2f));Assert.That(old.SameValues(next),Is.False);
                Assert.That(old.RequiresRestart(next),Is.False);WeaponConfigValidation.Validate(next);
                asset.referenceFootDepth=0;Assert.Throws<InvalidOperationException>(()=>WeaponConfigValidation.Validate(asset.Snapshot()));
                asset.referenceFootDepth=float.NaN;Assert.Throws<InvalidOperationException>(()=>WeaponConfigValidation.Validate(asset.Snapshot()));
                new WeaponAssetTests().RoomSignatureIncludesWeaponAssetParameters("referenceFootDepth");
            }
            finally{UnityEngine.Object.DestroyImmediate(asset);}
        }
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void RealControllerTraversesMeasuredInkAndReplays(int hero)
        {
            var result=WeaponReferenceMeasurements.Capture(hero,hero==6?1:0,"flat",60,3,null,fullActions:true,
                inspectFloor:(floor,origin)=>
                {
                    var arenaObject=new GameObject("Isolated measured arena");arenaObject.SetActive(false);
                    var arena=arenaObject.AddComponent<PrototypeArena>();
                    var pawn=new GameObject("Real swim controller");pawn.layer=8;
                    var controller=pawn.AddComponent<CharacterController>();controller.height=1.8f;controller.center=Vector3.up*.9f;controller.radius=.35f;controller.skinWidth=.03f;
                    try
                    {
                        var motor=new PlayerMotorSimulation(controller,arena);
                        PlayerSnapshot Run(out int friendly,out float uninterrupted)
                        {
                            var s=new PlayerSnapshot{HeroId=hero,Team=1,Health=100,Ink=100,Revision=1,Grounded=true,Position=origin+Vector3.up*.04f};
                            motor.Restore(s);Physics.SyncTransforms();controller.Move(Vector3.down*.2f);s.Position=pawn.transform.position;s.Grounded=controller.isGrounded;s.VerticalSpeed=-2;
                            Assert.That(s.Grounded,Is.True);friendly=0;uninterrupted=0;bool continuous=true;
                            for(int tick=0;tick<120;tick++)
                            {
                                var input=new PlayerInputFrame{Sequence=(uint)tick+1,Swim=true,Move=Vector2.up};
                                motor.Step(ref s,input,1f/60,tick/60.0,false,GameplayConfig.GetWeapon(hero).ShootMoveSpeed);
                                if(s.SwimSource==SwimSurface.Friendly&&s.Swimming)friendly++;else continuous=false;
                                if(continuous)uninterrupted=s.Position.z-origin.z;
                            }
                            return s;
                        }
                        var a=Run(out int friendly,out float uninterrupted);var b=Run(out int replayFriendly,out _);
                        Assert.That(Vector3.Distance(a.Position,b.Position),Is.LessThan(.003));Assert.That(replayFriendly,Is.EqualTo(friendly));
                        Assert.That(a.Position.z,Is.GreaterThan(origin.z));
                        Directory.CreateDirectory(PaintParityTests.Root+"/Swim");
                        File.WriteAllText(PaintParityTests.Root+$"/Swim/w{hero}.txt",$"realCharacterController=true\nfriendlyTicks={friendly}\ntotalTicks=120\nuninterruptedFriendlyReach={uninterrupted}\ntravelled={a.Position.z-origin.z}\nreplayError={Vector3.Distance(a.Position,b.Position)}\nOriginal-game target not validated.\n");
                    }
                    finally{UnityEngine.Object.DestroyImmediate(pawn);UnityEngine.Object.DestroyImmediate(arenaObject);}
                });
            Assert.That(result.floorArea,Is.GreaterThan(0));
        }
    }
}
#endif
