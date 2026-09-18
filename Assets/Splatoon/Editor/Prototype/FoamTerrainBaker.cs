using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Combat;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class FoamTerrainBaker
    {
        public const string DataPath="Assets/GameResource/Environment/TrainingGround/TrainingGroundFoam.asset";
        public const string MaterialPath="Assets/GameResource/Environment/TrainingGround/FoamTerrain.mat";
        static FoamTerrainBaker()=>EditorApplication.update+=Poll;
        static void Poll()
        {
            const string request="Temp/FoamTerrain/bake";
            const string build="Temp/FoamTerrain/build";
            if(File.Exists(build)&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating&&!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorUtility.scriptCompilationFailed)
            {
                var active=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi")).FirstOrDefault(t=>t!=null)?.GetMethod("IsRunActive",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
                if(active==null||(bool)active.Invoke(null,null))return;
                var path=File.ReadAllText(build).Trim();File.Delete(build);Directory.CreateDirectory("Reports/FoamTerrain");
                try{File.WriteAllText("Reports/FoamTerrain/build-status.txt","RUNNING "+DateTime.UtcNow.ToString("O"));PrototypeBuilder.BuildWindowsTo(path);File.WriteAllText("Reports/FoamTerrain/build-status.txt","PASS "+path);}
                catch(Exception e){File.WriteAllText("Reports/FoamTerrain/build-status.txt",e.ToString());Debug.LogException(e);}return;
            }
            if(EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode||!File.Exists(request))return;
            File.Delete(request);
            try{BakeTrainingGround();Directory.CreateDirectory("Reports/FoamTerrain");File.WriteAllText("Reports/FoamTerrain/bake.txt","OK "+DateTime.UtcNow.ToString("O"));}
            catch(Exception e){Directory.CreateDirectory("Reports/FoamTerrain");File.WriteAllText("Reports/FoamTerrain/bake.txt",e.ToString());Debug.LogException(e);}
        }
        [MenuItem("喷墨对战/地图/烘焙泡沫堆叠")]
        public static void BakeTrainingGround()
        {
            const string scenePath="Assets/GameResource/Gameplay/maps/TrainingGround.unity";
            var scene=UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);bool opened=!scene.isLoaded;
            if(opened)scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Additive);
            try
            {
                var arena=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<PrototypeArena>(true)).Single();
                if(scene.isDirty)throw new InvalidOperationException("训练场有未保存修改，请先保存再烘焙泡沫。");
                Bake(arena);EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();
            }
            finally{if(opened)EditorSceneManager.CloseScene(scene,true);}
        }
        public static void Bake(PrototypeArena arena)
        {
            InitializeConfig();arena.RegisterSurfaces();arena.RegisterHeroChangeZones();
            foreach(var s in arena.Surfaces.Values)s.InitializeOwnership(GameplayConfig.Map.CellSize);
            Physics.SyncTransforms();var generated=Build(arena,GameplayConfig.Map.FoamCellSize,GameplayConfig.Map.FoamChunkSize,GameplayConfig.Map.FoamMaxHeight,GameplayConfig.Map.FoamCeilingGap);
            var data=AssetDatabase.LoadAssetAtPath<FoamTerrainData>(DataPath);
            if(data==null){data=generated;AssetDatabase.CreateAsset(data,DataPath);}else{EditorUtility.CopySerialized(generated,data);UnityEngine.Object.DestroyImmediate(generated);}
            var material=AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if(material==null){material=new Material(Shader.Find("Splatoon/FoamTerrain"));AssetDatabase.CreateAsset(material,MaterialPath);}
            material.SetColor("_Pink",PrototypeArena.Pink);material.SetColor("_Blue",PrototypeArena.Blue);EditorUtility.SetDirty(material);
            arena.FoamData=data;arena.FoamMaterial=material;
            EditorUtility.SetDirty(data);EditorUtility.SetDirty(arena);
            Debug.Log($"[FOAM] Baked {data.Patches.Length} independent support patches.");
        }
        static void InitializeConfig()
        {
            // Use the project's editor bootstrap, which also resolves frozen weapon assets.
            InkLookUpgrade.LoadConfig();
        }
        public static FoamTerrainData Build(PrototypeArena arena,float cellSize,float chunkSize,float maxHeight,float ceilingGap)
        {
            bool backfaces=Physics.queriesHitBackfaces;Physics.queriesHitBackfaces=true;
            try{return BuildSupport(arena,cellSize,chunkSize,maxHeight,ceilingGap);}
            finally{Physics.queriesHitBackfaces=backfaces;}
        }
        static FoamTerrainData BuildSupport(PrototypeArena arena,float cellSize,float chunkSize,float maxHeight,float ceilingGap)
        {
            var data=ScriptableObject.CreateInstance<FoamTerrainData>();data.SourceTopology=arena.ComputeTopology();
            data.CellSize=cellSize;data.ChunkSize=chunkSize;data.MaxHeight=maxHeight;data.CeilingGap=ceilingGap;
            var patches=new List<FoamPatchBake>();var overlaps=new Collider[32];
            float spawnRadius=LubanConfigService.Current.Tables.TbHero.DataList.Max(h=>h.BodyRadius)+cellSize;
            foreach(var surface in arena.Surfaces.Values)foreach(var region in surface.GameplayRegions)
            {
                var normal=region.Normal(surface);if(normal.y<.65f)continue;
                int columns=Mathf.CeilToInt(region.Size.x/cellSize)+1,rows=Mathf.CeilToInt(region.Size.y/cellSize)+1;
                var bake=new FoamPatchBake{RegionKey=region.Key(surface),Columns=columns,Rows=rows,Size=region.Size,CeilingMm=new ushort[columns*rows],Edges=new byte[columns*rows]};
                var matrix=region.Matrix(surface);var step=new Vector2(region.Size.x/(columns-1),region.Size.y/(rows-1));
                Vector3 Point(int x,int z)=>matrix.MultiplyPoint3x4(new Vector3(-region.Size.x/2+x*step.x,0,-region.Size.y/2+z*step.y));
                for(int z=0;z<rows;z++)for(int x=0;x<columns;x++)
                {
                    int i=z*columns+x;var point=Point(x,z);
                    // Ownership cells classify their centres, not boundary vertices. At a ramp
                    // toe the next cell is blocked while the shared vertex is valid support.
                    bool blocked=false;int count=Physics.OverlapSphereNonAlloc(point+Vector3.up*.035f,.012f,overlaps,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore);
                    // A baked face may be the bottom of a solid wedge. Its own collider above
                    // the face is an obstruction too; only exposed upper surfaces can carry foam.
                    for(int n=0;n<count;n++)if(overlaps[n]!=null){blocked=true;break;}
                    if(blocked)continue;
                    float cap=maxHeight;
                    if(Physics.Raycast(point+Vector3.up*.001f,Vector3.up,out var hit,maxHeight+ceilingGap,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))cap=Mathf.Max(0,hit.point.y-point.y-ceilingGap);
                    foreach(var spawn in arena.SpawnPoints??Array.Empty<Transform>())
                        if(spawn!=null && Mathf.Abs(spawn.position.y-point.y)<.25f && new Vector2(spawn.position.x-point.x,spawn.position.z-point.z).sqrMagnitude<spawnRadius*spawnRadius)cap=Mathf.Min(cap,.001f);
                    if(arena.IsInHeroChangeZone(1,point+Vector3.up*.05f)||arena.IsInHeroChangeZone(2,point+Vector3.up*.05f))cap=Mathf.Min(cap,.001f);
                    bake.CeilingMm[i]=FoamRules.Quantize(cap);
                }
                for(int z=0;z<rows;z++)for(int x=0;x<columns;x++)
                {
                    int i=z*columns+x;if(bake.CeilingMm[i]==0)continue;
                    if(x+1<columns&&bake.CeilingMm[i+1]>0&&!Physics.Linecast(Point(x,z)+Vector3.up*.035f,Point(x+1,z)+Vector3.up*.035f,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))bake.Edges[i]|=1;
                    if(z+1<rows&&bake.CeilingMm[i+columns]>0&&!Physics.Linecast(Point(x,z)+Vector3.up*.035f,Point(x,z+1)+Vector3.up*.035f,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))bake.Edges[i]|=2;
                }
                bool supportsArea=false;
                for(int z=0;z<rows-1&&!supportsArea;z++)for(int x=0;x<columns-1;x++)
                {int i=z*columns+x;if(bake.CeilingMm[i]>0&&bake.CeilingMm[i+1]>0&&bake.CeilingMm[i+columns]>0&&bake.CeilingMm[i+columns+1]>0){supportsArea=true;break;}}
                // Decorative bodies immediately below a scored face cannot carry a second foam layer.
                if(supportsArea)patches.Add(bake);
            }
            data.Patches=patches.OrderBy(p=>p.RegionKey).ToArray();return data;
        }
    }
}
