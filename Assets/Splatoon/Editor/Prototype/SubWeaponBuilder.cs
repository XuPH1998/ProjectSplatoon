using System;
using System.IO;
using Splatoon.Config;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class SubWeaponBuilder
    {
        static double _next;
        static SubWeaponBuilder() => EditorApplication.update += Poll;
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return; _next = EditorApplication.timeSinceStartup + 1;
            const string request = "Temp/SubWeapons/install";
            if (!File.Exists(request) || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string token = File.GetLastWriteTimeUtc(request).Ticks.ToString();
            if (SessionState.GetString("SubWeapon.InstallRefresh", "") != token)
            { SessionState.SetString("SubWeapon.InstallRefresh", token); AssetDatabase.Refresh(); return; }
            if(EditorUtility.scriptCompilationFailed)return;
            File.Delete(request); Directory.CreateDirectory("Reports/SubWeapons");
            try { Install(); File.WriteAllText("Reports/SubWeapons/assets-result.txt", "PASS " + DateTime.UtcNow.ToString("O")); }
            catch (Exception e) { File.WriteAllText("Reports/SubWeapons/assets-result.txt", e.ToString()); Debug.LogException(e); }
        }
        [MenuItem("喷墨对战/副武器/安装13种副武器资产")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请在编辑模式安装");
            Directory.CreateDirectory(SubWeaponDefaults.Root); AssetDatabase.Refresh();
            var material = Material("Ink", new Color(.7f, .18f, .65f)); var dark = Material("Dark", new Color(.08f, .1f, .15f));
            const string effectPath=SubWeaponDefaults.Root+"SubEffect.mat";
            var effectMaterial=AssetDatabase.LoadAssetAtPath<Material>(effectPath);
            if(effectMaterial==null){effectMaterial=new Material(Shader.Find("Splatoon/SubWeaponEffect"));AssetDatabase.CreateAsset(effectMaterial,effectPath);}
            foreach (SubWeaponType type in Enum.GetValues(typeof(SubWeaponType)))
            {
                string dir = SubWeaponDefaults.Root + type; Directory.CreateDirectory(dir); AssetDatabase.Refresh();
                string path = SubWeaponDefaults.Path(type);
                var existing = AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(path);
                if (existing != null)
                {
                    if(existing.common.effectMaterial==null){existing.common.effectMaterial=effectMaterial;EditorUtility.SetDirty(existing);}
                    if (existing.common.icon == null)
                    {
                        string icon = dir + "/" + type + "Icon.png";
                        var ti = AssetImporter.GetAtPath(icon) as TextureImporter;
                        if (ti != null) { ti.spriteImportMode = SpriteImportMode.Single; ti.SaveAndReimport(); existing.common.icon = AssetDatabase.LoadAssetAtPath<Sprite>(icon); EditorUtility.SetDirty(existing); }
                    }
                    UpgradeVisuals(existing, dir, material, dark, effectMaterial); Register(path); continue;
                }
                var asset = ScriptableObject.CreateInstance<SubWeaponConfigAsset>(); SubWeaponDefaults.Apply(asset, type);
                asset.common.effectMaterial=effectMaterial;
                string prefab = dir + "/" + type + "Model.prefab";
                var root = Model(type, material, dark);
                try { asset.common.entityPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefab); }
                finally { UnityEngine.Object.DestroyImmediate(root); }
                asset.common.heldPrefab = asset.common.entityPrefab;
                string iconPath = dir + "/" + type + "Icon.png";
                Icon(type, iconPath); AssetDatabase.ImportAsset(iconPath);
                var importer = (TextureImporter)AssetImporter.GetAtPath(iconPath); importer.textureType = TextureImporterType.Sprite; importer.spriteImportMode = SpriteImportMode.Single; importer.alphaIsTransparency = true; importer.mipmapEnabled = false; importer.SaveAndReimport();
                asset.common.icon = AssetDatabase.LoadAssetAtPath<Sprite>(iconPath);
                string use = dir + "/Use.wav", effect = dir + "/Effect.wav";
                Tone(use, 240 + (int)type * 22, .08f); Tone(effect, 90 + (int)type * 10, .2f); AssetDatabase.ImportAsset(use); AssetDatabase.ImportAsset(effect);
                asset.common.useAudio = AssetDatabase.LoadAssetAtPath<AudioClip>(use); asset.common.effectAudio = AssetDatabase.LoadAssetAtPath<AudioClip>(effect);
                UpgradeVisuals(asset, dir, material, dark, effectMaterial);
                asset.Snapshot().Validate(); AssetDatabase.CreateAsset(asset, path); Register(path);
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        }
        static void UpgradeVisuals(SubWeaponConfigAsset a, string dir, Material ink, Material dark, Material effect)
        {
            var v = a.visuals; var t = a.typeVisuals;
            if(v.trailLifetime<=0) v=SubVisualCommon.Defaults;
            if(v.previewMaterial==null)v.previewMaterial=effect;
            if(v.trailMaterial==null)v.trailMaterial=effect;
            GameObject Save(string name, Func<GameObject> make)
            {
                string path=dir+"/"+name+".prefab";var existing=AssetDatabase.LoadAssetAtPath<GameObject>(path);if(existing!=null)return existing;
                var root=make();try{return PrefabUtility.SaveAsPrefabAsset(root,path);}finally{UnityEngine.Object.DestroyImmediate(root);}
            }
            if(v.impactPrefab==null)v.impactPrefab=Save("Impact",()=>Particles("Impact",effect,false,24));
            if(v.endPrefab==null)v.endPrefab=Save("End",()=>Particles("End",effect,false,12));
            bool fuse=a.type==SubWeaponType.SplatBomb||a.type==SubWeaponType.SuctionBomb||a.type==SubWeaponType.CurlingBomb||a.type==SubWeaponType.Autobomb||a.type==SubWeaponType.FizzyBomb||a.type==SubWeaponType.Torpedo||a.type==SubWeaponType.InkMine;
            if(fuse&&t.warningPrefab==null)t.warningPrefab=Save("Warning",()=>Particles("Warning",effect,true,6));
            if(a.type==SubWeaponType.Torpedo||a.type==SubWeaponType.Sprinkler)
                if(t.dropletPrefab==null)t.dropletPrefab=Save("Droplet",()=>{
                    var root=new GameObject("Ink droplet");var ball=GameObject.CreatePrimitive(PrimitiveType.Sphere);ball.transform.SetParent(root.transform,false);ball.transform.localScale=Vector3.one*.18f;UnityEngine.Object.DestroyImmediate(ball.GetComponent<Collider>());ball.GetComponent<Renderer>().sharedMaterial=ink;return root;});
            if(a.type==SubWeaponType.Torpedo&&t.trackingPrefab==null)t.trackingPrefab=Save("Tracking",()=>Model(a.type,ink,dark));
            if(a.type==SubWeaponType.SuctionBomb||a.type==SubWeaponType.InkMine||a.type==SubWeaponType.Sprinkler)
                if(t.deployedPrefab==null)t.deployedPrefab=Save("Deployed",()=>Model(a.type,ink,dark));
            if(a.type==SubWeaponType.SplashWall)
            {
                if(t.deployedPrefab==null)t.deployedPrefab=Save("Deployed",()=>WallFrame(dark));
                if(t.persistentPrefab==null)t.persistentPrefab=Save("InkCurtain",()=>{
                    var root=new GameObject("Ink curtain");var plane=GameObject.CreatePrimitive(PrimitiveType.Cube);plane.transform.SetParent(root.transform,false);plane.transform.localPosition=Vector3.up*.5f;plane.transform.localScale=new Vector3(1,1,.8f);UnityEngine.Object.DestroyImmediate(plane.GetComponent<Collider>());plane.GetComponent<Renderer>().sharedMaterial=effect;return root;});
                if(t.wallFlow<=0)t.wallFlow=SubVisualSpecific.Defaults.wallFlow;
            }
            else if(a.type==SubWeaponType.PointSensor||a.type==SubWeaponType.ToxicMist||a.type==SubWeaponType.InkMine||a.type==SubWeaponType.Sprinkler)
                if(t.persistentPrefab==null)t.persistentPrefab=Save("Persistent",()=>Particles(a.type.ToString(),effect,true,a.type==SubWeaponType.ToxicMist?40:8));
            if(a.type==SubWeaponType.ToxicMist&&t.mistEmission<=0)t.mistEmission=SubVisualSpecific.Defaults.mistEmission;
            if(a.type==SubWeaponType.Sprinkler&&string.IsNullOrEmpty(t.rotorName))t.rotorName="Rotor";
            const string particlePath=SubWeaponDefaults.Root+"SubParticle.mat";
            var soft=AssetDatabase.LoadAssetAtPath<Material>(particlePath);
            if(soft==null){soft=new Material(effect);soft.SetFloat("_Soft",1);AssetDatabase.CreateAsset(soft,particlePath);}
            foreach(var prefab in new[]{v.impactPrefab,v.endPrefab,t.warningPrefab,t.persistentPrefab})
            {
                if(prefab==null)continue;string path=AssetDatabase.GetAssetPath(prefab);if(!path.StartsWith(dir+"/",StringComparison.Ordinal))continue;
                var root=PrefabUtility.LoadPrefabContents(path);bool changed=false;
                try{foreach(var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(true))if(renderer.sharedMaterial==effect){renderer.sharedMaterial=soft;changed=true;}if(changed)PrefabUtility.SaveAsPrefabAsset(root,path);}
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
            if(t.deployedPrefab!=null&&a.type!=SubWeaponType.SplashWall)
            {
                string path=AssetDatabase.GetAssetPath(t.deployedPrefab);
                if(path==dir+"/Deployed.prefab")
                {
                    var root=PrefabUtility.LoadPrefabContents(path);
                    try{var renderers=root.GetComponentsInChildren<Renderer>();float bottom=float.PositiveInfinity;foreach(var r in renderers)bottom=Mathf.Min(bottom,r.bounds.min.y-root.transform.position.y);
                        if(float.IsFinite(bottom)&&Mathf.Abs(bottom)>.001f){foreach(Transform child in root.transform)child.localPosition-=Vector3.up*bottom;PrefabUtility.SaveAsPrefabAsset(root,path);}}
                    finally{PrefabUtility.UnloadPrefabContents(root);}
                    if(a.type==SubWeaponType.Sprinkler)
                    {
                        root=PrefabUtility.LoadPrefabContents(path);bool changed=false;
                        try{foreach(Transform part in root.transform)
                            {if(part.name=="Rotor"&&Mathf.Abs(part.localPosition.y-.15f)>.001f){part.localPosition=new Vector3(0,.15f,0);changed=true;}
                             else if(part.name=="Ink"&&part.localScale.x<.2f&&part.localScale.y>.1f){part.localPosition=new Vector3(0,.085f,0);part.localScale=new Vector3(.14f,.065f,.14f);changed=true;}}
                            if(changed)PrefabUtility.SaveAsPrefabAsset(root,path);}
                        finally{PrefabUtility.UnloadPrefabContents(root);}
                    }
                }
            }
            a.visuals=v;a.typeVisuals=t;EditorUtility.SetDirty(a);
        }
        static GameObject WallFrame(Material material)
        {
            var root=new GameObject("Wall normalized frame");
            void Bar(Vector3 p,Vector3 scale){var bar=GameObject.CreatePrimitive(PrimitiveType.Cube);bar.name="Dark";bar.transform.SetParent(root.transform,false);bar.transform.localPosition=p;bar.transform.localScale=scale;UnityEngine.Object.DestroyImmediate(bar.GetComponent<Collider>());bar.GetComponent<Renderer>().sharedMaterial=material;}
            Bar(new Vector3(0,.02f,0),new Vector3(1,.04f,1));Bar(new Vector3(0,.98f,0),new Vector3(1,.04f,1));
            Bar(new Vector3(-.48f,.5f,0),new Vector3(.04f,1,1));Bar(new Vector3(.48f,.5f,0),new Vector3(.04f,1,1));return root;
        }
        static GameObject Particles(string name,Material material,bool loop,int count)
        {
            var root=new GameObject(name);var ps=root.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=ps.main;main.playOnAwake=false;main.loop=loop;main.duration=1;main.startLifetime=loop?1:.3f;main.startSpeed=loop?.3f:3;main.startSize=loop?.12f:.16f;main.maxParticles=160;main.simulationSpace=ParticleSystemSimulationSpace.Local;
            var emission=ps.emission;emission.rateOverTime=loop?count:0;if(!loop)emission.SetBursts(new[]{new ParticleSystem.Burst(0,(short)count)});
            var shape=ps.shape;shape.shapeType=ParticleSystemShapeType.Sphere;shape.radius=loop?.2f:.08f;
            var size=ps.sizeOverLifetime;size.enabled=true;size.size=new ParticleSystem.MinMaxCurve(1,AnimationCurve.Linear(0,1,1,0));
            var renderer=ps.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=material;
            if(name=="ToxicMist") { main.startSpeed=.12f;main.startLifetime=2;main.startSize=1;shape.radius=1; }
            if(name=="Warning") { main.startSize=.065f;main.startLifetime=.25f;shape.radius=.15f; }
            return root;
        }
        static Material Material(string name, Color color)
        {
            string path = SubWeaponDefaults.Root + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path); if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")); m.SetColor("_BaseColor", color); m.SetFloat("_Smoothness", .5f); AssetDatabase.CreateAsset(m, path); return m;
        }
        static void Register(string path)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var group = settings.FindGroup("Splatoon Local") ?? settings.DefaultGroup;
            var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), group); entry.address = path;
            EditorUtility.SetDirty(settings);
        }
        static GameObject Model(SubWeaponType type, Material ink, Material dark)
        {
            var root = new GameObject(type.ToString());
            void Part(PrimitiveType primitive, Vector3 p, Vector3 size, bool trim = false, Vector3 rotation = default)
            {
                var child = GameObject.CreatePrimitive(primitive); child.name = trim ? "Dark" : "Ink"; child.transform.SetParent(root.transform, false);
                child.transform.localPosition = p; child.transform.localScale = size; child.transform.localEulerAngles = rotation;
                UnityEngine.Object.DestroyImmediate(child.GetComponent<Collider>()); child.GetComponent<Renderer>().sharedMaterial = trim ? dark : ink;
            }
            switch (type)
            {
                case SubWeaponType.SplatBomb:
                    Part(PrimitiveType.Cube, Vector3.zero, new Vector3(.45f,.45f,.45f), false, new Vector3(30,45,20)); Part(PrimitiveType.Cylinder, Vector3.up*.27f,new Vector3(.12f,.05f,.12f),true); break;
                case SubWeaponType.SuctionBomb:
                    Part(PrimitiveType.Capsule, Vector3.up*.2f,new Vector3(.25f,.28f,.25f)); Part(PrimitiveType.Cylinder,Vector3.zero,new Vector3(.6f,.035f,.6f),true); break;
                case SubWeaponType.BurstBomb:
                    Part(PrimitiveType.Sphere,Vector3.zero,Vector3.one*.45f); Part(PrimitiveType.Cube,Vector3.up*.24f,new Vector3(.13f,.08f,.1f),true); break;
                case SubWeaponType.CurlingBomb:
                    Part(PrimitiveType.Cylinder,Vector3.zero,new Vector3(.65f,.09f,.65f)); Part(PrimitiveType.Cube,Vector3.up*.18f,new Vector3(.36f,.07f,.08f),true); break;
                case SubWeaponType.Autobomb:
                    Part(PrimitiveType.Sphere,Vector3.up*.15f,Vector3.one*.42f); Part(PrimitiveType.Cube,new Vector3(-.2f,-.05f,0),new Vector3(.12f,.25f,.28f),true); Part(PrimitiveType.Cube,new Vector3(.2f,-.05f,0),new Vector3(.12f,.25f,.28f),true); Part(PrimitiveType.Sphere,new Vector3(0,.2f,.22f),Vector3.one*.13f,true); break;
                case SubWeaponType.FizzyBomb:
                    Part(PrimitiveType.Cylinder,Vector3.zero,new Vector3(.26f,.3f,.26f)); Part(PrimitiveType.Cylinder,Vector3.up*.3f,new Vector3(.2f,.035f,.2f),true); break;
                case SubWeaponType.Torpedo:
                    Part(PrimitiveType.Capsule,Vector3.zero,new Vector3(.25f,.45f,.25f),false,new Vector3(90,0,0)); Part(PrimitiveType.Cube,new Vector3(0,0,-.2f),new Vector3(.75f,.04f,.2f),true); break;
                case SubWeaponType.InkMine:
                    Part(PrimitiveType.Cylinder,Vector3.zero,new Vector3(.6f,.035f,.6f)); Part(PrimitiveType.Sphere,Vector3.up*.06f,Vector3.one*.12f,true); break;
                case SubWeaponType.PointSensor:
                    Part(PrimitiveType.Cube,Vector3.zero,Vector3.one*.32f); Part(PrimitiveType.Cylinder,Vector3.up*.28f,new Vector3(.03f,.2f,.03f),true); break;
                case SubWeaponType.ToxicMist:
                    Part(PrimitiveType.Capsule,Vector3.zero,new Vector3(.3f,.24f,.3f)); Part(PrimitiveType.Cylinder,Vector3.up*.24f,new Vector3(.12f,.05f,.12f),true); break;
                case SubWeaponType.AngleShooter:
                    Part(PrimitiveType.Cube,Vector3.zero,new Vector3(.16f,.16f,.65f)); Part(PrimitiveType.Cube,new Vector3(0,0,.35f),new Vector3(.2f,.2f,.15f),true); break;
                case SubWeaponType.Sprinkler:
                    Part(PrimitiveType.Cylinder,Vector3.zero,new Vector3(.55f,.045f,.55f),true); Part(PrimitiveType.Cylinder,Vector3.up*.25f,new Vector3(.14f,.25f,.14f)); Part(PrimitiveType.Cube,Vector3.up*.5f,new Vector3(.65f,.1f,.1f)); root.transform.GetChild(root.transform.childCount-1).name="Rotor"; break;
                case SubWeaponType.SplashWall:
                    Part(PrimitiveType.Cube,Vector3.up*.05f,new Vector3(3,.1f,.35f),true);
                    for(int i=0;i<9;i++) Part(PrimitiveType.Cylinder,new Vector3(-1.4f+i*.35f,1.3f,0),new Vector3(.08f,1.3f,.08f));
                    Part(PrimitiveType.Cube,Vector3.up*2.6f,new Vector3(3,.12f,.3f),true); break;
            }
            return root;
        }
        static void Icon(SubWeaponType type, string path)
        {
            var texture = new Texture2D(128,128,TextureFormat.RGBA32,false);
            var colors = new Color[128*128];
            for(int y=0;y<128;y++) for(int x=0;x<128;x++)
            {
                float px=(x-64)/64f, py=(y-64)/64f; bool shape;
                switch(type)
                {
                    case SubWeaponType.SplatBomb: shape=py>-.65f&&py<.7f&&Mathf.Abs(px)<(.75f-py)*.55f;break;
                    case SubWeaponType.CurlingBomb: case SubWeaponType.InkMine: shape=px*px/.55f+py*py/.12f<1 || type==SubWeaponType.CurlingBomb&&Mathf.Abs(px)<.3f&&py>.25f&&py<.36f;break;
                    case SubWeaponType.Torpedo: shape=Mathf.Abs(py)<.15f&&Mathf.Abs(px)<.75f || px<-.2f&&Mathf.Abs(py)<.5f;break;
                    case SubWeaponType.SplashWall: shape=Mathf.Abs(px)<.68f&&Mathf.Abs(py)<.7f&&(Mathf.Abs(py)>.57f||((x/12)%2==0));break;
                    case SubWeaponType.Sprinkler: shape=Mathf.Abs(px)<.12f&&Mathf.Abs(py)<.7f || Mathf.Abs(py-.4f)<.1f&&Mathf.Abs(px)<.7f;break;
                    case SubWeaponType.FizzyBomb: case SubWeaponType.ToxicMist: case SubWeaponType.SuctionBomb: shape=Mathf.Abs(px)<.3f&&Mathf.Abs(py)<.65f || type==SubWeaponType.SuctionBomb&&py<-.48f&&py>-.65f&&Mathf.Abs(px)<.6f;break;
                    case SubWeaponType.AngleShooter: shape=Mathf.Abs(px-py)<.2f&&Mathf.Abs(px)<.65f;break;
                    case SubWeaponType.PointSensor: shape=Mathf.Abs(px)<.4f&&Mathf.Abs(py)<.4f||Mathf.Abs(px)<.045f&&py>.35f&&py<.75f;break;
                    default: shape=px*px+py*py<.4f;break;
                }
                colors[y*128+x]=shape?new Color(.94f,.96f,1,1):new Color(0,0,0,0);
            }
            texture.SetPixels(colors); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
        }
        static void Tone(string path, float frequency, float seconds)
        {
            const int rate=22050; int count=Mathf.CeilToInt(rate*seconds);
            using var w=new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+count*2);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(rate);w.Write(rate*2);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(count*2);
            for(int i=0;i<count;i++) w.Write((short)(Mathf.Sin(i*frequency*2*Mathf.PI/rate)*Mathf.Pow(1-i/(float)count,2)*12000));
        }
    }
}
