#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
            var down=new UniTaskCompletionSource();
            UnityEditor.EditorApplication.delayCall+=()=>{_view.SendEvent(new Event{type=EventType.MouseDown,button=0,mousePosition=position});down.TrySetResult();};
            await down.Task.Timeout(TimeSpan.FromSeconds(4));
            await UniTask.Delay(100);
            var up=new UniTaskCompletionSource();
            UnityEditor.EditorApplication.delayCall+=()=>{_view.SendEvent(new Event{type=EventType.MouseUp,button=0,mousePosition=position});up.TrySetResult();};
            await up.Task.Timeout(TimeSpan.FromSeconds(4));
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
        async UniTask ClickHero(int index)
        {
            var viewport=HeroSelectionLayout.CardViewport;var point=HeroSelectionLayout.Portrait(index).center;
            float scroll=Mathf.Clamp(point.y-viewport.center.y,0,HeroSelectionLayout.ContentHeight(PrototypeApp.Current.Heroes.PortraitCount)-viewport.height);
            typeof(PrototypeApp).GetField("_heroCardScroll",Flags).SetValue(PrototypeApp.Current,new Vector2(0,scroll));
            await UniTask.Yield();await ClickHeroPoint(point-Vector2.up*scroll);
        }
        UniTask ConfirmHero() => ClickHeroPoint(HeroSelectionLayout.Confirm.center);
        async UniTask VerifyEquipment(PrototypeApp app, PrototypePlayer player)
        {
            var before=player.Snapshot.Value; var original=Splatoon.Combat.PlayerLoadout.From(before);
            var tables=Splatoon.Config.LubanConfigService.Current.Tables;
            await ClickHero(0);
            for(int tab=0;tab<2;tab++)
            {
                await ClickHeroPoint(HeroSelectionLayout.Tab(tab).center);
                int count=tab==0?tables.TbSubWeapon.DataList.Count:tables.TbSpecialWeapon.DataList.Count;
                for(int i=0;i<count;i++)
                {
                    await ClickHeroPoint(HeroSelectionLayout.EquipmentCard(i,tab==0?4:3).center);
                    int id=tab==0?tables.TbSubWeapon.DataList[i].Id:tables.TbSpecialWeapon.DataList[i].Id;
                    Check((int)typeof(PrototypeApp).GetField(tab==0?"_previewSubId":"_previewSpecialId",Flags).GetValue(app)==id,$"equipment tab {tab} option {id} previews");
                    Check(Splatoon.Combat.PlayerLoadout.From(player.Snapshot.Value).Matches(original),"equipment preview leaves live loadout unchanged");
                }
                await Capture(tab==0?"all-sub-weapons":"all-special-weapons");
            }
            int savedSub=(int)typeof(PrototypeApp).GetField("_previewSubId",Flags).GetValue(app);
            int savedSpecial=(int)typeof(PrototypeApp).GetField("_previewSpecialId",Flags).GetValue(app);
            await ClickHeroPoint(HeroSelectionLayout.Tab(0).center);
            Check((int)typeof(PrototypeApp).GetField("_previewSubId",Flags).GetValue(app)==savedSub,"switching tabs preserves sub selection");
            Check((int)typeof(PrototypeApp).GetField("_previewSpecialId",Flags).GetValue(app)==savedSpecial,"switching tabs preserves special selection");
            Check(player.Snapshot.Value.ShotSequence==before.ShotSequence,"equipment clicks do not fire");
            await Key(UnityEngine.InputSystem.Key.Escape);
            Check(Splatoon.Combat.PlayerLoadout.From(player.Snapshot.Value).Matches(original),"closing cancels unconfirmed equipment");
            await Key(UnityEngine.InputSystem.Key.H);
            Check((int)typeof(PrototypeApp).GetField("_previewSubId",Flags).GetValue(app)==original.SubWeaponId,"reopen restores equipped sub");
            await ClickHeroPoint(HeroSelectionLayout.Tab(0).center);
            await ClickHeroPoint(HeroSelectionLayout.EquipmentCard(1,4).center);
            await ClickHeroPoint(HeroSelectionLayout.Tab(1).center);
            await ClickHeroPoint(HeroSelectionLayout.EquipmentCard(1,3).center);
            await ConfirmHero();
            await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.SubWeaponId==tables.TbSubWeapon.DataList[1].Id&&player.Snapshot.Value.SpecialWeaponId==tables.TbSpecialWeapon.DataList[1].Id).Timeout(TimeSpan.FromSeconds(5));
            Check(player.Snapshot.Value.HeroId==before.HeroId,"confirming equipment keeps hero");
            await Capture("equipment-confirmed");
        }
        async UniTask CaptureCombatStates(PrototypePlayer player, PrototypeMatch match)
        {
            var original=PlayerLoadout.From(player.Snapshot.Value);
            var state=player.Snapshot.Value;
            state.Ink=0;state.InkRecoverAt=player.NetworkManager.ServerTime.Time+60;
            state.SpecialPoints=original.RequiredPoints;state.SpecialFailure=SpecialFailure.NoSpace;
            player.Snapshot.Value=state;
            await UniTask.Delay(180);
            Check(player.PresentedState.Ink<1,"low ink fixture reaches presented HUD");
            await Capture("combat-low-ink-special-failure");
            await Key(UnityEngine.InputSystem.Key.Q);
            Check(SpecialWeaponSimulation.Active(player.PresentedState),"Q activates charged special through gameplay input");
            await Capture("combat-special-active");
            player.Respawn();
            player.RequestLoadoutChange(new PlayerLoadout(6,1,1),HeroSelectionOrigin.Warmup);
            await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==6).Timeout(TimeSpan.FromSeconds(5));
            InputSystem.QueueStateEvent(Mouse.current,new MouseState { position=Mouse.current.position.ReadValue(),buttons=1 });
            await UniTask.Delay(350);
            Check(SplatlingSimulation.Charging(player.PresentedState),"held fire displays Splatling charge");
            await Capture("combat-splatling-charge");
            InputSystem.QueueStateEvent(Mouse.current,new MouseState { position=Mouse.current.position.ReadValue() });
            await UniTask.WaitUntil(()=>player.PresentedState.SplatlingRemaining>0).Timeout(TimeSpan.FromSeconds(3));
            Check(player.PresentedState.SplatlingRemaining>0,"release displays Splatling remaining shots");
            await Capture("combat-splatling-firing");
            player.Respawn();
            player.RequestLoadoutChange(original,HeroSelectionOrigin.Warmup);
            await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==original.HeroId).Timeout(TimeSpan.FromSeconds(5));
            var prefab=UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(PrototypeApp.PlayerAddress);
            await prefab.ToUniTask();
            try
            {
                match.AddTestBot(prefab.Result);
                var bot=PrototypePlayer.ByOwner.Values.First(p=>p.IsTestBot);
                foreach(bool friendly in new[]{true,false})
                {
                    state=bot.Snapshot.Value;state.Team=friendly?player.Snapshot.Value.Team:(byte)(3-player.Snapshot.Value.Team);bot.Snapshot.Value=state;
                    bot.DiagnosticPlace(player.Snapshot.Value.Position+Vector3.right*1.5f,0);
                    bot.ReceiveDamage((byte)(3-bot.Snapshot.Value.Team),10000,Vector3.forward);
                    await UniTask.WaitUntil(()=>player.BubbleInteractionTarget==bot).Timeout(TimeSpan.FromSeconds(5));
                    await Capture(friendly?"combat-rescue-prompt":"combat-execute-prompt");
                    await Key(UnityEngine.InputSystem.Key.F);
                    Check(friendly?bot.Snapshot.Value.IsAlive:bot.Snapshot.Value.IsDead,friendly?"F rescues friendly bubble":"F executes enemy bubble");
                }
            }
            finally { match.ClearTestBots();UnityEngine.AddressableAssets.Addressables.Release(prefab); }
            state=player.Snapshot.Value;state.ProtectedUntil=0;player.Snapshot.Value=state;
            player.ReceiveDamage((byte)(3-state.Team),10000,Vector3.forward);
            await UniTask.Delay(180);Check(player.PresentedState.IsBubble,"downed state reaches HUD");
            await Capture("combat-downed");
            state=player.Snapshot.Value;state.BubbleUntil=player.NetworkManager.ServerTime.Time-.1;player.Snapshot.Value=state;
            await UniTask.WaitUntil(()=>player.PresentedState.IsDead).Timeout(TimeSpan.FromSeconds(5));
            await Capture("combat-respawn-countdown");
            await UniTask.WaitUntil(()=>player.PresentedState.IsAlive).Timeout(TimeSpan.FromSeconds(15));
            await Capture("combat-respawned");
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
                int heroCount = Splatoon.Config.LubanConfigService.Current.Tables.TbHero.DataList.Count;
                Check(app.Heroes.PortraitCount == heroCount, "all portraits loaded before selection");
                await ConfirmHero(); Check(!player.HeroChangePending, "current hero confirmation disabled");
                uint beforePreviewShots = player.Snapshot.Value.ShotSequence;
                for (int i = 0; i < heroCount; i++)
                {
                    await ClickHero(i);
                    Check((int)typeof(PrototypeApp).GetField("_previewHeroId", Flags).GetValue(app) == i + 1, "portrait click previews hero " + (i + 1));
                    await Capture("hero-" + (i + 1));
                }
                Check(player.Snapshot.Value.HeroId == 1, "all portrait clicks only preview");
                Check(player.Snapshot.Value.ShotSequence == beforePreviewShots, "portrait clicks do not fire");
                await VerifyEquipment(app,player);
                await ClickHero(6); await ConfirmHero();
                await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==7).Timeout(TimeSpan.FromSeconds(5));
                Check(player.Snapshot.Value.HeroId == 7, "seventh card confirms BubbleGirl"); await Capture("bubble-equipped");
                await ClickHero(2);Check(player.Snapshot.Value.HeroId==7,"list click only previews");await Capture("warmup-preview");
                uint life=player.Snapshot.Value.Revision;await ConfirmHero();
                await UniTask.WaitUntil(()=>!player.HeroChangePending&&player.Snapshot.Value.HeroId==3).Timeout(TimeSpan.FromSeconds(5));
                Check(player.Snapshot.Value.Ink==100,"warmup hero switch refills ink");Check(player.Snapshot.Value.Revision==life,"hero switch keeps lifecycle");Check(app.Overlay==GameplayOverlay.Heroes,"hero switch keeps list open");await Capture("warmup-equipped");
                before = player.Snapshot.Value; before.Health = 0; before.RespawnsAt = player.NetworkManager.ServerTime.Time + 20; player.Snapshot.Value = before;
                await ClickHero(3); await ConfirmHero(); Check(player.Snapshot.Value.HeroId == 3 && !player.HeroChangePending, "dead hero confirmation disabled");
                await Capture("dead-disabled"); before = player.Snapshot.Value; before.RespawnsAt = player.NetworkManager.ServerTime.Time; player.Snapshot.Value = before;
                await UniTask.WaitUntil(() => player.Snapshot.Value.Health > 0).Timeout(TimeSpan.FromSeconds(5));
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Game,"H closes warmup selection");
                await Capture("hero-gameplay");
                await CaptureCombatStates(player,match);
                var state=match.State.Value;state.Phase=MatchPhase.Playing;state.Round=1;state.EndsAt=player.NetworkManager.ServerTime.Time+180;match.State.Value=state;
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Heroes,"H opens own spawn selection during match");
                await Capture("spawn-area-selection");
                await Key(UnityEngine.InputSystem.Key.H);Check(app.Overlay==GameplayOverlay.Game,"H closes spawn selection");
                await Capture("spawn-area-hud");
                await Key(UnityEngine.InputSystem.Key.Escape);
                var edge=CombatUiLayout.Bounds(Screen.width,Screen.height);
                await ClickHeroPoint(new Vector2(edge.x+80,edge.y+37));Check(app.Overlay==GameplayOverlay.Debug,"match DEBUG button opens");await Capture("match-debug");
                await ClickHeroPoint(new Vector2(175,144));Check(app.Overlay==GameplayOverlay.Heroes,"DEBUG opens shared hero list");
                await ClickHero(4);await Capture("rapid-blaster-preview");
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
