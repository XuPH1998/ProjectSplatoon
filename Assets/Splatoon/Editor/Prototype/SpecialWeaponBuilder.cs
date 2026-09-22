using System;
using System.IO;
using Splatoon.Config;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
namespace Splatoon.Editor
{
    [InitializeOnLoad]
    public static class SpecialWeaponBuilder
    {
        static double _next;
        static SpecialWeaponBuilder()=>EditorApplication.update+=Poll;
        static void Poll()
        {
            if(EditorApplication.timeSinceStartup<_next)return;_next=EditorApplication.timeSinceStartup+1;
            const string request="Temp/SpecialWeapons/install";
            if(!File.Exists(request)||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            if(EditorUtility.scriptCompilationFailed)return;File.Delete(request);Directory.CreateDirectory("Reports/SpecialWeapons");
            try{Install();File.WriteAllText("Reports/SpecialWeapons/assets-result.txt","PASS "+DateTime.UtcNow.ToString("O"));}catch(Exception e){File.WriteAllText("Reports/SpecialWeapons/assets-result.txt",e.ToString());Debug.LogException(e);}
        }
        [MenuItem("喷墨对战/大招/安装五种大招和自由配装目录")]
        public static void Install()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("请在编辑模式安装");
            Directory.CreateDirectory(SpecialWeaponDefaults.Root);AssetDatabase.Refresh();
            var shader=Shader.Find("Splatoon/SubWeaponEffect");if(shader==null)throw new InvalidOperationException("缺少大招效果Shader");
            string mp=SpecialWeaponDefaults.Root+"SpecialEffect.mat";var material=AssetDatabase.LoadAssetAtPath<Material>(mp);
            if(material==null){material=new Material(shader);AssetDatabase.CreateAsset(material,mp);}
            bool refresh=File.Exists("Temp/SpecialWeapons/refresh-parameters");
            foreach(SpecialWeaponType type in Enum.GetValues(typeof(SpecialWeaponType)))
            {
                string path=SpecialWeaponDefaults.Path(type),dir=Path.GetDirectoryName(path).Replace('\\','/');Directory.CreateDirectory(dir);AssetDatabase.Refresh();
                var asset=AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(path);bool fresh=asset==null;
                if(fresh){asset=ScriptableObject.CreateInstance<SpecialWeaponConfigAsset>();SpecialWeaponDefaults.Apply(asset,type);}
                if(refresh)SpecialWeaponDefaults.Apply(asset,type);
                if(asset.effectMaterial==null)asset.effectMaterial=material;
                string prefab=dir+"/Model.prefab";
                if(asset.entityPrefab==null)
                {
                    var go=Model(type,material);
                    try{asset.entityPrefab=PrefabUtility.SaveAsPrefabAsset(go,prefab);}finally{UnityEngine.Object.DestroyImmediate(go);}
                }
                if(asset.heldPrefab==null)asset.heldPrefab=asset.entityPrefab;
                string icon=dir+"/Icon.png";
                if(asset.icon==null)
                {
                    var texture=new Texture2D(64,64,TextureFormat.RGBA32,false);
                    for(int y=0;y<64;y++)for(int x=0;x<64;x++)
                    {float r=Vector2.Distance(new Vector2(x,y),new Vector2(31.5f,31.5f));bool mark=type switch{SpecialWeaponType.Trizooka=>x>16&&x<48&&y>22&&y<42,SpecialWeaponType.TripleInkstrike=>y>12&&y<52&&(x%16<8),SpecialWeaponType.WaveBreaker=>r>18&&r<23||r<8,SpecialWeaponType.InkStorm=>y>24&&y<45&&r<24||y<22&&x%12<4, _=>Mathf.Abs(x-32)<(y+8)*.4f&&y<48};texture.SetPixel(x,y,r>30?Color.clear:mark?Color.white:Color.HSVToRGB((int)type*.15f,.75f,.65f));}
                    texture.Apply();File.WriteAllBytes(icon,texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);AssetDatabase.ImportAsset(icon);
                    var importer=(TextureImporter)AssetImporter.GetAtPath(icon);importer.textureType=TextureImporterType.Sprite;importer.spriteImportMode=SpriteImportMode.Single;importer.alphaIsTransparency=true;importer.mipmapEnabled=false;importer.SaveAndReimport();asset.icon=AssetDatabase.LoadAssetAtPath<Sprite>(icon);
                }
                if(asset.useAudio==null){Tone(dir+"/Use.wav",280+(int)type*65,.12f);AssetDatabase.ImportAsset(dir+"/Use.wav");asset.useAudio=AssetDatabase.LoadAssetAtPath<AudioClip>(dir+"/Use.wav");}
                if(asset.effectAudio==null){Tone(dir+"/Effect.wav",100+(int)type*30,.25f);AssetDatabase.ImportAsset(dir+"/Effect.wav");asset.effectAudio=AssetDatabase.LoadAssetAtPath<AudioClip>(dir+"/Effect.wav");}
                asset.Snapshot().Validate();if(fresh)AssetDatabase.CreateAsset(asset,path);else EditorUtility.SetDirty(asset);Register(path);
            }
            foreach(var file in Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban","*.json"))Register(file.Replace('\\','/'),"Luban");
            foreach(SubWeaponType t in Enum.GetValues(typeof(SubWeaponType)))Register(SubWeaponDefaults.Path(t));
            AssetDatabase.SaveAssets();AssetDatabase.Refresh();
            if(refresh)File.Delete("Temp/SpecialWeapons/refresh-parameters");
        }
        static void Register(string path,string label=null)
        {var settings=AddressableAssetSettingsDefaultObject.Settings;var entry=settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path),settings.FindGroup("Splatoon Local")??settings.DefaultGroup);entry.address=path;if(label!=null){settings.AddLabel(label);entry.SetLabel(label,true);}EditorUtility.SetDirty(settings);}
        static GameObject Model(SpecialWeaponType t,Material material)
        {
            var root=new GameObject(t.ToString());
            void Part(PrimitiveType primitive,Vector3 pos,Vector3 size,Vector3 rot=default){var child=GameObject.CreatePrimitive(primitive);child.transform.SetParent(root.transform,false);child.transform.localPosition=pos;child.transform.localScale=size;child.transform.localEulerAngles=rot;UnityEngine.Object.DestroyImmediate(child.GetComponent<Collider>());child.GetComponent<Renderer>().sharedMaterial=material;}
            switch(t)
            {
                case SpecialWeaponType.Trizooka:for(int i=0;i<3;i++){float a=i*Mathf.PI*2/3;Part(PrimitiveType.Cylinder,new Vector3(Mathf.Cos(a)*.13f,Mathf.Sin(a)*.13f,.1f),new Vector3(.23f,.5f,.23f),new Vector3(90,0,0));}break;
                case SpecialWeaponType.TripleInkstrike:Part(PrimitiveType.Capsule,Vector3.zero,new Vector3(.18f,.24f,.18f));Part(PrimitiveType.Sphere,Vector3.up*.22f,Vector3.one*.16f);break;
                case SpecialWeaponType.WaveBreaker:Part(PrimitiveType.Cylinder,Vector3.up*.08f,new Vector3(.8f,.08f,.8f));Part(PrimitiveType.Cylinder,Vector3.up*.7f,new Vector3(.12f,.7f,.12f));Part(PrimitiveType.Sphere,Vector3.up*1.4f,Vector3.one*.6f);break;
                case SpecialWeaponType.InkStorm:Part(PrimitiveType.Sphere,Vector3.zero,new Vector3(1,.35f,1));break;
                case SpecialWeaponType.Reefslider:Part(PrimitiveType.Capsule,Vector3.zero,new Vector3(.8f,1.1f,.8f),new Vector3(90,0,0));Part(PrimitiveType.Cube,new Vector3(0,.5f,-.3f),new Vector3(.15f,.6f,.5f));break;
            }
            return root;
        }
        static void Tone(string path,float frequency,float seconds)
        {const int rate=22050;int count=Mathf.CeilToInt(rate*seconds);using var w=new BinaryWriter(File.Create(path));w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+count*2);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(rate);w.Write(rate*2);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(count*2);for(int i=0;i<count;i++)w.Write((short)(Mathf.Sin(i*frequency*2*Mathf.PI/rate)*Mathf.Pow(1-i/(float)count,2)*12000));}
    }
}
