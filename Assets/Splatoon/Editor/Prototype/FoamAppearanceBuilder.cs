using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class FoamAppearanceBuilder
    {
        const string Root="Assets/GameResource/Environment/TrainingGround/";
        public const string ProfilePath=Root+"FoamAppearance.asset";
        static FoamAppearanceBuilder(){EditorApplication.update+=Poll;}
        static void Poll()
        {
            const string path="Temp/FoamAppearance/build-assets";
            if(!File.Exists(path)||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorUtility.scriptCompilationFailed)return;
            File.Delete(path);
            try{Build();Directory.CreateDirectory("Reports/FoamAppearance");File.WriteAllText("Reports/FoamAppearance/assets-status.txt","PASS");}
            catch(System.Exception e){Debug.LogException(e);File.WriteAllText("Reports/FoamAppearance/assets-status.txt",e.ToString());}
        }
        [MenuItem("喷墨对战/表现/构建绵软泡沫资源")]
        public static void Build()
        {
            string texturePath=Root+"FoamPores.png";
            AssetDatabase.ImportAsset(texturePath,ImportAssetOptions.ForceSynchronousImport);
            var importer=(TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.textureType=TextureImporterType.Default;importer.sRGBTexture=false;importer.mipmapEnabled=true;
            importer.wrapMode=TextureWrapMode.Repeat;importer.filterMode=FilterMode.Trilinear;importer.anisoLevel=2;
            importer.alphaSource=TextureImporterAlphaSource.FromInput;importer.alphaIsTransparency=false;
            importer.isReadable=false;importer.textureCompression=TextureImporterCompression.CompressedHQ;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings{name="Android",overridden=true,maxTextureSize=512,format=TextureImporterFormat.ASTC_6x6});
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings{name="iPhone",overridden=true,maxTextureSize=512,format=TextureImporterFormat.ASTC_6x6});
            importer.SaveAndReimport();
            var profile=AssetDatabase.LoadAssetAtPath<FoamAppearanceProfile>(ProfilePath);
            bool created=profile==null;
            if(created){profile=ScriptableObject.CreateInstance<FoamAppearanceProfile>();AssetDatabase.CreateAsset(profile,ProfilePath);}
            if(profile.Pores==null)profile.Pores=AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(Root+"FoamBubble.asset");
            if(mesh==null)
            {
                mesh=BubbleMesh();AssetDatabase.CreateAsset(mesh,Root+"FoamBubble.asset");
            }
            var material=AssetDatabase.LoadAssetAtPath<Material>(Root+"FoamBubble.mat");
            if(material==null){material=new Material(Shader.Find("Splatoon/FoamParticle"));AssetDatabase.CreateAsset(material,Root+"FoamBubble.mat");}
            if(profile.BubbleMesh==null)profile.BubbleMesh=mesh;
            if(profile.BubbleMaterial==null)profile.BubbleMaterial=material;
            EditorUtility.SetDirty(profile);
            var terrain=AssetDatabase.LoadAssetAtPath<Material>(FoamTerrainBaker.MaterialPath);profile.Apply(terrain);EditorUtility.SetDirty(terrain);
            const string scenePath="Assets/GameResource/Gameplay/maps/TrainingGround.unity";
            var scene=UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);bool opened=!scene.isLoaded;
            if(opened)scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Additive);
            try
            {
                if(scene.isDirty)throw new System.InvalidOperationException("训练场有未保存修改，不能自动写入泡沫资源引用。");
                var arena=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<PrototypeArena>(true)).Single();
                arena.FoamAppearance=profile;EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
            }
            finally{if(opened)EditorSceneManager.CloseScene(scene,true);}
            AssetDatabase.SaveAssets();
        }
        static Mesh BubbleMesh()
        {
            const int sides=8;
            var vertices=new Vector3[2+sides*3];vertices[0]=Vector3.up*.5f;vertices[1]=Vector3.down*.5f;
            for(int ring=0;ring<3;ring++)for(int x=0;x<sides;x++)
            {
                float y=(1-ring)*.33f;float r=Mathf.Sqrt(.25f-y*y);float angle=x*Mathf.PI*2/sides;
                vertices[2+ring*sides+x]=new Vector3(Mathf.Cos(angle)*r,y,Mathf.Sin(angle)*r);
            }
            var triangles=new System.Collections.Generic.List<int>();
            for(int x=0;x<sides;x++)
            {
                int next=(x+1)%sides;triangles.AddRange(new[]{0,2+next,2+x,1,2+2*sides+x,2+2*sides+next});
                for(int ring=0;ring<2;ring++)
                {int a=2+ring*sides+x,b=2+ring*sides+next,c=a+sides,d=b+sides;triangles.AddRange(new[]{a,b,c,b,d,c});}
            }
            var mesh=new Mesh{name="Foam bubble 48 triangles"};mesh.vertices=vertices;mesh.triangles=triangles.ToArray();
            var normals=new Vector3[vertices.Length];for(int i=0;i<vertices.Length;i++)normals[i]=vertices[i].normalized;
            mesh.normals=normals;mesh.RecalculateBounds();return mesh;
        }
    }
}
