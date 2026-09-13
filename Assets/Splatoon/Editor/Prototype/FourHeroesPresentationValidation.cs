#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Splatoon.Editor
{
    public static class FourHeroesPresentationValidation
    {
        const string Output="Docs/CombatGirls/FourHeroes";
        static readonly List<string> Report=new();
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        public static void Run()
        {
            try
            {
                typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current,
                    new cfg.Tables(n=>SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/"+n+".json"))));
                CombatGirlsHeroBuilder.Validate();
                CombatGirlsHeroBuilder.CaptureComparisons();
                EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity");
                Physics.SyncTransforms();
                var bright=CombatGirlsGraphicsValidation.FindCaptureFloor(false);var shadow=CombatGirlsGraphicsValidation.FindCaptureFloor(true);
                var camera=Camera.main;camera.enabled=false;camera.fieldOfView=35;camera.aspect=1;
                foreach(var pack in CombatGirlsHeroBuilder.Definitions)
                {
                    var root=new GameObject("Validation "+pack.name);root.layer=8;root.transform.position=bright;
                    using(var binder=new HeroViewBinder(root.transform))
                    {
                        binder.Apply(new HeroContent(GameplayConfig.GetHero(pack.id),AssetDatabase.LoadAssetAtPath<GameObject>(pack.CharacterPath),AssetDatabase.LoadAssetAtPath<GameObject>(pack.WeaponPath)));
                        var view=binder.View;view.InitializeBindings();
                        var state=new PlayerSnapshot{HeroId=pack.id,Health=100,Ink=100,Grounded=true,Team=1,Revision=1,Position=bright};
                        double now=0;
                        void Advance(int frames)
                        {
                            for(int i=0;i<frames;i++)
                            {
                                now+=1.0/60;view.Present(state,1f/60,now);
                                if(view.Animator.enabled)view.Animator.Update(1f/60);
                                view.ApplyAim();
                                Require(view.transform.localPosition.sqrMagnitude<.00001f,pack.name+" root motion drift");
                            }
                        }
                        void Capture(string name,float yaw=0,bool face=false)
                        {
                            Vector3 center=root.transform.position+Vector3.up*(face?view.Profile.CameraPivot.y:.9f);
                            camera.transform.position=center+Quaternion.Euler(0,yaw,0)*new Vector3(0,face?.025f:.15f,face?1.1f:3.8f);camera.transform.LookAt(center);
                            CombatGirlsGraphicsValidation.Render(camera,pack.name+"-training-"+name,Output+"/Screenshots");
                        }
                        Advance(20);Capture("front");Capture("side",90);Capture("back",180);Capture("face",0,true);
                        foreach(var direction in new[]{Vector3.forward,Vector3.back,Vector3.left,Vector3.right,new Vector3(1,0,1).normalized})
                        {state.Velocity=direction*5;Advance(30);}
                        state.Velocity=Vector3.zero;state.Grounded=false;Advance(12);
                        Require(view.Animator.GetCurrentAnimatorStateInfo(0).IsName("Air"),pack.name+" airborne pose");state.Grounded=true;
                        foreach(sbyte turn in new sbyte[]{-1,1,-1})
                        {state.TurnDirection=turn;state.TurnStartedAt=now;Advance(30);state.TurnDirection=0;Advance(12);}
                        state.RightShotAction=1;state.RightShotAt=now;Advance(3);
                        Require(view.Animator.GetLayerWeight(1)>.8f,pack.name+" right shot layer");
                        if(view.Profile.DualWield)
                        {
                            Advance(7);state.LeftShotAction=2;state.LeftShotAt=now;Advance(3);
                            Require(view.Animator.GetLayerWeight(1)>.8f&&view.Animator.GetLayerWeight(2)>.8f,"dual independent overlapping arm layers");
                            Require(!view.UseSupportGrip&&view.LeftWeapon.parent==view.LeftWeaponSocket,"dual hand mount/IK");
                        }
                        Capture("shot",35);Advance(60);
                        for(int layer=1;layer<view.Animator.layerCount;layer++)Require(view.Animator.GetLayerWeight(layer)==0,pack.name+" finished shot layer");
                        state.RightShotAction=3;state.RightShotAt=now;Advance(3);
                        state.Swimming=true;Advance(2);state.Swimming=false;Advance(1);
                        Require(view.Animator.GetLayerWeight(1)==0,pack.name+" emerge must not replay previous shot");
                        foreach(float pitch in new[]{-65f,75f})
                        {
                            state.Pitch=pitch;Advance(30);Capture(pitch<0?"aim-up":"aim-down",40);
                            float error=Vector3.Angle(view.AimReference.forward,Quaternion.Euler(pitch,0,0)*Vector3.forward);
                            Require(error<.1f,pack.name+" aim reference "+error);
                            if(view.UseSupportGrip)Require(Vector3.Distance(view.Animator.GetBoneTransform(HumanBodyBones.LeftHand).position,view.LeftGrip.position)<.045f,pack.name+" support hand");
                        }
                        state.Pitch=0;root.transform.position=shadow;state.Position=shadow;Advance(30);Capture("shadow");
                        root.transform.position=bright;state.Position=bright;state.Swimming=true;Advance(2);Require(!view.Animator.enabled,pack.name+" swimming hides animator");
                        state.Health=0;state.DeathDirection=1;state.DiedAt=now;Advance(130);Capture("death-forward",40);
                        Require(view.Animator.enabled&&view.GetComponentsInChildren<SkinnedMeshRenderer>().Any(r=>r.enabled),pack.name+" swim death visible");
                        state.DeathDirection=2;state.DiedAt=now;Advance(130);Capture("death-backward",40);
                        state.Health=100;state.Swimming=false;state.Revision++;state.RightShotAction=state.LeftShotAction=0;Advance(20);
                        Require(view.Weapon.parent==view.WeaponSocket,pack.name+" respawn weapon");Capture("respawn");
                        Report.Add(pack.name+": four directions, diagonal, air, turn interrupts, timed shot layers, aim -65/+75, grip, swim-death, both deaths and respawn PASS");
                    }
                    UnityEngine.Object.DestroyImmediate(root);
                }
                Report.Add("GPU captures: real TrainingGround lights/ink renderer, 960x960; source comparison captures use matched white light separately. This is evaluated Editor animation, not remote-device visual acceptance.");
                File.WriteAllLines(Output+"/presentation-validation.txt",Report);Debug.Log("[FourHeroes-Presentation] PASS");EditorApplication.Exit(0);
            }
            catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
        }
        public static void Play()
        {
            CombatGirlsHeroBuilder.Validate();
            UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings.ActivePlayModeDataBuilderIndex=0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main/Boot.unity");EditorApplication.EnterPlaymode();
        }
    }
}
#endif
