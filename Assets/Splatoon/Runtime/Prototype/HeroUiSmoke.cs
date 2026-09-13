#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    // Sends real IMGUI mouse events and Input System keyboard events through the production UI.
    public sealed class HeroUiSmoke : MonoBehaviour
    {
        static HeroUiSmoke _instance;
        readonly List<string> _checks = new();
        string _output;
        bool _manual;
        ushort _editorPort;
        bool _mouseCalibrated;
        Vector2? _mouseSample;
        Vector2 _mouseOffset;
        UnityEditor.EditorWindow _view;
        object _sizeGroup; int _temporarySize = -1, _previousSize;
        const System.Reflection.BindingFlags Flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
        void ConfigureView()
        {
            _view=Array.Find(Resources.FindObjectsOfTypeAll<UnityEditor.EditorWindow>(),w=>w.GetType().Name=="GameView");
            if(_view==null)_view=UnityEditor.EditorWindow.GetWindow(typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView"));
            _view.Show();_view.Focus();
            var assembly=_view.GetType().Assembly;
            var sizesType=assembly.GetType("UnityEditor.GameViewSizes");
            var singleton=typeof(UnityEditor.ScriptableSingleton<>).MakeGenericType(sizesType).GetProperty("instance").GetValue(null);
            var getGroup=sizesType.GetMethod("GetGroup",Flags);
            _sizeGroup=getGroup.Invoke(singleton,new[]{Enum.Parse(getGroup.GetParameters()[0].ParameterType,"Standalone")});
            int count=(int)_sizeGroup.GetType().GetMethod("GetTotalCount",Flags).Invoke(_sizeGroup,null);
            int width=int.Parse(HeroSelectionSmoke.Arg("-weaponUiWidth","1280")),height=int.Parse(HeroSelectionSmoke.Arg("-weaponUiHeight","720"));
            int selected=-1;
            for(int i=0;i<count;i++)
            {
                var item=_sizeGroup.GetType().GetMethod("GetGameViewSize",Flags).Invoke(_sizeGroup,new object[]{i});
                if((int)item.GetType().GetProperty("width",Flags).GetValue(item)==width&&(int)item.GetType().GetProperty("height",Flags).GetValue(item)==height){selected=i;break;}
            }
            if(selected<0)
            {
                var sizeType=assembly.GetType("UnityEditor.GameViewSize");var kind=assembly.GetType("UnityEditor.GameViewSizeType");
                var size=Activator.CreateInstance(sizeType,Flags,null,new[]{Enum.Parse(kind,"FixedResolution"),(object)width,height,"Hero UI temporary"},null);
                _sizeGroup.GetType().GetMethod("AddCustomSize",Flags).Invoke(_sizeGroup,new[]{size});selected=count;_temporarySize=count;
            }
            var selector=_view.GetType().GetProperty("selectedSizeIndex",Flags);_previousSize=(int)selector.GetValue(_view);selector.SetValue(_view,selected);
            UnityEditor.EditorApplication.update+=EditorTick;
        }
        void EditorTick(){if(UnityEditor.EditorApplication.isPaused)UnityEditor.EditorApplication.isPaused=false;_view?.Repaint();}
        void RestoreView()
        {
            UnityEditor.EditorApplication.update-=EditorTick;
            if(_view!=null)_view.GetType().GetProperty("selectedSizeIndex",Flags).SetValue(_view,_previousSize);
            if(_temporarySize>=0)_sizeGroup.GetType().GetMethod("RemoveCustomSize",Flags).Invoke(_sizeGroup,new object[]{_temporarySize});
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Initialize()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(),"-weaponUi")<0 || _instance!=null) return;
            var go=new GameObject("HeroUiSmoke");DontDestroyOnLoad(go);_instance=go.AddComponent<HeroUiSmoke>();
        }
        void Check(bool condition,string label) { if(!condition)throw new Exception(label);_checks.Add(label); }
        void OnGUI()
        {
            if(Event.current.rawType==EventType.MouseDown)_mouseSample=Event.current.mousePosition;
        }
        async UniTask Click(float x,float y)
        {
            var target=(Rect)_view.GetType().GetProperty("targetInView",Flags).GetValue(_view);
            var scale=new Vector2(target.width/1280f,target.height/720f);
            if(!_mouseCalibrated)
            {
                // Measure the editor toolbar/viewport offset with a right click on the lobby background.
                // It varies across dock layouts and must not be hard-coded into gameplay clicks.
                var probe=target.position+Vector2.Scale(new Vector2(640,360),scale);_mouseSample=null;
                UnityEditor.EditorApplication.delayCall+=()=>_view.SendEvent(new Event{type=EventType.MouseDown,button=1,mousePosition=probe});
                await UniTask.WaitUntil(()=>_mouseSample.HasValue).Timeout(TimeSpan.FromSeconds(3));
                _mouseOffset=Vector2.Scale(new Vector2(640,360)-_mouseSample.Value,scale);_mouseCalibrated=true;
                UnityEditor.EditorApplication.delayCall+=()=>_view.SendEvent(new Event{type=EventType.MouseUp,button=1,mousePosition=probe});
                await UniTask.Delay(100);
            }
            var position=target.position+Vector2.Scale(new Vector2(x,y),scale)+_mouseOffset;
            UnityEditor.EditorApplication.delayCall+=()=>_view.SendEvent(new Event{type=EventType.MouseDown,button=0,mousePosition=position});
            await UniTask.Delay(100);
            UnityEditor.EditorApplication.delayCall+=()=>_view.SendEvent(new Event{type=EventType.MouseUp,button=0,mousePosition=position});
            await UniTask.Delay(180);
        }
        async UniTask Key(Key key)
        {
            if(Keyboard.current==null)InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(key));await UniTask.Delay(100);
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());await UniTask.Delay(150);
        }
        async UniTask Capture(string name)
        {
            string path=Path.ChangeExtension(_output,null)+"-"+name+".png";
            if(File.Exists(path))File.Delete(path);
            ScreenCapture.CaptureScreenshot(path);
            await UniTask.WaitUntil(()=>File.Exists(path)).Timeout(TimeSpan.FromSeconds(8));
            _checks.Add($"screenshot {name} {Screen.width}x{Screen.height}");
        }
        async UniTaskVoid Start()
        {
            if(_manual)return;
            var error=await Run();
            UnityEditor.EditorApplication.Exit(error==null?0:1);
        }
        public static async UniTask RunInEditorAsync(ushort port)
        {
            var go=new GameObject("Hero UI editor validation");DontDestroyOnLoad(go);
            var probe=go.AddComponent<HeroUiSmoke>();probe._manual=true;probe._editorPort=port;
            try { var error=await probe.Run();if(error!=null)throw new Exception(error); }
            finally { Destroy(go); }
        }
        async UniTask<string> Run()
        {
            _output=_manual?"Logs/HeroMigration/ui-validation.txt":HeroSelectionSmoke.Arg("-weaponOutput","Temp/HeroUi.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
            string error=null;
            try
            {
                await UniTask.WaitUntil(()=>PrototypeApp.Current!=null&&PrototypeApp.Current.Ready).Timeout(TimeSpan.FromSeconds(100));
                var app=PrototypeApp.Current; ConfigureView();
                await UniTask.Delay(500);await Capture("initial");await Click(80,37);Check(app.Overlay==GameplayOverlay.Debug,"main menu DEBUG button opens");
                await Click(175,144);Check(app.Overlay==GameplayOverlay.Debug,"main menu change hero disabled");await Capture("main-debug");
                await Click(286,90);
                await app.Connect(true,"127.0.0.1",_manual?_editorPort:ushort.Parse(HeroSelectionSmoke.Arg("-weaponPort","18413")));
                Check(app.InRoom,"host connected");await UniTask.Delay(700);
                var player=PrototypePlayer.Local;var match=PrototypeMatch.Current;var before=player.Snapshot.Value;before.Health=70;before.Ink=20;player.Snapshot.Value=before;
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Heroes,"H opens warmup selection");
                await Click(305,357);Check(player.Snapshot.Value.HeroId==1,"list click only previews");await Capture("warmup-preview");
                uint life=player.Snapshot.Value.Revision;await Click(1005,624);
                await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==3).Timeout(TimeSpan.FromSeconds(5));
                Check(player.Snapshot.Value.Ink==100,"warmup hero switch refills ink");Check(player.Snapshot.Value.Revision==life,"hero switch keeps lifecycle");Check(app.Overlay==GameplayOverlay.Heroes,"hero switch keeps list open");await Capture("warmup-equipped");
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Game,"H closes warmup selection");
                await Capture("hero-gameplay");
                var state=match.State.Value;state.Phase=MatchPhase.Playing;state.Round=1;state.EndsAt=player.NetworkManager.ServerTime.Time+180;match.State.Value=state;
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay!=GameplayOverlay.Heroes,"H blocked during match");
                await Key(UnityEngine.InputSystem.Key.Escape);await Click(80,37);Check(app.Overlay==GameplayOverlay.Debug,"match DEBUG button opens");await Capture("match-debug");
                await Click(175,144);Check(app.Overlay==GameplayOverlay.Heroes,"DEBUG opens shared hero list");
                await Click(305,520);await Capture("charge-preview");
                before=player.Snapshot.Value;before.Ink=20;before.InkRecoverAt=player.NetworkManager.ServerTime.Time+100;player.Snapshot.Value=before;
                uint shots=before.ShotSequence;await Click(1005,624);
                await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==5).Timeout(TimeSpan.FromSeconds(5));
                Check(player.Snapshot.Value.Ink==20,"DEBUG hero switch preserves ink");Check(player.Snapshot.Value.ShotSequence==shots,"UI clicks do not fire");
                await Key(UnityEngine.InputSystem.Key.Escape);Check(app.Overlay==GameplayOverlay.Debug,"Esc returns from picker to DEBUG");
                await Key(UnityEngine.InputSystem.Key.Escape);Check(app.Overlay==GameplayOverlay.Game,"Esc returns from DEBUG to gameplay");
                await app.Leave();Check(app.Overlay==GameplayOverlay.Game,"leave clears overlays");
            }
            catch(Exception e){error=e.ToString();Debug.LogError("[HERO-UI] "+error);}
            File.WriteAllText(_output,"passed="+(error==null)+"\n"+string.Join("\n",_checks)+"\nerror="+error);
            if(PrototypeApp.Current!=null&&PrototypeApp.Current.InRoom)await PrototypeApp.Current.Leave();
            RestoreView();return error;
        }
        void OnDestroy(){UnityEditor.EditorApplication.update-=EditorTick;if(_instance==this)_instance=null;}
    }
}
#endif
