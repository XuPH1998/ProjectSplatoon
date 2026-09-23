#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkSoftValidation
    {
        static string Output
        {
            get { var a=Environment.GetCommandLineArgs(); int i=Array.IndexOf(a,"-inkValidationOutput"); return i>=0?a[i+1]:"Reports/InkSoftEdges/Final"; }
        }
        [MenuItem("喷墨对战/墨迹静态表现/柔润实验/当前效果（不保存资源）")]
        public static void Current() => Switch(InkLook.Current);
        [MenuItem("喷墨对战/墨迹静态表现/柔润实验/柔润效果（不保存资源）")]
        public static void Soft() => Switch(InkLook.Soft);
        static void Switch(InkLook look) { foreach(var s in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None))s.SetInkLook(look); }
        static void Check(bool ok,string message) { if(!ok)throw new InvalidOperationException(message); }
        static void Cleanup()
        {
            AsyncGPUReadback.WaitAllRequests();
            foreach(var s in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None))s.ReleaseGraphics();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Check(PaintSurface.AllocatedBytes==0 && PaintSurface.CheckpointBytes==0,"Paint RT leak");
        }
        static float[] ReadDistance(RenderTexture rt)
        {
            var previous=RenderTexture.active; RenderTexture.active=rt;
            var t=new Texture2D(rt.width,rt.height,TextureFormat.RGBAFloat,false,true);
            try { t.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); t.Apply(); bool half=rt.graphicsFormat==UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat;
                return t.GetPixels().SelectMany(c=>half?new[]{c.r/(2*PaintSurface.BoundaryRange)+.5f,c.g/(2*PaintSurface.BoundaryRange)+.5f}:new[]{c.r,c.g}).ToArray(); }
            finally { RenderTexture.active=previous; UnityEngine.Object.DestroyImmediate(t); }
        }
        public static async void Run()
        {
            try
            {
                Directory.CreateDirectory(Output); InkStaticUpgrade.LoadTables(); Cleanup(); await Task.Delay(200);
                var camera=InkLookValidation.FixtureCamera();
                var material=new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/TrainingGround/Materials/TrainingConcrete.mat"));
                var s=InkLookValidation.Plane("Soft comparison",12,512,material); s.SetInkLook(InkLook.Soft);
                for(int z=-2;z<=2;z++)for(int x=-3;x<=3;x++)
                    s.Apply(new PaintStamp { Position=new Vector3(x*.7f,0,z*.7f), Normal=Vector3.up, Radius=1.2f, Hardness=.6f, Strength=1,
                        Team=(byte)(x>1?2:1), ShapeSeed=InkShapeAtlas.Pack((x+z+10)%32,(uint)((x+z+10)*12739)<<6) });
                for(int k=0;k<4;k++)s.Apply(new PaintStamp { Position=new Vector3(-1.5f+k,0,-2.8f),Normal=Vector3.up,Radius=.10f+k*.08f,
                    Hardness=.6f,Strength=1,Team=1,ShapeSeed=InkShapeAtlas.Pack(k+5,(uint)(k+1)*9719<<6) });
                s.FlushDisplay();
                var coverage=InkStaticUpgrade.Read(s.Mask); var visual=InkStaticUpgrade.ReadVisual(s);
                var derived=ReadDistance(s.BoundaryTexture);
                Check(derived.Min()<.49f && derived.Max()>.51f,"Boundary cache is empty or unsigned");
                File.WriteAllText(Output+"/distance-stats.txt",$"min {derived.Min()} max {derived.Max()} ramp {derived.Count(v=>v>.51f && v<.7f)}\n");
                Check(derived.Count(v=>v>.51f && v<.7f)>100,"Boundary seeds did not propagate a resolved shoulder");
                int updates=s.BoundaryUpdates; s.FlushDisplay(); s.FlushDisplay(); Check(s.BoundaryUpdates==updates,"Static surface rebuilt boundary");
                var positions=new[]{new Vector3(0,4,-5),new Vector3(1,.65f,-4),new Vector3(-3,1.3f,3),new Vector3(-3,6,5)};
                foreach(var look in new[]{InkLook.Current,InkLook.Soft})
                {
                    s.SetInkLook(look);
                    for(int view=0;view<positions.Length;view++)
                    { camera.transform.position=positions[view];camera.transform.LookAt(Vector3.zero);InkStaticValidation.Capture(camera,Output+$"/fixture-{view}-{look}.png"); }
                    Check(coverage.SequenceEqual(InkStaticUpgrade.Read(s.Mask)) && visual.SequenceEqual(InkStaticUpgrade.ReadVisual(s)),"Look changed persistent state");
                }
                foreach(var debug in new[]{InkBoundaryDebug.OuterDistance,InkBoundaryDebug.TeamDistance,InkBoundaryDebug.Normals})
                { s.SetInkBoundaryDebug(debug);InkStaticValidation.Capture(camera,Output+$"/debug-{debug}.png"); }
                s.SetInkBoundaryDebug(InkBoundaryDebug.Off);
                ValidateContour(camera,s);
                var profile=InkAppearanceProfile.Current;float height=profile.SoftHeight,width=profile.SoftWidth;
                try
                {
                    camera.transform.position=positions[2];camera.transform.LookAt(Vector3.zero);
                    foreach(float w in new[]{.08f,.12f,.16f})foreach(float h in new[]{.012f,.018f,.024f})
                    { profile.SoftWidth=w;profile.SoftHeight=h;s.SetInkLook(InkLook.Soft);InkStaticValidation.Capture(camera,Output+$"/matrix-w{w:F3}-h{h:F3}.png"); }
                }
                finally { profile.SoftHeight=height;profile.SoftWidth=width;s.SetInkLook(InkLook.Soft); }
                float maxDirtyError=0;
                for(int tile=0;tile<32;tile++)
                {
                    var stamp=new PaintStamp { Position=new Vector3((tile%4-2)*.6f,0,(tile/4-4)*.4f),Normal=Vector3.up,Radius=.25f+tile*.013f,
                        Hardness=.6f,Strength=.75f,Team=(byte)(tile%2+1),ShapeSeed=InkShapeAtlas.Pack(tile,(uint)(tile*9719)<<6) };
                    s.Apply(stamp);s.FlushDisplay();var dirty=ReadDistance(s.BoundaryTexture);
                    s.InvalidateInkBoundary();var full=ReadDistance(s.BoundaryTexture);
                    float error=dirty.Zip(full,(a,b)=>Mathf.Abs(a-b)*2*PaintSurface.BoundaryRange).Max();maxDirtyError=Mathf.Max(maxDirtyError,error);
                    Check(error<.002f,"Dirty/full distance mismatch metres: "+error);
                }
                s.Restore(coverage,visual);var restored=ReadDistance(s.BoundaryTexture);
                Check(derived.Zip(restored,(a,b)=>Mathf.Abs(a-b)).Max()<.001f,"Restore derived cache mismatch");
                s.Clear();s.FlushDisplay();s.SetInkLook(InkLook.Current);InkStaticValidation.Capture(camera,Output+"/bare-Current.png");
                s.SetInkLook(InkLook.Soft);InkStaticValidation.Capture(camera,Output+"/bare-Soft.png");
                Check(File.ReadAllBytes(Output+"/bare-Current.png").SequenceEqual(File.ReadAllBytes(Output+"/bare-Soft.png")),"Bare pixels changed");
                File.WriteAllText(Output+"/fixture-result.txt",$"PASS: state parity, static cache reuse, 32 dirty/full checks max error {maxDirtyError} m, restore cache, bare pixel equality.\n");
                Cleanup();UnityEngine.Object.DestroyImmediate(material);
                ValidateShapes();
                ValidateSeams();
                ValidateAtlasSeams();
                ValidateDirtyDependencies();
                await CaptureMap();
                foreach(string name in new[]{"Splatoon/InkSurface","Hidden/Splatoon/InkBoundaryCache"})
                    Check(!ShaderUtil.ShaderHasError(Shader.Find(name)),"Shader errors: "+name);
                File.WriteAllText(Output+"/graphics-result.txt","PASS: fixture and map captures, state parity, cache lifecycle. Visual acceptance remains user judgement.\n"+SystemInfo.graphicsDeviceName);
                EditorApplication.Exit(0);
            }
            catch(Exception e) { Debug.LogException(e);EditorApplication.Exit(1); }
        }
        static async Task CaptureMap()
        {
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath); await Task.Delay(200);
            var arena=UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();arena.InitializeRuntime();
            foreach(var s in arena.Surfaces.Values)s.SetInkLook(InkLook.Soft);
            foreach(var stamp in InkEdgeFixture.Stamps(arena))arena.Apply(stamp,true);
            foreach(var s in arena.Surfaces.Values)s.FlushDisplay();
            long peak=PaintSurface.AllocatedBytes+PaintSurface.CheckpointBytes;
            Check(peak<=256L*1048576,"Map exceeds 256 MiB");
            File.WriteAllText(Output+"/map-memory.txt",$"Surfaces {arena.Surfaces.Count}\nResident {PaintSurface.AllocatedBytes}\nCheckpoint reserve {PaintSurface.CheckpointBytes}\nPeak {peak}\n");
            var camera=new GameObject("Soft map camera").AddComponent<Camera>();camera.fieldOfView=60;camera.allowHDR=true;camera.clearFlags=CameraClearFlags.Skybox;
            var positions=new[]{new Vector3(0,5,-9),new Vector3(3,1.4f,-5),new Vector3(-11,2.5f,-20),new Vector3(-13,1,-16)};
            var targets=new[]{Vector3.zero,Vector3.zero,new Vector3(-9,1,-17),new Vector3(-9,1,-17)};
            foreach(var look in new[]{InkLook.Current,InkLook.Soft})
            {
                foreach(var s in arena.Surfaces.Values)s.SetInkLook(look);
                for(int i=0;i<positions.Length;i++) { camera.transform.position=positions[i];camera.transform.LookAt(targets[i]);InkStaticValidation.Capture(camera,Output+$"/map-{i}-{look}.png"); }
            }
            Cleanup();
        }
        static void ValidateSeams()
        {
            var material=new Material(Shader.Find("Splatoon/InkSurface"));
            var left=InkLookValidation.Plane("Seam left",4,128,material);left.transform.position=Vector3.left*2;
            var right=InkLookValidation.Plane("Seam right",4,128,material);right.transform.position=Vector3.right*2;right.SurfaceId=2;
            left.SetInkLook(InkLook.Soft);right.SetInkLook(InkLook.Soft);
            var stamp=new PaintStamp {Position=new Vector3(-.25f,0,0),Normal=Vector3.up,Radius=.55f,Hardness=.75f,Strength=1,Team=1,ShapeSeed=InkShapeAtlas.Pack(0,0)};
            left.Apply(stamp);right.Apply(stamp);left.FlushDisplay();right.FlushDisplay();
            var l=ReadDistance(left.BoundaryTexture);var r=ReadDistance(right.BoundaryTexture);
            var rightState=InkStaticUpgrade.Read(right.Mask);int rightUpdates=right.BoundaryUpdates;
            var repaint=stamp;repaint.Team=2;repaint.Position=new Vector3(-.05f,0,0);left.Apply(repaint);right.FlushDisplay();
            Check(right.BoundaryUpdates>rightUpdates,"Shared neighbour missed paint invalidation");
            Check(rightState.SequenceEqual(InkStaticUpgrade.Read(right.Mask)),"Neighbour cache modified persistent coverage");
            var incremental=ReadDistance(right.BoundaryTexture);right.InvalidateInkBoundary();var rebuilt=ReadDistance(right.BoundaryTexture);
            Check(incremental.Zip(rebuilt,(a,b)=>Mathf.Abs(a-b)).Max()*2*PaintSurface.BoundaryRange<.002f,"Shared-neighbour dirty/full mismatch");
            left.ReleaseGraphics();right.ReleaseGraphics();UnityEngine.Object.DestroyImmediate(left.gameObject);UnityEngine.Object.DestroyImmediate(right.gameObject);
            var whole=InkLookValidation.Plane("Whole seam reference",8,256,material);whole.SetInkLook(InkLook.Soft);whole.Apply(stamp);whole.FlushDisplay();
            var reference=ReadDistance(whole.BoundaryTexture);float error=0;
            for(int y=48;y<80;y++)for(int x=120;x<128;x++)
            {
                error=Mathf.Max(error,Mathf.Abs(l[(y*128+x)*2]-reference[((y+64)*256+x)*2])*2*PaintSurface.BoundaryRange);
                error=Mathf.Max(error,Mathf.Abs(r[(y*128+127-x)*2]-reference[((y+64)*256+255-x)*2])*2*PaintSurface.BoundaryRange);
            }
            Check(error<.003f,"Shared geometric seam differs from unsplit plane: "+error);
            File.WriteAllText(Output+"/seam-result.txt",$"PASS: independently painted shared coplanar surfaces vs unsplit world-space reference; max distance error {error} m.\n");
            Cleanup();UnityEngine.Object.DestroyImmediate(material);
        }
        static void ValidateShapes()
        {
            int cases=0;var material=new Material(Shader.Find("Splatoon/InkSurface"));
            foreach(bool wall in new[]{false,true})
            {
                var s=InkLookValidation.Plane("Boundary shape matrix",4,128,material);
                s.transform.rotation=wall?Quaternion.Euler(-90,0,0):Quaternion.identity;s.SetInkLook(InkLook.Soft);
                foreach(bool mirror in new[]{false,true})foreach(uint angle in new uint[]{0,8192,23017})for(int tile=0;tile<32;tile++)
                {
                    s.Clear();s.Apply(new PaintStamp {Normal=s.transform.up,Radius=1.2f,Hardness=.6f,Strength=1,Team=1,
                        ShapeSeed=InkShapeAtlas.Pack(tile,(angle<<6)|(mirror?32u:0u))});
                    var coverage=InkStaticUpgrade.Read(s.Mask);var field=ReadDistance(s.BoundaryTexture);
                    Check(field.All(float.IsFinite),"Nonfinite boundary field");
                    Check(field.Where((v,i)=>i%2==0).Any(v=>v>.51f && v<.7f),"Missing shoulder shape "+tile);
                    Check(coverage.SequenceEqual(InkStaticUpgrade.Read(s.Mask)),"Derived cache changed coverage");cases++;
                }
                s.ReleaseGraphics();s.Apply(new PaintStamp {Normal=s.transform.up,Radius=1,Hardness=.6f,Strength=1,Team=1});s.FlushDisplay();
                var block=new MaterialPropertyBlock();s.GetComponent<Renderer>().GetPropertyBlock(block);
                Check(s.Look==InkLook.Current && block.GetFloat("_InkSoftEdge")==0,"Reinitialization retained stale experimental texture binding");
                Cleanup();
            }
            UnityEngine.Object.DestroyImmediate(material);
            File.WriteAllText(Output+"/shape-matrix.txt",$"PASS: {cases} boundary fields; 32 shapes x 3 rotations x 2 mirrors x floor/wall; coverage unchanged and nonempty finite shoulders; release/reinitialize resets look.\n");
        }
        static void ValidateAtlasSeams()
        {
            var material=new Material(Shader.Find("Splatoon/InkSurface"));
            var s=InkLookValidation.Plane("Weak ink across atlas charts",32,128,material);
            InkSurfaceAtlas.Rebuild(s.GetComponent<MeshFilter>().sharedMesh,out int width,out int height);s.Resolution=width;s.ResolutionHeight=height;
            s.SetInkLook(InkLook.Soft);
            // Restore an exactly uniform coating; an authored stamp can itself
            // contain a real hole, which must not be mistaken for an atlas seam.
            var occupancy=(RenderTexture)typeof(PaintSurface).GetField("_islands",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(s);
            var occupied=InkStaticUpgrade.Read(occupancy);var uniform=new byte[width*height*4];var visual=new byte[width*height*2];
            for(int pixel=0;pixel<width*height;pixel++)if(occupied[pixel*4]>250)
            { uniform[pixel*4]=153;uniform[pixel*4+2]=1;uniform[pixel*4+3]=153;visual[pixel*2]=visual[pixel*2+1]=76; }
            s.Restore(uniform,visual);
            s.FlushDisplay();var mask=InkStaticUpgrade.Read(s.Mask);var field=ReadDistance(s.BoundaryTexture);
            int painted=0,bad=0;float minimum=1;
            for(int pixel=0;pixel<width*height;pixel++)if(mask[pixel*4+3]>140)
            { painted++;minimum=Mathf.Min(minimum,field[pixel*2]);if(field[pixel*2]<.9f)bad++; }
            Check(painted>100000,"Weak atlas fixture was empty");
            Check(bad==0,$"False outer edge in uniformly weak ink at UV seams: {bad}/{painted}, min={minimum}");
            File.WriteAllText(Output+"/atlas-seam-result.txt",$"PASS: {painted} occupied pixels at 60% coverage across packed UV charts; no false outer shoulder at chart or geometry edges.\n");
            Cleanup();UnityEngine.Object.DestroyImmediate(material);
        }
        static void ValidateContour(Camera camera,PaintSurface surface)
        {
            camera.orthographic=true;camera.orthographicSize=4;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.transform.position=Vector3.up*10;camera.transform.rotation=Quaternion.Euler(90,0,0);
            surface.SetInkBoundaryDebug(InkBoundaryDebug.ContourComparison);
            string path=Output+"/contour-comparison.png";InkStaticValidation.Capture(camera,path);
            var t=new Texture2D(2,2);t.LoadImage(File.ReadAllBytes(path));var pixels=t.GetPixels32();int w=t.width,h=t.height;
            int changed=pixels.Count(p=>(p.r>127)!=(p.g>127));
            int Components(int channel,bool ink)
            {
                var seen=new bool[pixels.Length];var queue=new int[pixels.Length];int count=0;
                bool At(int p) => ((channel==0?pixels[p].r:pixels[p].g)>127)==ink;
                for(int first=0;first<pixels.Length;first++)
                {
                    if(seen[first] || !At(first))continue;
                    int head=0,tail=0;queue[tail++]=first;seen[first]=true;
                    while(head<tail)
                    {
                        int p=queue[head++],x=p%w,y=p/w;
                        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        {
                            if(!ink && dx!=0 && dy!=0)continue;
                            int xx=x+dx,yy=y+dy;if(xx<0 || yy<0 || xx>=w || yy>=h)continue;
                            int q=yy*w+xx;if(seen[q] || !At(q))continue;seen[q]=true;queue[tail++]=q;
                        }
                    }
                    count++;
                }
                return count;
            }
            int oldInk=Components(0,true),newInk=Components(1,true),oldEmpty=Components(0,false),newEmpty=Components(1,false);
            File.WriteAllText(Output+"/contour-result.txt",$"Changed display pixels {changed}; components {oldInk}/{newInk}; empty components (including outside) {oldEmpty}/{newEmpty}.\n");
            Check(changed>0,"Contour smoothing was not active");
            Check(oldInk==newInk && oldEmpty==newEmpty,"Contour smoothing changed connected drops or holes");
            UnityEngine.Object.DestroyImmediate(t);surface.SetInkBoundaryDebug(InkBoundaryDebug.Off);
            camera.orthographic=false;camera.clearFlags=CameraClearFlags.Skybox;
        }
        static void ValidateDirtyDependencies()
        {
            var material=new Material(Shader.Find("Splatoon/InkSurface"));
            var a=InkLookValidation.Plane("Receiving face",4,128,material);
            var b=InkLookValidation.Plane("Unrelated parallel face",4,128,material);b.SurfaceId=2;b.transform.position=Vector3.up*.1f;
            a.SetInkLook(InkLook.Soft);b.SetInkLook(InkLook.Soft);a.FlushDisplay();b.FlushDisplay();int before=b.BoundaryUpdates;
            a.Apply(new PaintStamp {Normal=Vector3.up,Radius=.6f,Hardness=.6f,Strength=1,Team=1});a.FlushDisplay();b.FlushDisplay();
            Check(b.BoundaryUpdates==before,"Painting invalidated an unrelated nearby surface");
            File.WriteAllText(Output+"/dirty-dependencies.txt","PASS: nearby parallel surface without a shared edge does not rebuild when another surface is painted.\n");
            Cleanup();UnityEngine.Object.DestroyImmediate(material);
        }
        public static void BuildWindows()
        {
            bool previous=PlayerSettings.enableFrameTimingStats;
            try { InkStaticUpgrade.LoadTables();PlayerSettings.enableFrameTimingStats=true;PrototypeBuilder.BuildWindowsTo(Output+"/Windows");EditorApplication.Exit(0); }
            catch(Exception e) { Debug.LogException(e);EditorApplication.Exit(1); }
            finally { PlayerSettings.enableFrameTimingStats=previous; }
        }
        public static void CompileMobileVariants()
        {
            var apis=PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);bool automatic=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android);
            try
            {
                string output=Output+"/AndroidShaderBundle";Directory.CreateDirectory(output);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,new[]{GraphicsDeviceType.OpenGLES3,GraphicsDeviceType.Vulkan});
                var manifest=BuildPipeline.BuildAssetBundles(output,new[]{new AssetBundleBuild {assetBundleName="ink-soft",assetNames=new[]{
                    "Assets/GameResource/Environment/TrainingGround/Materials/TrainingConcrete.mat",
                    "Assets/GameResource/Environment/TrainingGround/Materials/CoverConcrete.mat",InkAppearanceProfile.AssetPath,
                    "Assets/Splatoon/Resources/InkBoundaryCache.shader"}}},BuildAssetBundleOptions.ForceRebuildAssetBundle|BuildAssetBundleOptions.StrictMode,BuildTarget.Android);
                Check(manifest!=null,"Mobile shader bundle failed");
                File.WriteAllText(Output+"/mobile-compile.txt","PASS: Android GLES3/Vulkan surface and boundary shader bundle. No device acceptance.\n");EditorApplication.Exit(0);
            }
            catch(Exception e) { Debug.LogException(e);EditorApplication.Exit(1); }
            finally { PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,apis);PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android,automatic); }
        }
    }
}
#endif
