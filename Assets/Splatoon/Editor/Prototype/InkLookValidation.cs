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
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Editor
{
    public static class InkLookValidation
    {
        const string Output="Reports/InkLook/Screenshots";
        public static void Run()
        {
            InkLookUpgrade.LoadConfig();Directory.CreateDirectory(Output);
            ValidateAtlasLifecycle();ValidateCpuGpu();ValidateAtlasFaces();CompareReference();CaptureTrainingGround();
            EditorApplication.Exit(0);
        }
        static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException("[INK-LOOK] "+message);}
        public static Texture2D Read(RenderTexture texture)
        {
            var old=RenderTexture.active;RenderTexture.active=texture;
            var result=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false,true);
            result.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0);result.Apply();RenderTexture.active=old;return result;
        }
        static byte[] Bytes(RenderTexture texture){var read=Read(texture);var result=read.GetRawTextureData<byte>().ToArray();UnityEngine.Object.DestroyImmediate(read);return result;}
        static void ValidateAtlasLifecycle()
        {
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);var arena=UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();arena.InitializeRuntime();
            long checkpointBytes=0;int surfaces=0;
            foreach(var surface in arena.Surfaces.Values)
            {
                var mesh=surface.GetComponent<MeshFilter>().sharedMesh;int[] t=mesh.triangles;var v=mesh.vertices;
                Vector3 a=v[t[0]],b=v[t[1]],c=v[t[2]];
                var stamp=new PaintStamp{SurfaceId=surface.SurfaceId,Position=surface.transform.TransformPoint((a+b+c)/3),Normal=surface.transform.TransformDirection(Vector3.Cross(b-a,c-a).normalized),Radius=1.5f,Hardness=.01f,Strength=.3f,Team=1};
                surface.Apply(stamp);var initial=Bytes(surface.Mask);
                Check(initial.Where((_,i)=>i%4==3).Any(a=>a>0&&a<128),surface.name+" lacks continuous ink");
                surface.Apply(stamp);var continuous=Bytes(surface.Mask);surface.Clear();surface.Restore(initial);surface.Apply(stamp);
                Check(continuous.SequenceEqual(Bytes(surface.Mask)),surface.name+" restore then paint differs");
                surface.FlushDisplay(); Check(Bytes(surface.DisplayMask).Any(b=>b>0),surface.name+" empty display");
                surface.Clear();Check(Bytes(surface.Mask).All(b=>b==0)&&Bytes(surface.DisplayMask).All(b=>b==0),surface.name+" clear left paint");
                checkpointBytes+=surface.TextureBytes;surfaces++;
            }
            long peak=PaintSurface.AllocatedBytes+checkpointBytes;
            Check(peak<=128L*1048576,"RT peak exceeds 128 MiB");
            Debug.Log($"[INK-LOOK] GPU surfaces={surfaces} continuous/restore/continue/clear=PASS residentMiB={PaintSurface.AllocatedBytes/1048576f:F2} peakMiB={peak/1048576f:F2}");
            foreach(var surface in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)) surface.ReleaseGraphics();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);Check(PaintSurface.AllocatedBytes==0,"RT leaked after unload");
        }
        static PaintSurface Plane(string name,float size,int resolution,Material material,bool referenceUV=false)
        {
            var go=new GameObject(name);var mesh=new Mesh{name=name};float h=size/2;
            mesh.vertices=new[]{new Vector3(-h,0,-h),new Vector3(-h,0,h),new Vector3(h,0,h),new Vector3(h,0,-h)};
            mesh.triangles=new[]{0,1,2,0,2,3};mesh.uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right};
            if(referenceUV)mesh.uv=mesh.vertices.Select(v=>InkCoverage.DetailUV(v,Vector3.up,.034424f)).ToArray();
            mesh.uv2=mesh.uv;mesh.RecalculateNormals();mesh.RecalculateTangents();
            go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=material;
            var surface=go.AddComponent<PaintSurface>();surface.SurfaceId=1;surface.Resolution=resolution;surface.PainterShader=Shader.Find("Splatoon/InkTexturePainter");surface.ExtendShader=Shader.Find("TNTC/ExtendIslands");surface.DisplayShader=Shader.Find("Splatoon/InkDisplay");return surface;
        }
        static void ValidateCpuGpu()
        {
            var material=new Material(Shader.Find("Splatoon/InkSurface"));var surface=Plane("CPU-GPU fixture",8,64,material);
            var grid=new SurfaceOwnershipGrid(new Vector2(8,8),.125f,null);
            var stamps=new List<PaintStamp>();
            for(int n=0;n<12;n++)stamps.Add(new PaintStamp{Position=new Vector3((n%3-1)*.5f,0,(n%4-2)*.4f),Normal=Vector3.up,Radius=1.5f,Hardness=.01f,Strength=.25f,Team=(byte)(n%4==0?2:1)});
            foreach(var stamp in stamps){surface.Apply(stamp);grid.Apply(stamp,Matrix4x4.identity,.5f,.034424f,110);}
            var probe=new Material(Shader.Find("Hidden/Splatoon/InkCoverageProbe"));
            var owners=new RenderTexture(64,64,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);owners.Create();
            probe.SetMatrix("_LocalToWorld",Matrix4x4.identity);probe.SetVector("_Normal",Vector3.up);Graphics.Blit(surface.Mask,owners,probe);
            var gpuOwners=Read(owners);var ownerPixels=gpuOwners.GetPixels32();
            var tex=Read(surface.Mask);var pixels=tex.GetPixels32();int wrong=0,maxDelta=0;
            for(int i=0;i<pixels.Length;i++)
            {
                var p=pixels[i];int o=i*4;maxDelta=Math.Max(maxDelta,Math.Max(Math.Abs(p.r-grid.State[o]),Math.Abs(p.g-grid.State[o+1])));
                if(ownerPixels[i].r!=grid.Cells[i])wrong++;
            }
            Check(maxDelta<=1&&wrong<=4,$"CPU/GPU mismatch delta={maxDelta} owners={wrong}");
            Debug.Log($"[INK-LOOK] CPU/GPU samples={pixels.Length} maxByteDelta={maxDelta} ownerMismatch={wrong}");
            UnityEngine.Object.DestroyImmediate(gpuOwners);
            foreach(var matrix in new[]{Matrix4x4.TRS(new Vector3(-14,3,-28),Quaternion.Euler(-26.565f,0,0),Vector3.one),Matrix4x4.Translate(new Vector3(14,0,28))})
            {
                Vector3 normal=matrix.MultiplyVector(Vector3.up);probe.SetMatrix("_LocalToWorld",matrix);probe.SetVector("_Normal",normal);Graphics.Blit(surface.Mask,owners,probe);
                gpuOwners=Read(owners);ownerPixels=gpuOwners.GetPixels32();wrong=0;
                for(int i=0;i<pixels.Length;i++)if(ownerPixels[i].r!=InkCoverage.Owner(pixels[i],matrix.MultiplyPoint3x4(grid.Center(i)),normal,.5f,.034424f,110))wrong++;
                Check(wrong<=4,$"GPU world-space noise ownership differs at {matrix.GetColumn(3)}: {wrong}");
                Debug.Log($"[INK-LOOK] GPU coverage noise translated/slope samples={pixels.Length} ownerMismatch={wrong}");UnityEngine.Object.DestroyImmediate(gpuOwners);
            }
            owners.Release();UnityEngine.Object.DestroyImmediate(owners);UnityEngine.Object.DestroyImmediate(probe);
            UnityEngine.Object.DestroyImmediate(tex);surface.ReleaseGraphics();UnityEngine.Object.DestroyImmediate(surface.gameObject);UnityEngine.Object.DestroyImmediate(material);
        }
        static void ValidateAtlasFaces()
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);var mesh=UnityEngine.Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            mesh.vertices=mesh.vertices.Select(p=>Vector3.Scale(p,new Vector3(32,3,.5f))).ToArray();InkSurfaceAtlas.Rebuild(mesh,out int width,out int height);go.GetComponent<MeshFilter>().sharedMesh=mesh;
            var surface=go.AddComponent<PaintSurface>();surface.Resolution=width;surface.ResolutionHeight=height;surface.PainterShader=Shader.Find("Splatoon/InkTexturePainter");surface.DisplayShader=Shader.Find("Splatoon/InkDisplay");
            var stamp=new PaintStamp{Position=new Vector3(-16+32/3f,0,.25f),Normal=Vector3.forward,Radius=1.4f,Hardness=.01f,Strength=.8f,Team=1};surface.Apply(stamp);surface.FlushDisplay();
            var raw=Read(surface.Mask);var display=Read(surface.DisplayMask);var pixels=raw.GetPixels32();var uv=mesh.uv2;var verts=mesh.vertices;var t=mesh.triangles;int samples=0,painted=0;
            float Cross(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
            for(int k=0;k<t.Length;k+=3)
            {
                int a=t[k],b=t[k+1],c=t[k+2];Vector2 ab=uv[b]-uv[a],ac=uv[c]-uv[a];float det=Cross(ab,ac);if(Mathf.Abs(det)<1e-10f)continue;
                Vector3 normal=Vector3.Cross(verts[b]-verts[a],verts[c]-verts[a]).normalized;
                int x0=Mathf.FloorToInt(Mathf.Min(uv[a].x,Mathf.Min(uv[b].x,uv[c].x))*width),x1=Mathf.CeilToInt(Mathf.Max(uv[a].x,Mathf.Max(uv[b].x,uv[c].x))*width);
                int y0=Mathf.FloorToInt(Mathf.Min(uv[a].y,Mathf.Min(uv[b].y,uv[c].y))*height),y1=Mathf.CeilToInt(Mathf.Max(uv[a].y,Mathf.Max(uv[b].y,uv[c].y))*height);
                for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
                {
                    Vector2 p=new((x+.5f)/width,(y+.5f)/height);Vector2 ap=p-uv[a];float v=Cross(ap,ac)/det,w=Cross(ab,ap)/det;if(v<.00001f||w<.00001f||v+w>.99999f)continue;
                    Vector3 world=verts[a]+v*(verts[b]-verts[a])+w*(verts[c]-verts[a]);float f=Vector3.Dot(normal,stamp.Normal)<.5f?0:InkBrush.Coverage(Vector3.Distance(world,stamp.Position),stamp.Radius,stamp.Hardness,stamp.Strength);
                    int expected=Mathf.FloorToInt(255*f+.5f);Check(Math.Abs(pixels[y*width+x].a-expected)<=1,"Atlas face leaked or missed paint");samples++;if(expected>100)painted++;
                }
            }
            // Two sides of the split must retain coverage through bilinear display padding.
            foreach(float side in new[]{-.001f,.001f})
            {
                Vector3 point=stamp.Position+Vector3.right*side;bool found=false;
                for(int k=0;k<t.Length;k+=3)
                {
                    Vector3 a=verts[t[k]],b=verts[t[k+1]],c=verts[t[k+2]];if(Mathf.Abs(a.z-.25f)>.001f||Mathf.Abs(b.z-.25f)>.001f||Mathf.Abs(c.z-.25f)>.001f)continue;
                    Vector2 ab=new(b.x-a.x,b.y-a.y),ac=new(c.x-a.x,c.y-a.y),ap=new(point.x-a.x,point.y-a.y);float det=Cross(ab,ac);if(Mathf.Abs(det)<1e-8f)continue;
                    float v=Cross(ap,ac)/det,w=Cross(ab,ap)/det;if(v<0||w<0||v+w>1)continue;
                    Vector2 coord=uv[t[k]]+v*(uv[t[k+1]]-uv[t[k]])+w*(uv[t[k+2]]-uv[t[k]]);
                    Check(display.GetPixelBilinear(coord.x,coord.y).a>.75f,"Atlas display seam lost coverage");found=true;break;
                }
                Check(found,"Atlas seam sample missing");
            }
            Check(painted>100,"Atlas fixture missed seam brush");Debug.Log($"[INK-LOOK] Atlas seam/backface samples={samples} painted={painted} PASS");
            surface.ReleaseGraphics();UnityEngine.Object.DestroyImmediate(raw);UnityEngine.Object.DestroyImmediate(display);UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(mesh);
        }
        static Camera FixtureCamera()
        {
            var camera=new GameObject("Comparison camera").AddComponent<Camera>();camera.transform.position=new Vector3(0,7,-9);camera.transform.LookAt(new Vector3(0,0,.5f));camera.fieldOfView=40;camera.clearFlags=CameraClearFlags.Skybox;camera.allowHDR=true;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing=false;
            var sun=new GameObject("Comparison sun").AddComponent<Light>();sun.type=LightType.Directional;sun.color=new Color(1,.95686275f,.8392157f);sun.intensity=2;sun.shadows=LightShadows.Soft;sun.transform.rotation=Quaternion.Euler(45,-30,0);
            RenderSettings.sun=sun;RenderSettings.skybox=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Sky/Sky_8.mat");RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.55f,.62f,.64f);RenderSettings.ambientEquatorColor=new Color(.38f,.4f,.42f);RenderSettings.ambientGroundColor=new Color(.18f,.2f,.22f);RenderSettings.fog=false;
            return camera;
        }
        static void CompareReference()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);var camera=FixtureCamera();
            var original=AssetDatabase.LoadAssetAtPath<Material>("Assets/GameResource/Environment/Ink/Materials/PaintableWall.mat");
            var referenceMat=new Material(original){shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/Splatoon/Tests/VisualReference/ReferencePaintable.shadergraph")};
            var currentMat=new Material(original){shader=Shader.Find("Splatoon/InkSurface")};
            var current=Plane("Migrated ink",20,1024,currentMat,true);var reference=Plane("Reference ink",20,1024,referenceMat,true);
            var referenceRenderer=reference.GetComponent<Renderer>();var currentRenderer=current.GetComponent<Renderer>();referenceRenderer.enabled=false;
            var raw=new RenderTexture(1024,1024,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear){filterMode=FilterMode.Bilinear};raw.Create();
            var scratch=new RenderTexture(raw.descriptor);scratch.Create();var old=RenderTexture.active;RenderTexture.active=raw;GL.Clear(false,true,Color.clear);RenderTexture.active=old;
            var painter=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Splatoon/Tests/VisualReference/ReferenceTexturePainter.shader"));referenceMat.SetTexture("_MaskTexture",raw);
            int count=0;
            void Paint(Vector3 p,float radius,byte team=1)
            {
                current.Apply(new PaintStamp{Position=p,Normal=Vector3.up,Radius=radius,Hardness=.01f,Strength=1,Team=team});
                painter.SetFloat("_PrepareUV",0);painter.SetVector("_PainterPosition",p);painter.SetFloat("_Radius",radius);painter.SetFloat("_Hardness",.01f);painter.SetFloat("_Strength",1);painter.SetColor("_PainterColor",PrototypeArena.TeamColor(team));painter.SetTexture("_MainTex",scratch);
                var cmd=CommandBufferPool.Get("Reference brush");cmd.Blit(raw,scratch);cmd.SetRenderTarget(raw);cmd.DrawMesh(reference.GetComponent<MeshFilter>().sharedMesh,Matrix4x4.identity,painter);Graphics.ExecuteCommandBuffer(cmd);CommandBufferPool.Release(cmd);count++;
            }
            void Pair(string name)
            {
                currentRenderer.enabled=false;referenceRenderer.enabled=true;Capture(camera,"reference-"+name);
                referenceRenderer.enabled=false;currentRenderer.enabled=true;Capture(camera,"fixed-"+name);
            }
            Paint(new Vector3(0,0,-1),1.5f);Pair("single");
            Paint(new Vector3(1,0,-.7f),1.5f);Paint(new Vector3(-.8f,0,-.6f),1.2f);Pair("fusion");
            for(int i=0;i<5;i++)Paint(new Vector3(0,0,-1),1.5f);Pair("repeat");
            for(int z=-5;z<=5;z++)for(int x=-5;x<=5;x++)Paint(new Vector3(x*.9f,0,z*.9f),1.5f);
            Pair("pink-field");
            for(int z=-4;z<=4;z++)for(int x=1;x<=5;x++)Paint(new Vector3(x*.85f,0,z*.85f),1.4f,2);
            referenceRenderer.enabled=false;currentRenderer.enabled=true;Capture(camera,"fixed-pink-mint");
            camera.transform.position=new Vector3(7,5,-6);camera.transform.LookAt(Vector3.zero);Capture(camera,"fixed-pink-mint-angle");
            Directory.CreateDirectory(Output+"/Orbit");
            for(int i=0;i<36;i++)
            {
                float angle=(i-18)*.7f*Mathf.Deg2Rad;camera.transform.position=new Vector3(9*Mathf.Sin(angle),6,-9*Mathf.Cos(angle));camera.transform.LookAt(Vector3.zero);
                Capture(camera,"Orbit/frame-"+i.ToString("D3"));
            }
            Debug.Log($"[INK-LOOK] Reference comparison captured stamps={count} 1920x1080 FOV=40 identical geometry/lights/brush");
            raw.Release();scratch.Release();UnityEngine.Object.DestroyImmediate(raw);UnityEngine.Object.DestroyImmediate(scratch);UnityEngine.Object.DestroyImmediate(painter);
            current.ReleaseGraphics();reference.ReleaseGraphics();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);UnityEngine.Object.DestroyImmediate(referenceMat);UnityEngine.Object.DestroyImmediate(currentMat);
        }
        public static void Capture(Camera camera,string name)
        {
            foreach(var surface in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None))surface.FlushDisplay();
            var rt=new RenderTexture(1920,1080,24,RenderTextureFormat.ARGB32);rt.Create();
            var request=new UniversalRenderPipeline.SingleCameraRequest{destination=rt};RenderPipeline.SubmitRenderRequest(camera,request);RenderPipeline.SubmitRenderRequest(camera,request);
            Check(!ShaderUtil.ShaderHasError(Shader.Find("Splatoon/InkSurface")),"Ink surface shader compilation failed");
            var image=Read(rt);File.WriteAllBytes(Output+"/"+name+".png",image.EncodeToPNG());UnityEngine.Object.DestroyImmediate(image);rt.Release();UnityEngine.Object.DestroyImmediate(rt);
        }
        static void CaptureTrainingGround()
        {
            EditorSceneManager.OpenScene(TrainingGroundBuilder.ScenePath);var arena=UnityEngine.Object.FindFirstObjectByType<PrototypeArena>();arena.InitializeRuntime();
            foreach(var surface in arena.Surfaces.Values)
            {
                var mesh=surface.GetComponent<MeshFilter>().sharedMesh;var verts=mesh.vertices;var t=mesh.triangles;
                if(surface.Scores)
                {
                    for(int z=-2;z<=2;z++)for(int x=-2;x<=2;x++)
                    {Vector3 p=surface.transform.TransformPoint(new Vector3(x*.85f,0,z*.85f));arena.Apply(new PaintStamp{SurfaceId=surface.SurfaceId,Position=p,Normal=surface.transform.up,Radius=1.5f,Hardness=.01f,Strength=1,Team=(byte)(x<=0?1:2)},true);}
                }
                else for(int i=0;i<t.Length;i+=3)
                {
                    Vector3 a=verts[t[i]],b=verts[t[i+1]],c=verts[t[i+2]];Vector3 p=surface.transform.TransformPoint((a+b+c)/3);
                    if(Mathf.Abs(p.z)>12||p.y<.1f)continue;
                    surface.Apply(new PaintStamp{Position=p,Normal=surface.transform.TransformDirection(Vector3.Cross(b-a,c-a).normalized),Radius=1.5f,Hardness=.01f,Strength=1,Team=(byte)(p.x<0?1:2)});
                }
            }
            var camera=Camera.main;camera.transform.position=new Vector3(-5,5,-9);camera.transform.LookAt(new Vector3(0,1,0));Capture(camera,"training-ground");
            camera.transform.position=new Vector3(-14,2,-4);camera.transform.LookAt(new Vector3(-16,1.4f,1));Capture(camera,"training-wall");
            foreach(var surface in UnityEngine.Object.FindObjectsByType<PaintSurface>(FindObjectsSortMode.None)) surface.ReleaseGraphics();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);Check(PaintSurface.AllocatedBytes==0,"comparison RT leaked");
        }
    }
}
#endif
