#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class ShooterMovementMigration
    {
        [MenuItem("喷墨对战/升级/射击与墙游数据")]
        public static void Run()
        {
            var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(CombatGirlsBuilder.ControllerPath);
            if(!controller.parameters.Any(p=>p.name=="MovePlayback"))controller.AddParameter("MovePlayback",AnimatorControllerParameterType.Float);
            var locomotion=controller.layers[0].stateMachine.states.Single(s=>s.state.name=="Locomotion").state;
            locomotion.speedParameter="MovePlayback";locomotion.speedParameterActive=true;EditorUtility.SetDirty(locomotion);EditorUtility.SetDirty(controller);
            var visual=PrefabUtility.LoadPrefabContents(CombatGirlsBuilder.CharacterPath);
            try
            {
                var effect=visual.GetComponent<Splatoon.Combat.InkCharacterView>().SwimEffect;
                Splatoon.Combat.InkCharacterView.ConfigureSwimEffect(effect);
                Splatoon.Combat.InkCharacterView.SetInkMesh(effect,AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab").GetComponent<ParticleSystemRenderer>().mesh);
                PrefabUtility.SaveAsPrefabAsset(visual,CombatGirlsBuilder.CharacterPath);
            }
            finally{PrefabUtility.UnloadPrefabContents(visual);}
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);
            var arena=Object.FindFirstObjectByType<PrototypeArena>(); arena.LayoutVersion=4;
            TrainingGroundBuilder.Bake(arena); AddRuler(arena);
            EditorSceneManager.SaveScene(arena.gameObject.scene);AssetDatabase.SaveAssets();
            CombatGirlsBuilder.ValidateInstalled();
            Debug.Log("[SHOOTER-MIGRATION] PASS wallRegions="+arena.Surfaces.Values.Sum(s=>s.WallRegions.Length));
        }
        static void AddRuler(PrototypeArena arena)
        {
            var root=arena.transform.Find("ShootingRangeRuler");
            if(root==null){root=new GameObject("ShootingRangeRuler").transform;root.SetParent(arena.transform,false);}
            var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // Decorative only: no colliders, no paint surfaces, no score changes.
            for(int i=0;i<=4;i++)
            {
                var marker=root.Find("Mark_"+i*4+"m");var go=marker!=null?marker.gameObject:new GameObject("Mark_"+i*4+"m",typeof(TextMesh));go.transform.SetParent(root,false);
                go.transform.position=new Vector3(-3,.035f,-28+i*4);go.transform.rotation=Quaternion.Euler(90,0,0);
                var text=go.GetComponent<TextMesh>();text.text=i*4+" m";text.characterSize=.16f;text.fontSize=32;text.anchor=TextAnchor.MiddleCenter;text.color=Color.white;
                text.font=font;go.GetComponent<MeshRenderer>().sharedMaterial=font.material;
            }
        }
    }
}
#endif
