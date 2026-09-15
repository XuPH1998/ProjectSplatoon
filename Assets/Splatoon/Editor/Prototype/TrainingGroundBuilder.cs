#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Splatoon.Prototype;
using Splatoon.Painting;
using Splatoon.Combat;
using Splatoon.Config;
namespace Splatoon.Editor
{
    public static class TrainingGroundBuilder
    {
        public const string ScenePath = "Assets/GameResource/Gameplay/maps/TrainingGround.unity";
        const string Art = "Assets/GameResource/Environment/TrainingGround";
        static int _id;
        static Transform _walk, _cover, _decor;
        static Material _ground, _wall, _dark, _pink, _blue, _white;
        [MenuItem("喷墨对战/地图/打开立体训练场")]
        public static void Open()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
        }
        [MenuItem("喷墨对战/地图/创建立体训练场（仅首次）")]
        public static void Create()
        {
            if (File.Exists(ScenePath)) throw new InvalidOperationException("地图已经存在，请直接编辑 Scene；首次创建不会覆盖已有地图。");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Art + "/Meshes"); Directory.CreateDirectory(Art + "/Materials");
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)); AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _id = 0;
            var root = new GameObject("TrainingGround · 立体训练场");
            var arena = root.AddComponent<PrototypeArena>();
            _walk = Group("01 可行走地形", root.transform); _cover = Group("02 掩体与边界", root.transform); _decor = Group("03 标识与场外装饰", root.transform);
            _ground = PaintMaterial("TrainingConcrete", new Color(1.35f,1.4f,1.4f));
            _wall = PaintMaterial("CoverConcrete", new Color(1.05f,1.15f,1.2f));
            _dark = SolidMaterial("Graphite", new Color(.10f,.16f,.20f));
            _pink = SolidMaterial("Pink", PrototypeArena.Pink);
            _blue = SolidMaterial("Blue", PrototypeArena.Blue);
            _white = SolidMaterial("White", new Color(.9f,.93f,.92f));
            // Eight ground tiles: equal texel density over the full 32 x 64 metre footprint.
            for (int x = 0; x < 2; x++) for (int z = 0; z < 4; z++)
                Face("Ground_" + x + "_" + z, new Vector3(-8 + 16*x,0,-24+16*z), Quaternion.identity, new Vector2(16,16),512);
            Box("Foundation", new Vector3(0,-.3f,0), new Vector3(32,.55f,64), _dark, _decor, false);
            foreach (int side in new[]{-1,1})
            {
                Box("PlatformBody_" + side, new Vector3(side*10.5f,1.48f,0), new Vector3(5,2.96f,10), _wall,_cover,true);
                Face("Platform_" + side,new Vector3(side*10.5f,3,0),Quaternion.identity,new Vector2(5,10),512);
                foreach (int end in new[]{-1,1})
                {
                    Ramp("Ramp_" + side + "_" + end, new Vector3(side*10.5f,1.5f,end*8), end);
                    Box("PlatformRail_" + side + "_" + end,new Vector3(side*12.9f,3.45f,end*4.8f),new Vector3(.2f,.9f,1.5f),_dark,_cover,false);
                }
                Box("SideBoundary_" + side,new Vector3(side*16.25f,1.5f,0),new Vector3(.5f,3,65),_wall,_cover,true);
                for(int z=-28;z<=28;z+=8)
                {
                    Box("BoundaryPost_"+side+"_"+z,new Vector3(side*16.6f,2.3f,z),new Vector3(.35f,4.6f,.35f),_dark,_decor,false);
                    Box("BoundaryCap_"+side+"_"+z,new Vector3(side*16.6f,4.65f,z),new Vector3(.7f,.15f,.7f),_white,_decor,false);
                }
            }
            Box("BridgeBody",new Vector3(0,2.78f,0),new Vector3(16,.4f,4),_wall,_cover,true);
            Face("Bridge",new Vector3(0,3,0),Quaternion.identity,new Vector2(16,4),512);
            foreach(int end in new[]{-1,1})
            {
                Material team = end<0 ? _pink : _blue;
                Box("EndBoundary_"+end,new Vector3(0,1.5f,end*32.25f),new Vector3(32.5f,3,.5f),_wall,_cover,true);
                Box("TeamHeader_"+end,new Vector3(0,3.2f,end*32.1f),new Vector3(14,.5f,.3f),team,_decor,false);
                Cover("SpawnShield_"+end,new Vector3(0,1.2f,end*24),new Vector3(8,2.4f,1));
                foreach(int side in new[]{-1,1})
                {
                    Cover("ForwardCover_"+side+"_"+end,new Vector3(side*7,1.1f,end*17),new Vector3(4,2.2f,2));
                    Cover("CenterCover_"+side+"_"+end,new Vector3(side*4,1.1f,end*8),new Vector3(2,2.2f,3));
                    Box("BridgeGuard_"+side+"_"+end,new Vector3(side*5.4f,3.4f,end*1.9f),new Vector3(3.2f,.8f,.2f),_dark,_cover,false);
                    Box("SpawnMarker_"+side+"_"+end,new Vector3(side*3,.008f,end*28),new Vector3(2,.01f,1),team,_decor,false,false);
                    Box("SideLaneStripe_"+side+"_"+end,new Vector3(side*15.4f,.008f,end*20),new Vector3(.15f,.01f,14),team,_decor,false,false);
                    Box("ExteriorBlock_"+side+"_"+end,new Vector3(side*21,2,end*22),new Vector3(5,4,10),_dark,_decor,false);
                    Box("ExteriorAccent_"+side+"_"+end,new Vector3(side*21,4.1f,end*22),new Vector3(5.2f,.2f,10.2f),team,_decor,false);
                }
            }
            var spawns = Group("04 出生点",root.transform); arena.SpawnPoints = new Transform[2 * TeamSelectionRules.Capacity];
            float[] spawnX = { -3, 3, -6, 6 };
            for(int i=0;i<arena.SpawnPoints.Length;i++)
            {
                bool pink = i < TeamSelectionRules.Capacity; int slot = i % TeamSelectionRules.Capacity;
                var point=Group((pink?"Pink_":"Blue_")+(slot+1),spawns);
                point.position=new Vector3(spawnX[slot],.05f,pink?-28:28); point.rotation=Quaternion.Euler(0,pink?0:180,0);arena.SpawnPoints[i]=point;
            }
            var lighting = Group("05 灯光与相机",root.transform);
            var sun=Group("Sun",lighting).gameObject.AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.8f;sun.color=new Color(1,.96f,.88f);sun.shadows=LightShadows.Soft;sun.transform.rotation=Quaternion.Euler(48,-35,0);sun.gameObject.AddComponent<UniversalAdditionalLightData>();
            RenderSettings.sun=sun;RenderSettings.ambientMode=AmbientMode.Trilight;RenderSettings.ambientSkyColor=new Color(.64f,.75f,.84f);RenderSettings.ambientEquatorColor=new Color(.52f,.59f,.64f);RenderSettings.ambientGroundColor=new Color(.32f,.35f,.38f);
            RenderSettings.skybox=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Sky/Sky_8.mat");
            var cam=Group("Main Camera",lighting).gameObject.AddComponent<Camera>();cam.tag="MainCamera";cam.transform.position=new Vector3(0,19,-36);cam.transform.LookAt(new Vector3(0,0,2));cam.fieldOfView=65;cam.farClipPlane=250;cam.nearClipPlane=.05f;cam.backgroundColor=new Color(.66f,.79f,.87f);cam.clearFlags=CameraClearFlags.Skybox;cam.gameObject.AddComponent<AudioListener>();cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var effects=root.AddComponent<InkPresentation>();
            effects.StreamPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab").GetComponent<ParticleSystem>();
            effects.ImpactPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Effects/Ink/Prefabs/InkImpact.prefab").GetComponent<ParticleSystem>();
            HeroChangeZoneSetup.AddMissingZones(arena);
            Bake(arena); EditorSceneManager.SaveScene(scene,ScenePath); AssetDatabase.SaveAssets(); PrototypeBuilder.ConfigureAddressables();
            Debug.Log("[MAP] Created fixed TrainingGround scene.");
        }
        static Transform Group(string name,Transform parent) {var go=new GameObject(name);go.transform.SetParent(parent,false);return go.transform;}
        static Material PaintMaterial(string name,Color color)
        {
            var mat=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Materials/PaintableWall.mat"));
            mat.SetTexture("Texture2D_41271c3c5f484ca2a435c65087a81705", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameResource/Environment/Ink/Textures/concrete_Wall_Albedo.jpg"));
            mat.SetVector("Vector2_e97cb9b7b5564bc9857e7669e2d0b82f", new Vector4(4,4,0,0));
            mat.name=name;mat.SetColor("Color_863351f5ceea4c998ef51baab6dd758b",color);
            AssetDatabase.CreateAsset(mat,Art+"/Materials/"+name+".mat");return mat;
        }
        static Material SolidMaterial(string name,Color color)
        {
            var mat=new Material(Shader.Find("Universal Render Pipeline/Lit")){name=name};mat.SetColor("_BaseColor",color);mat.SetFloat("_Smoothness",.22f);
            AssetDatabase.CreateAsset(mat,Art+"/Materials/"+name+".mat");return mat;
        }
        static Mesh SaveMesh(string name,List<Vector3> vertices,List<int> triangles,List<Vector2> uv)
        {
            var mesh=new Mesh{name=name};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.SetUVs(0,uv);mesh.SetUVs(1,uv);mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh,Art+"/Meshes/"+name+".asset");return mesh;
        }
        static PaintSurface Bind(GameObject go,bool scores,Vector2 size,int resolution)
        {
            var surface=go.AddComponent<PaintSurface>();surface.SurfaceId=++_id;surface.Scores=scores;surface.WalkableSize=size;surface.Resolution=resolution;
            surface.PainterShader=Shader.Find("Splatoon/InkTexturePainter");surface.ExtendShader=Shader.Find("TNTC/ExtendIslands");surface.DisplayShader=Shader.Find("Splatoon/InkDisplay");
            surface.ShapeAtlas=AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath);
            return surface;
        }
        static void Face(string name,Vector3 pos,Quaternion rotation,Vector2 size,int resolution)
        {
            var go=Group(name,_walk).gameObject;go.transform.SetPositionAndRotation(pos,rotation);
            var mesh=SaveMesh(name,new List<Vector3>{new(-size.x/2,0,-size.y/2),new(-size.x/2,0,size.y/2),new(size.x/2,0,size.y/2),new(size.x/2,0,-size.y/2)},new List<int>{0,1,2,0,2,3},new List<Vector2>{new(0,0),new(0,1),new(1,1),new(1,0)});
            go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=_ground;go.AddComponent<MeshCollider>().sharedMesh=mesh;Bind(go,true,size,resolution);
        }
        static GameObject Box(string name,Vector3 pos,Vector3 size,Material mat,Transform parent,bool paint,bool collision=true)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent,false);go.transform.position=pos;
            var source=go.GetComponent<MeshFilter>().sharedMesh;var mesh=UnityEngine.Object.Instantiate(source);mesh.name=name;
            var vertices=mesh.vertices;for(int i=0;i<vertices.Length;i++) vertices[i]=Vector3.Scale(vertices[i],size);mesh.vertices=vertices;
            var normals=mesh.normals;var uv=new Vector2[vertices.Length];
            for(int i=0;i<uv.Length;i++)
            {
                Vector3 n=normals[i],p=source.vertices[i];int face;Vector2 at;
                if(n.y>.5f){face=0;at=new Vector2(p.x+.5f,p.z+.5f);}else if(n.y<-.5f){face=1;at=new Vector2(p.x+.5f,p.z+.5f);}
                else if(n.x>.5f){face=2;at=new Vector2(p.z+.5f,p.y+.5f);}else if(n.x<-.5f){face=3;at=new Vector2(p.z+.5f,p.y+.5f);}
                else if(n.z>.5f){face=4;at=new Vector2(p.x+.5f,p.y+.5f);}else{face=5;at=new Vector2(p.x+.5f,p.y+.5f);}
                uv[i]=new Vector2((face%3+Mathf.Lerp(.02f,.98f,at.x))/3,(face/3+Mathf.Lerp(.02f,.98f,at.y))/2);
            }
            mesh.uv2=uv;mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,Art+"/Meshes/"+name+".asset");go.GetComponent<MeshFilter>().sharedMesh=mesh;go.GetComponent<MeshRenderer>().sharedMaterial=mat;
            if(collision)go.GetComponent<BoxCollider>().size=size;else UnityEngine.Object.DestroyImmediate(go.GetComponent<BoxCollider>());
            if(paint)Bind(go,false,Vector2.zero,256);return go;
        }
        static void Cover(string name,Vector3 pos,Vector3 size)
        {
            // Keep the paintable top exposed; a solid decorative cap would hide its ink.
            Box(name,pos,size,_wall,_cover,true);
        }
        static void Ramp(string name,Vector3 center,int end)
        {
            float angle=Mathf.Atan2(3,6)*Mathf.Rad2Deg;
            Quaternion rotation=Quaternion.Euler(-angle,end<0?0:180,0);
            Face(name,center,rotation,new Vector2(5,Mathf.Sqrt(45)),512);
            // Solid wedge below the scoring face. Its collision excludes inaccessible ground underneath.
            var go=Group(name+"_Body",_cover).gameObject;go.transform.position=new Vector3(center.x,0,center.z);go.transform.rotation=Quaternion.Euler(0,end<0?0:180,0);
            var v=new List<Vector3>{new(-2.5f,0,-3),new(2.5f,0,-3),new(-2.5f,0,3),new(2.5f,0,3),new(-2.5f,2.96f,3),new(2.5f,2.96f,3)};
            var t=new List<int>{0,4,2,0,2,1,1,2,3,1,3,5,2,4,5,2,5,3,0,1,5,0,5,4};
            // Render hard disconnected faces with a nonoverlapping paint atlas.
            var verts=new List<Vector3>();var tris=new List<int>();var uv=new List<Vector2>();
            for(int i=0;i<t.Count;i+=3){int f=i/3;int n=verts.Count;verts.Add(v[t[i]]);verts.Add(v[t[i+1]]);verts.Add(v[t[i+2]]);tris.AddRange(new[]{n,n+1,n+2});uv.Add(new Vector2((f%4+.05f)/4,(f/4+.05f)/3));uv.Add(new Vector2((f%4+.95f)/4,(f/4+.05f)/3));uv.Add(new Vector2((f%4+.5f)/4,(f/4+.95f)/3));}
            var mesh=SaveMesh(name+"_Body",verts,tris,uv);go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=_wall;
            var collider=go.AddComponent<MeshCollider>();collider.sharedMesh=mesh;collider.convex=true;Bind(go,false,Vector2.zero,256);
        }
        [MenuItem("喷墨对战/地图/校验并烘焙当前地图")]
        public static void BakeCurrent()
        {
            var arena=UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();
            if(arena==null||arena.gameObject.scene.path!=ScenePath)throw new InvalidOperationException("请先打开立体训练场 Scene");
            Bake(arena);EditorSceneManager.MarkSceneDirty(arena.gameObject.scene);EditorSceneManager.SaveScene(arena.gameObject.scene);
        }
        public static void Bake(PrototypeArena arena)
        {
            arena.RegisterSurfaces();Physics.SyncTransforms();
            arena.LayoutVersion=4;
            foreach(var surface in arena.Surfaces.Values) { PaintRegionBaker.Bake(surface,arena.OwnershipCellSize);EditorUtility.SetDirty(surface); }
            foreach(var s in arena.Surfaces.Values.Where(s=>s.Scores))
            {
                var grid=new SurfaceOwnershipGrid(s.WalkableSize,arena.OwnershipCellSize,null);var blocked=new List<int>();
                for(int i=0;i<grid.Cells.Length;i++)
                {
                    Vector3 p=s.transform.TransformPoint(grid.Center(i))+s.transform.up*.045f;
                    if(Physics.OverlapSphere(p,.012f,~(1<<8),QueryTriggerInteraction.Ignore).Any(c=>c.gameObject!=s.gameObject))blocked.Add(i);
                }
                s.BlockedCells=blocked.ToArray();s.InitializeOwnership(arena.OwnershipCellSize);EditorUtility.SetDirty(s);
            }
            arena.BakedTopology=arena.ComputeTopology();EditorUtility.SetDirty(arena);Validate(arena);
        }
        public static void Validate(PrototypeArena arena)
        {
            arena.RegisterSurfaces();
            if(arena.Dimensions!=new Vector2(32,64)||arena.LayoutVersion!=4)throw new InvalidOperationException("需要 32×64 米、版本 4 地图");
            if(arena.SpawnPoints==null||arena.SpawnPoints.Length!=2*TeamSelectionRules.Capacity||arena.SpawnPoints.Any(p=>p==null))throw new InvalidOperationException("八个出生点未完整绑定");
            HeroChangeZoneSetup.Validate(arena);
            long bytes=0;var sizes=new HashSet<Vector2Int>();
            foreach(var s in arena.Surfaces.Values)
            {
                var mesh=s.GetComponent<MeshFilter>().sharedMesh;
                if(mesh==null||mesh.uv2.Length!=mesh.vertexCount||s.GetComponent<Collider>()==null||s.PainterShader==null||s.DisplayShader==null)throw new InvalidOperationException("表面资源缺失："+s.name);
                if(s.Scores&&(Vector3.Distance(s.transform.lossyScale,Vector3.one)>.0001f||s.WalkableSize.x<=0||s.WalkableSize.y<=0))throw new InvalidOperationException("可行走面尺寸或缩放无效："+s.name);
                s.InitializeOwnership(arena.OwnershipCellSize);bytes+=4L*s.TextureBytes;sizes.Add(new Vector2Int(s.Resolution,s.Height));
            }
            foreach(var size in sizes)bytes+=(long)size.x*size.y*4;
            if(bytes>128L*1048576)throw new InvalidOperationException("涂色 RT 超过 128 MiB");
            if(arena.TotalArea<=0||arena.BakedTopology!=arena.ComputeTopology())throw new InvalidOperationException("地图拓扑未烘焙或没有可计分区域");
            foreach(var p in arena.SpawnPoints)
                if(!Physics.Raycast(p.position+Vector3.up,Vector3.down,out var hit,2,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore)||hit.collider.GetComponent<PaintSurface>()?.Scores!=true)throw new InvalidOperationException("出生点未落在可行走地面");
            Debug.Log("[MAP] Validation PASS dimensions=32x64 surfaces="+arena.Surfaces.Count+" area="+arena.TotalArea+" rtMiB="+bytes/1048576f+" topology="+arena.BakedTopology);
        }
        [MenuItem("喷墨对战/地图/增补四人队伍出生点")]
        public static void UpgradeFourPlayerSpawns()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出运行模式");
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            if (!opened && scene.isDirty) throw new InvalidOperationException("请先保存地图中的未保存编辑");
            if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                var arena = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PrototypeArena>()).Single();
                var old = arena.SpawnPoints;
                if (old == null || (old.Length != 4 && old.Length != 8) || old.Any(p => p == null)) throw new InvalidOperationException("原出生点绑定无效");
                if (old.Length == 4)
                {
                    var points = new Transform[8];
                    for (int team = 0; team < 2; team++)
                    {
                        points[team * 4] = old[team * 2]; points[team * 4 + 1] = old[team * 2 + 1];
                        for (int slot = 2; slot < 4; slot++)
                        {
                            var point = Group((team == 0 ? "Pink_" : "Blue_") + (slot + 1), old[team * 2].parent);
                            point.position = new Vector3(slot == 2 ? -6 : 6, old[team * 2].position.y, old[team * 2].position.z);
                            point.rotation = old[team * 2].rotation; points[team * 4 + slot] = point;
                        }
                    }
                    arena.SpawnPoints = points;
                }
                Physics.SyncTransforms(); arena.RegisterSurfaces();
                // Only spawn transforms changed; retain the authored surfaces and their existing paint bake.
                arena.BakedTopology = arena.ComputeTopology(); Validate(arena);
                EditorUtility.SetDirty(arena); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }
        public static void ValidateSavedScene()
        {
            var scene=UnityEngine.SceneManagement.SceneManager.GetSceneByPath(ScenePath);
            bool opened=!scene.IsValid()||!scene.isLoaded;
            if(opened)scene=EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Additive);
            try{Validate(scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<PrototypeArena>()).Single());}
            finally{if(opened)EditorSceneManager.CloseScene(scene,true);}
        }
        public static void CreateAndBuild()
        {
            if (!File.Exists(ScenePath)) Create();
            else EditorSceneManager.OpenScene(ScenePath);
            ValidateSavedScene();Capture();PrototypeBuilder.BuildWindows();
        }
        public static void RefreshPresentationAndBuild()
        {
            EditorSceneManager.OpenScene(ScenePath);
            var arena=UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();
            foreach(var s in arena.GetComponentsInChildren<PaintSurface>()) if(s.Scores)s.Resolution=512;
            foreach(var t in arena.GetComponentsInChildren<Transform>())
            {
                if(!(t.name.StartsWith("Platform")||t.name.StartsWith("Ramp"))||Mathf.Abs(Mathf.Abs(t.position.x)-11)>.01f)continue;
                var pos=t.position;pos.x=Mathf.Sign(pos.x)*10.5f;t.position=pos;
                if(t.name.StartsWith("PlatformRail"))continue;
                var mesh=t.GetComponent<MeshFilter>().sharedMesh;var vertices=mesh.vertices;
                for(int i=0;i<vertices.Length;i++)vertices[i].x*=5f/6;mesh.vertices=vertices;mesh.RecalculateBounds();EditorUtility.SetDirty(mesh);
                var box=t.GetComponent<BoxCollider>();if(box!=null){var size=box.size;size.x=5;box.size=size;}
                var collider=t.GetComponent<MeshCollider>();if(collider!=null){collider.sharedMesh=null;collider.sharedMesh=mesh;}
                var surface=t.GetComponent<PaintSurface>();if(surface.Scores)surface.WalkableSize=new Vector2(5,surface.WalkableSize.y);
            }
            foreach(var t in arena.GetComponentsInChildren<Transform>().Where(t=>t.name.StartsWith("PlatformRail")))
            {
                var pos=t.position;pos.x=Mathf.Sign(pos.x)*12.9f;t.position=pos;
                var mesh=t.GetComponent<MeshFilter>().sharedMesh;var vertices=mesh.vertices;var bounds=mesh.bounds;
                for(int i=0;i<vertices.Length;i++){vertices[i].x=vertices[i].x/bounds.size.x*.2f;vertices[i].z=vertices[i].z/bounds.size.z*1.5f;}
                mesh.vertices=vertices;mesh.RecalculateBounds();EditorUtility.SetDirty(mesh);t.GetComponent<BoxCollider>().size=new Vector3(.2f,.9f,1.5f);
            }
            foreach(string name in new[]{"TrainingConcrete","CoverConcrete"})
            {
                var mat=AssetDatabase.LoadAssetAtPath<Material>(Art+"/Materials/"+name+".mat");
                mat.SetTexture("Texture2D_41271c3c5f484ca2a435c65087a81705", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameResource/Environment/Ink/Textures/concrete_Wall_Albedo.jpg"));
                mat.SetVector("Vector2_e97cb9b7b5564bc9857e7669e2d0b82f",new Vector4(4,4,0,0));
                mat.SetColor("Color_863351f5ceea4c998ef51baab6dd758b",name=="TrainingConcrete"?new Color(1.35f,1.4f,1.4f):new Color(1.05f,1.15f,1.2f));EditorUtility.SetDirty(mat);
            }
            Bake(arena);EditorSceneManager.SaveScene(arena.gameObject.scene);AssetDatabase.SaveAssets();Capture();PrototypeBuilder.BuildWindows();
        }
        public static void Capture()
        {
            Directory.CreateDirectory("Reports/TrainingGround/Screenshots");
            var camera=UnityEngine.Object.FindFirstObjectByType<Camera>();var oldPos=camera.transform.position;var oldRot=camera.transform.rotation;bool ortho=camera.orthographic;
            camera.orthographic=true;camera.orthographicSize=38;camera.transform.position=new Vector3(44,58,-60);camera.transform.LookAt(Vector3.zero);
            Render(camera,"birdseye");
            camera.orthographic=false;camera.transform.position=new Vector3(-3,2.1f,-29);camera.transform.LookAt(new Vector3(0,2,-5));Render(camera,"spawn");
            camera.transform.position=new Vector3(-6,5,-3);camera.transform.LookAt(new Vector3(10,3,1));Render(camera,"bridge");
            camera.transform.SetPositionAndRotation(oldPos,oldRot);camera.orthographic=ortho;
        }
        static void Render(Camera camera,string name)
        {
            var rt=new RenderTexture(1600,1000,24);rt.Create();var prior=RenderTexture.active;
            var request=new UniversalRenderPipeline.SingleCameraRequest{destination=rt};RenderPipeline.SubmitRenderRequest(camera,request);RenderPipeline.SubmitRenderRequest(camera,request);
            RenderTexture.active=rt;var texture=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);texture.Apply();File.WriteAllBytes("Reports/TrainingGround/Screenshots/"+name+".png",texture.EncodeToPNG());
            RenderTexture.active=prior;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
#endif
