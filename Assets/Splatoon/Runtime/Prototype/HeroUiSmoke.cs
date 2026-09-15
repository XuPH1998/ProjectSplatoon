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
        int _requestedWidth, _requestedHeight;
        public static int RequestedWidth = 1280, RequestedHeight = 720;
        bool _mouseCalibrated;
        Vector2? _mouseSample;
        Vector2 _mouseOffset;
        Vector2 _mouseScale;
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
            if (_manual) { width = _requestedWidth; height = _requestedHeight; }
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
            if(Event.current.rawType==EventType.MouseDown)_mouseSample=new Vector2(Event.current.mousePosition.x * 1280 / Screen.width, Event.current.mousePosition.y * 720 / Screen.height);
        }
        async UniTask Click(float x,float y)
        {
            _view.Focus();
            var target=(Rect)_view.GetType().GetProperty("targetInView",Flags).GetValue(_view);
            var scale=new Vector2(target.width/1280f,target.height/720f);
            if(!_mouseCalibrated)
            {
                // Measure both scale and offset: a zoomed or letterboxed Game View's targetInView
                // dimensions alone do not describe SendEvent's mapping to game pixels.
                var first = target.position + Vector2.Scale(new Vector2(400, 240), scale);
                var second = target.position + Vector2.Scale(new Vector2(800, 480), scale);
                var firstSample = await ProbeMouse(first);
                var secondSample = await ProbeMouse(second);
                var delta = secondSample - firstSample;
                if (Mathf.Abs(delta.x) < 1 || Mathf.Abs(delta.y) < 1) throw new Exception("Game View mouse calibration failed");
                _mouseScale = new Vector2((second.x - first.x) / delta.x, (second.y - first.y) / delta.y);
                _mouseOffset = first - Vector2.Scale(firstSample, _mouseScale);
                _mouseCalibrated = true;
            }
            var position=Vector2.Scale(new Vector2(x,y),_mouseScale)+_mouseOffset;
            UnityEditor.EditorApplication.delayCall+=()=>_view.SendEvent(new Event{type=EventType.MouseDown,button=0,mousePosition=position});
            await UniTask.Delay(100);
            UnityEditor.EditorApplication.delayCall+=()=>_view.SendEvent(new Event{type=EventType.MouseUp,button=0,mousePosition=position});
            await UniTask.Delay(180);
        }
        async UniTask<Vector2> ProbeMouse(Vector2 position)
        {
            _mouseSample = null;
            UnityEditor.EditorApplication.delayCall += () => _view.SendEvent(new Event { type = EventType.MouseDown, button = 1, mousePosition = position });
            await UniTask.WaitUntil(() => _mouseSample.HasValue).Timeout(TimeSpan.FromSeconds(3));
            Vector2 sample = _mouseSample.Value;
            UnityEditor.EditorApplication.delayCall += () => _view.SendEvent(new Event { type = EventType.MouseUp, button = 1, mousePosition = position });
            await UniTask.Delay(100);
            return sample;
        }
        async UniTask Key(Key key)
        {
            _view.Focus(); await UniTask.Delay(100);
            if(Keyboard.current==null)InputSystem.AddDevice<Keyboard>();
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState(key));await UniTask.Delay(100);
            InputSystem.QueueStateEvent(Keyboard.current,new KeyboardState());await UniTask.Delay(150);
        }
        async UniTask ClickHeroPoint(Vector2 point)
        {
            var pixel = HeroSelectionLayout.Matrix(Screen.width, Screen.height).MultiplyPoint3x4(point);
            await Click(pixel.x * 1280 / Screen.width, pixel.y * 720 / Screen.height);
        }
        UniTask ClickHero(int index) => ClickHeroPoint(HeroSelectionLayout.Portrait(index).center);
        UniTask ConfirmHero() => ClickHeroPoint(HeroSelectionLayout.Confirm.center);
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
            probe._requestedWidth = RequestedWidth; probe._requestedHeight = RequestedHeight;
            try { var error=await probe.Run();if(error!=null)throw new Exception(error); }
            finally { Destroy(go); }
        }
        async UniTask<string> Run()
        {
            _output=_manual?$"Reports/HeroSelection/ui-{_requestedWidth}x{_requestedHeight}.txt":HeroSelectionSmoke.Arg("-weaponOutput","Reports/HeroSelection/hero-ui.txt");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_output)));
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
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Heroes,
                    "H opens warmup selection" + (app.Overlay==GameplayOverlay.Heroes ? "" : $" (overlay={app.Overlay}, focused={Application.isFocused}, reason={app.HeroSelectionUnavailableReason(HeroSelectionOrigin.Warmup)})"));
                Check(app.Heroes.PortraitCount == 6, "all six portraits loaded before selection");
                await ConfirmHero(); Check(!player.HeroChangePending, "current hero confirmation disabled");
                uint beforePreviewShots = player.Snapshot.Value.ShotSequence;
                for (int i = 0; i < 6; i++)
                {
                    await ClickHero(i);
                    Check((int)typeof(PrototypeApp).GetField("_previewHeroId", Flags).GetValue(app) == i + 1, "portrait click previews hero " + (i + 1));
                    await Capture("hero-" + (i + 1));
                }
                Check(player.Snapshot.Value.HeroId == 1, "all six portrait clicks only preview");
                Check(player.Snapshot.Value.ShotSequence == beforePreviewShots, "portrait clicks do not fire");
                await ClickHero(2);Check(player.Snapshot.Value.HeroId==1,"list click only previews");await Capture("warmup-preview");
                uint life=player.Snapshot.Value.Revision;await ConfirmHero();
                await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==3).Timeout(TimeSpan.FromSeconds(5));
                Check(player.Snapshot.Value.Ink==100,"warmup hero switch refills ink");Check(player.Snapshot.Value.Revision==life,"hero switch keeps lifecycle");Check(app.Overlay==GameplayOverlay.Heroes,"hero switch keeps list open");await Capture("warmup-equipped");
                before = player.Snapshot.Value; before.Health = 0; before.RespawnsAt = player.NetworkManager.ServerTime.Time + 20; player.Snapshot.Value = before;
                await ClickHero(3); await ConfirmHero(); Check(player.Snapshot.Value.HeroId == 3 && !player.HeroChangePending, "dead hero confirmation disabled");
                await Capture("dead-disabled"); before = player.Snapshot.Value; before.RespawnsAt = player.NetworkManager.ServerTime.Time; player.Snapshot.Value = before;
                await UniTask.WaitUntil(() => player.Snapshot.Value.Health > 0).Timeout(TimeSpan.FromSeconds(5));
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Game,"H closes warmup selection");
                await Capture("hero-gameplay");
                var state=match.State.Value;state.Phase=MatchPhase.Playing;state.Round=1;state.EndsAt=player.NetworkManager.ServerTime.Time+180;match.State.Value=state;
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Heroes,"H opens own spawn selection during match");
                await Capture("spawn-area-selection");
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Game,"H closes spawn selection");
                await Capture("spawn-area-hud");
                await Key(UnityEngine.InputSystem.Key.Escape);await Click(80,37);Check(app.Overlay==GameplayOverlay.Debug,"match DEBUG button opens");await Capture("match-debug");
                await Click(175,144);Check(app.Overlay==GameplayOverlay.Heroes,"DEBUG opens shared hero list");
                await ClickHero(4);await Capture("charge-preview");
                before=player.Snapshot.Value;before.Ink=20;before.InkRecoverAt=player.NetworkManager.ServerTime.Time+100;player.Snapshot.Value=before;
                uint shots=before.ShotSequence;await ConfirmHero();
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
