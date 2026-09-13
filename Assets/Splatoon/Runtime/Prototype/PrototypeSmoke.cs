using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Splatoon.Networking;
using Splatoon.Config;

namespace Splatoon.Prototype
{
    // 开发构建联机测试驱动；仅在显式传入 -lanSmokeHost/-lanSmokeClient 时生效。
    public sealed class PrototypeSmoke : MonoBehaviour
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        public static bool Active { get; private set; }
        private static bool _host;
        private static string _inkCase;
        private static float _connectedAt;
        private float _lastLog;
        private bool _started, _captured, _restarted, _dumped;
        private float _captureAfter;
        private string _label;
        private float _finishedAt;
        private float _leaveAfter;
        private int _cycles;
        private bool _cycling;
        private string _address;
        private ushort _port;
        private async UniTaskVoid Start()
        {
            var args = Environment.GetCommandLineArgs();
            _host = args.Contains("-lanSmokeHost"); Active = _host || args.Contains("-lanSmokeClient");
            _inkCase = Arg(args, "-inkSmokeCase", "combat");
            _captureAfter = float.Parse(Arg(args, "-inkCaptureAfter", _inkCase == "combat" ? "43" : "6.5"), System.Globalization.CultureInfo.InvariantCulture);
            _label = Arg(args, "-inkLabel", _host ? "host" : "client");
            if (!Active) return;
            var app = PrototypeApp.Current;
            await UniTask.WaitUntil(() => app.Ready || (!app.Busy && !string.IsNullOrEmpty(app.Error)));
            if (!app.Ready) { Debug.LogError("[SMOKE] Startup failed"); Application.Quit(2); return; }
            string address = Arg(args,"-lanAddress","127.0.0.1");
            ushort port=ushort.Parse(Arg(args,"-lanPort","7788"));
            _address=address;_port=port;
            _leaveAfter=float.Parse(Arg(args,"-lanLeaveAfter","0"),System.Globalization.CultureInfo.InvariantCulture);
            _cycles=int.Parse(Arg(args,"-lanCycles","0"));
            float cancelAfter=float.Parse(Arg(args,"-lanCancelAfter","0"),System.Globalization.CultureInfo.InvariantCulture);
            string roomCode=Arg(args,"-lanRoomCode","");
            var connection=!_host && !string.IsNullOrEmpty(roomCode) ? app.ConnectRoomCode(roomCode) : app.Connect(_host,address,port);
            if(cancelAfter>0){await UniTask.Delay(TimeSpan.FromSeconds(cancelAfter));app.CancelConnection();}
            await connection;
            if (!app.InRoom) { Debug.LogError("[SMOKE] Connect failed: "+app.Error); Application.Quit(3); return; }
            _connectedAt=Time.realtimeSinceStartup;
            Debug.Log("[SMOKE] Connected");
        }
        private static string Arg(string[] args,string key,string fallback)
        { int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback; }
        public static void ModifyInput(PrototypePlayer p, ref PlayerInputFrame frame)
        {
            if (!Active) return;
            float t=Time.realtimeSinceStartup-_connectedAt;
            if (_inkCase == "inkperf") { frame.Move = Vector2.zero; frame.Look = new Vector2(p.Snapshot.Value.Team == 1 ? 0 : 180, 55); frame.Fire = true; frame.Swim = false; return; }
            if (_inkCase == "observer") { frame.Move = Vector2.zero; frame.Look = new Vector2(0, 45); frame.Fire = frame.Swim = false; return; }
            if (_inkCase == "map") { frame.Move = Vector2.zero; frame.Look = new Vector2(p.Snapshot.Value.Team == 1 ? 0 : 180, 12); frame.Fire = frame.Swim = false; return; }
            if (_inkCase == "surfaces")
            {
                frame.Move = t < 5 && p.Snapshot.Value.Position.x < 13.8f ? new Vector2(.7f, 0) : Vector2.zero;
                frame.Look = t < 5 ? new Vector2(0, 55) : new Vector2(90, 0);
                frame.Fire = t < 20; frame.Swim = false;
                return;
            }
            frame.Move=Vector2.zero; frame.Look=new Vector2(p.Snapshot.Value.Team==1?0:180,55);frame.Fire=false;frame.Swim=false;
            if (t<5) {frame.Fire=true;frame.Move=new Vector2(0,.5f);}
            else if(t<10) {frame.Swim=true;frame.Move=new Vector2(0,.5f);}
            else if(t<18) {frame.Fire=true;frame.Move=new Vector2(.6f,0);}
            else if(t<22) {frame.Move=new Vector2(-.6f,0);frame.JumpSequence=1;}
            var match=PrototypeMatch.Current;
            if(match!=null && match.State.Value.Phase==MatchPhase.Playing && match.State.Value.Round==1)
            {
                double elapsed=GameplayConfig.Mode.MatchSeconds-(match.State.Value.EndsAt-p.NetworkManager.ServerTime.Time);
                var state=p.Snapshot.Value;
                frame.Fire=false;frame.Swim=false;frame.Move=Vector2.zero;
                if(elapsed<3)
                    frame.Move.x=Mathf.Abs(state.Position.x)>.2f?Mathf.Sign(-state.Position.x)*(state.Team==1?1:-1):0;
                else if(elapsed<6) {frame.Fire=true;frame.Move.y=.5f;}
                else if(elapsed<9) {frame.Swim=true;frame.Move.y=.2f;}
                else if(elapsed<24 && _host)
                {
                    var target=UnityEngine.Object.FindObjectsByType<PrototypePlayer>(FindObjectsSortMode.None).FirstOrDefault(x=>x.OwnerClientId!=p.OwnerClientId);
                    if(target!=null)
                    {
                        float distance = Vector3.Distance(state.Position, target.Snapshot.Value.Position);
                        var aim=target.Snapshot.Value.Position+Vector3.up*(.9f + .5f * GameplayConfig.DefaultHero.ProjectileGravity * Mathf.Pow(distance / 22.5f, 2));
                        for(int n=0;n<3;n++)
                        {
                            var origin=PrototypePlayer.CameraPosition(state.Position+Vector3.up*1.5f,Quaternion.Euler(frame.Look.y,frame.Look.x,0));
                            var d=aim-origin;frame.Look=new Vector2(Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg,-Mathf.Atan2(d.y,new Vector2(d.x,d.z).magnitude)*Mathf.Rad2Deg);
                        }
                        frame.Fire=true;
                        if (distance > 6) frame.Move.y = 1;
                    }
                }
            }
        }
        private void Update()
        {
            if (!Active || !PrototypeApp.Current.InRoom || PrototypeMatch.Current==null) return;
            var match=PrototypeMatch.Current;var s=match.State.Value;float t=Time.realtimeSinceStartup-_connectedAt;
            if (_inkCase == "inkperf") InkPerformanceSmoke.Tick(match);
            if (_inkCase == "map")
            {
                try { TrainingGroundSmoke.Tick(match); }
                catch (Exception e) { Debug.LogException(e); Application.Quit(5); return; }
            }
            if(_leaveAfter>0&&t>_leaveAfter&&!_cycling){Cycle().Forget();return;}
            if (_inkCase == "combat" && _host&&!_started&&s.PlayerCount>=2&&t>25) {match.StartRound();_started=true;}
            if (Time.realtimeSinceStartup-_lastLog>2)
            {
                _lastLog=Time.realtimeSinceStartup;var p=PrototypePlayer.Local.Snapshot.Value;
                int walls = PrototypeArena.Current.Surfaces.Values.Count(x => !x.Scores && x.HasPaint);
                Debug.Log($"[SMOKE] phase={s.Phase} round={s.Round} players={s.PlayerCount} pink={s.PinkArea} blue={s.BlueArea} hash={match.Arena.OwnershipHash()} hp={p.Health:F0} ink={p.Ink:F1} swim={p.Swimming} pos={p.Position} cells={match.Arena.CellCount} paintSeq={match.AppliedPaintSequence} walls={walls} fps={1f/Time.smoothDeltaTime:F1} rtMiB={Splatoon.Painting.PaintSurface.AllocatedBytes/1048576f:F1}");
            }
            if (_inkCase != "inkperf" && !_dumped && t > 25 && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                _dumped = true;
                foreach (var surface in PrototypeArena.Current.Surfaces.Values)
                {
                    if (!surface.HasPaint || surface.Mask == null) continue;
                    int surfaceId = surface.SurfaceId;
                    UnityEngine.Rendering.AsyncGPUReadback.Request(surface.Mask, 0, TextureFormat.RGBA32, readback =>
                    {
                        if (readback.hasError) { Debug.LogError("[SMOKE] Paint readback failed"); return; }
                        uint hash = 2166136261;
                        foreach (byte value in readback.GetData<byte>()) hash = unchecked((hash ^ value) * 16777619);
                        Debug.Log($"[SMOKE-RT] surface={surfaceId} hash={hash}");
                    });
                }
            }
            if(!_captured&&t>_captureAfter&&!Application.isBatchMode)
            {
                _captured=true;
                var path=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"..","smoke-"+_label+".png"));
                CaptureWorld(path);Debug.Log("[SMOKE] Screenshot: "+path);
            }
            if(_host&&s.Phase==MatchPhase.Finished&&!_restarted)
            {
                if(_finishedAt==0){_finishedAt=Time.realtimeSinceStartup;Debug.Log("[SMOKE] Three-minute round completed");}
                if(Time.realtimeSinceStartup-_finishedAt>5){_restarted=true;match.StartRound();Debug.Log("[SMOKE] Restart requested");}
            }
        }
        private static void CaptureWorld(string path)
        {
            // Hidden Windows players can have a black backbuffer. Submit an explicit URP render
            // request so visual verification does not require taking focus from the user's apps.
            var camera=Camera.main;
            if(camera==null){Debug.LogError("[SMOKE] No active gameplay camera");return;}
            var target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);
            var image=new Texture2D(1280,720,TextureFormat.RGB24,false);
            var previous=RenderTexture.active;
            try
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,new UnityEngine.Rendering.RenderPipeline.StandardRequest{destination=target});
                RenderTexture.active=target;image.ReadPixels(new Rect(0,0,1280,720),0,0);image.Apply();
                System.IO.File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally {RenderTexture.active=previous;target.Release();Destroy(target);Destroy(image);}
        }
        private async UniTask Cycle()
        {
            _cycling=true;var app=PrototypeApp.Current;
            await app.Leave();Debug.Log("[SMOKE] Clean leave completed. spawned="+app.Manager.SpawnManager?.SpawnedObjects.Count+" paintRtBytes="+Splatoon.Painting.PaintSurface.AllocatedBytes);
            if(Splatoon.Painting.PaintSurface.AllocatedBytes!=0){Debug.LogError("[SMOKE] Paint RT leaked after scene unload");Application.Quit(6);return;}
            if(_cycles--<=0){Application.Quit(0);return;}
            await app.Connect(_host,_address,_port);
            if(!app.InRoom){Debug.LogError("[SMOKE] Reconnect failed: "+app.Error);Application.Quit(4);return;}
            Debug.Log("[SMOKE] Reconnected after cleanup");_connectedAt=Time.realtimeSinceStartup;_cycling=false;
        }
        private void OnDestroy() { if (_inkCase == "inkperf") InkPerformanceSmoke.Reset(); Active=false; }
#endif
    }
}
