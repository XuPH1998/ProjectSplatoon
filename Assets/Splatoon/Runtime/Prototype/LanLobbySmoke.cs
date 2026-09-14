#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Cysharp.Threading.Tasks;
using Splatoon.Config;
using Splatoon.Networking;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Prototype
{
    // Explicitly started by the editor validation menu, never active in a normal play session.
    public sealed class LanLobbySmoke : MonoBehaviour
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        readonly List<UdpLanDiscoveryService> _hosts=new();
        readonly List<string> _checks=new();
        readonly List<Guid> _roomIds=new();
        UdpLanDiscoveryService _observer;
        EditorWindow _view; object _sizes; int _added=-1,_oldSize;
        string _error;
        [StructLayout(LayoutKind.Sequential)] struct CursorPoint { public int X,Y; }
        CursorPoint _previousCursor;
        [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint point);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if(!SessionState.GetBool("RoomDiscovery.PlaySmoke",false))return;
            SessionState.SetBool("RoomDiscovery.PlaySmoke",false);
            var go=new GameObject("LanLobbySmoke");DontDestroyOnLoad(go);go.AddComponent<LanLobbySmoke>();
        }
        void Check(bool value,string message){if(!value)throw new Exception(message);_checks.Add(message);}
        void Update(){foreach(var host in _hosts)host.Tick();_observer?.Tick();}
        void EditorTick(){_view?.Repaint();}
        void ConfigureView()
        {
            _view=Array.Find(Resources.FindObjectsOfTypeAll<EditorWindow>(),w=>w.GetType().Name=="GameView");
            if(_view==null)throw new Exception("GameView unavailable");
            var asm=_view.GetType().Assembly;var type=asm.GetType("UnityEditor.GameViewSizes");
            var sizes=typeof(ScriptableSingleton<>).MakeGenericType(type).GetProperty("instance").GetValue(null);
            var getGroup=type.GetMethod("GetGroup",Flags);
            _sizes=getGroup.Invoke(sizes,new[]{Enum.Parse(getGroup.GetParameters()[0].ParameterType,"Standalone")});
            int count=(int)_sizes.GetType().GetMethod("GetTotalCount",Flags).Invoke(_sizes,null);
            int builtins=(int)_sizes.GetType().GetMethod("GetBuiltinCount",Flags).Invoke(_sizes,null);
            int selected=-1;
            for(int i=0;i<count;i++)
            {
                var s=_sizes.GetType().GetMethod("GetGameViewSize",Flags).Invoke(_sizes,new object[]{i});
                if((int)s.GetType().GetProperty("width",Flags).GetValue(s)==1280&&(int)s.GetType().GetProperty("height",Flags).GetValue(s)==720){selected=i;break;}
            }
            if(selected<0)
            {
                var s=Activator.CreateInstance(asm.GetType("UnityEditor.GameViewSize"),Flags,null,new[]{Enum.Parse(asm.GetType("UnityEditor.GameViewSizeType"),"FixedResolution"),(object)1280,720,"LAN validation temporary"},null);
                _sizes.GetType().GetMethod("AddCustomSize",Flags).Invoke(_sizes,new[]{s});selected=count;_added=count-builtins;
            }
            var property=_view.GetType().GetProperty("selectedSizeIndex",Flags);_oldSize=(int)property.GetValue(_view);property.SetValue(_view,selected);
            _view.Show();_view.Focus();EditorApplication.update+=EditorTick;
        }
        async UniTask Click(float x,float y)
        {
            // Native input also exercises the active Input System backend and GameView's DPI/zoom mapping.
            _view.Focus();
            var window=System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            SetForegroundWindow(window);
            Check(GetForegroundWindow()==window,"Unity is foreground before native click");
            var target=(Rect)_view.GetType().GetProperty("targetInParent",Flags).GetValue(_view);
            float dpi=EditorGUIUtility.pixelsPerPoint;
            var position=new Vector2((_view.position.x+target.x+x*target.width/1280)*dpi,(_view.position.y+target.y+y*target.height/720)*dpi);
            SetCursorPos((int)position.x,(int)position.y);
            await UniTask.Delay(100);
            mouse_event(2,0,0,0,UIntPtr.Zero);
            await UniTask.Delay(100);
            mouse_event(4,0,0,0,UIntPtr.Zero);
            await UniTask.Delay(180);
        }
        async UniTask Capture(string name)
        {
            string path=Path.GetFullPath("Reports/LanDiscovery/room-lobby-"+name+".png");
            if(File.Exists(path))File.Delete(path);
            ScreenCapture.CaptureScreenshot(path);
            await UniTask.Delay(600);
            Check(File.Exists(path),"screenshot "+name);
        }
        void SetInput(string field,string value)=>typeof(PrototypeApp).GetField(field,Flags).SetValue(PrototypeApp.Current,value);
        static UniTask Wait(Func<bool> condition)=>UniTask.WaitUntil(condition).Timeout(TimeSpan.FromSeconds(45));
        async UniTaskVoid Start()
        {
            Directory.CreateDirectory("Reports/LanDiscovery");
            GetCursorPos(out _previousCursor);
            try
            {
                await Wait(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready&&!PrototypeApp.Current.Busy);
                var app=PrototypeApp.Current;ConfigureView();
                Check(GameplayConfig.Map.Id==1&&GameplayConfig.Map.Width==32&&GameplayConfig.Map.Length==64,"TbMap initialized through Addressables with unchanged map");
                for(int i=0;i<10;i++)
                {
                    var a=new LanRoomAdvertisement{RoomId=Guid.NewGuid(),GamePort=(ushort)(19300+i),ModeId=1,MapId=1,ModeName=GameplayConfig.Mode.Name,MapName=GameplayConfig.Map.Name,
                        PlayerCount=i==0?4:1,MaxPlayers=4,Phase=i==2?MatchPhase.Playing:i==3?MatchPhase.Finished:MatchPhase.Practice,
                        PlayerProtocol=i==1?99:(int)PlayerSnapshot.ProtocolVersion,PaintProtocol=GameplayContentSignature.PaintProtocolVersion,
                        ConfigDigest=LanDiscoveryProtocol.Digest(LubanConfigService.Current.ContentSignature)};
                    var host=new UdpLanDiscoveryService();host.StartAdvertising(()=>a);_hosts.Add(host);_roomIds.Add(a.RoomId);
                }
                app.Discovery.Refresh();await Wait(()=>app.Discovery.Rooms.Count>=10);await Capture("list");
                await app.JoinDiscoveredRoom(_roomIds[0]);Check(!app.Busy&&!app.InRoom&&app.Error.Contains("已满"),"full room cannot join");
                await app.JoinDiscoveredRoom(_roomIds[1]);Check(!app.Busy&&!app.InRoom&&app.Error.Contains("不兼容"),"incompatible room cannot join");
                typeof(PrototypeApp).GetField("_roomScroll",Flags).SetValue(app,new Vector2(0,250));await UniTask.Delay(250);await Capture("scroll");
                typeof(PrototypeApp).GetField("_roomScroll",Flags).SetValue(app,Vector2.zero);
                await UniTask.Delay(300);
                var selected=app.Discovery.Rooms.First(r=>r.Advertisement.RoomId==_roomIds[2]);var selectedEndpoint=selected.BestEndpoint;
                int row=app.Discovery.Rooms.ToList().IndexOf(selected);
                await Click(740,240+row*58+27);Check(app.Busy,"room row button starts connection, error="+app.Error);
                await Wait(()=>app.Session.State==NetworkSessionState.Joining);
                var transport=(Unity.Netcode.Transports.UTP.UnityTransport)app.Manager.NetworkConfig.NetworkTransport;
                Check(transport.ConnectionData.Address==selectedEndpoint.Address&&transport.ConnectionData.Port==selectedEndpoint.Port,"list join uses measured endpoint and advertised game port");
                await Click(1173,608);await Wait(()=>!app.Busy);Check(!app.InRoom,"cancel list connection returns to lobby");
                await Wait(()=>app.Discovery.Rooms.Count>=10);Check(true,"discovery resumes after canceled connection");
                foreach(var host in _hosts)host.Stop();await UniTask.Delay(6300);
                Check(app.Discovery.Rooms.Count==0,"departed rooms expire after six seconds");
                await app.Connect(true,"127.0.0.1",47777);Check(!app.InRoom&&app.Error.Contains("47777"),"reserved game port rejected before loading");
                SetInput("_port","19477");await Click(1050,195);await Wait(()=>app.InRoom&&!app.Busy);
                Check(app.Manager.IsHost,"create button starts real NGO host");
                _observer=new UdpLanDiscoveryService();_observer.StartBrowsing();
                await Wait(()=>_observer.Rooms.Count==1);
                var live=_observer.Rooms[0].Advertisement;Guid firstId=live.RoomId;
                Check(live.PlayerCount==1&&live.MaxPlayers==4&&live.GamePort==19477,"advertisement uses actual host count and game port");
                Check(live.MapName==GameplayConfig.Map.Name&&live.Phase==MatchPhase.Practice,"advertisement identifies map and warmup");
                var match=PrototypeMatch.Current;var state=match.State.Value;state.Phase=MatchPhase.Playing;state.EndsAt=app.Manager.ServerTime.Time+180;match.State.Value=state;
                _observer.Refresh();await Wait(()=>_observer.Rooms[0].Advertisement.Phase==MatchPhase.Playing);Check(true,"playing phase is refreshed");
                state=match.State.Value;state.Phase=MatchPhase.Finished;match.State.Value=state;
                _observer.Refresh();await Wait(()=>_observer.Rooms[0].Advertisement.Phase==MatchPhase.Finished);Check(true,"finished phase remains discoverable");
                // Exercise the actual admission callback: the host occupies one slot and pending admissions reserve the rest.
                for(ulong client=501;client<=504;client++)
                {
                    var response=new NetworkManager.ConnectionApprovalResponse();
                    app.Manager.ConnectionApprovalCallback(new NetworkManager.ConnectionApprovalRequest{ClientNetworkId=client,Payload=app.Manager.NetworkConfig.ConnectionData},response);
                    Check(response.Approved==(client<504),"capacity approval client "+client);
                }
                app.CaptureMouse(false);await Click(640,455);await Wait(()=>!app.InRoom&&!app.Busy);Check(true,"leave button returns to lobby");
                await UniTask.Delay(6300);Check(_observer.Rooms.Count==0,"real host stops advertising on leave");
                await app.Connect(true,"127.0.0.1",19477);Check(app.InRoom,"second host session starts");_observer.Refresh();await Wait(()=>_observer.Rooms.Count==1);
                Check(_observer.Rooms[0].Advertisement.RoomId!=firstId,"new host session gets a new room ID");await app.Leave();
                SetInput("_roomCodeInput",LanRoomCode.Encode("127.0.0.1",19478));await Click(1050,354);await Wait(()=>app.Session.State==NetworkSessionState.Joining);
                app.CancelConnection();await Wait(()=>!app.Busy);Check(!app.InRoom,"room-code entry still connects and cancels");
                SetInput("_port","19478");SetInput("_ip","127.0.0.1");await Click(1050,410);await Click(1175,460);await Wait(()=>app.Session.State==NetworkSessionState.Joining);
                app.CancelConnection();await Wait(()=>!app.Busy);Check(!app.InRoom,"direct-IP entry still connects and cancels");
                await Capture("returned");
            }
            catch(Exception e){_error=e.ToString();Debug.LogError("[LAN-LOBBY] "+e);await Capture("failed");}
            finally
            {
                foreach(var host in _hosts)host.Dispose();_hosts.Clear();_observer?.Dispose();_observer=null;
                if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom&&!PrototypeApp.Current.Busy)await PrototypeApp.Current.Leave();
                File.WriteAllText("Reports/LanDiscovery/room-lobby-playmode.txt","passed="+(_error==null)+"\n"+string.Join("\n",_checks)+"\nerror="+_error);
                RestoreView();SetCursorPos(_previousCursor.X,_previousCursor.Y);EditorApplication.ExitPlaymode();
            }
        }
        void RestoreView()
        {
            EditorApplication.update-=EditorTick;
            if(_view!=null)_view.GetType().GetProperty("selectedSizeIndex",Flags).SetValue(_view,_oldSize);
            if(_added>=0)_sizes.GetType().GetMethod("RemoveCustomSize",Flags).Invoke(_sizes,new object[]{_added});_added=-1;
        }
        void OnDestroy(){foreach(var h in _hosts)h.Dispose();_observer?.Dispose();EditorApplication.update-=EditorTick;}
    }
}
#endif
