using System;
using Cysharp.Threading.Tasks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using UnityEngine;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeApp
    {
        private readonly UdpLanDiscoveryService _discovery = new();
        private Guid _discoveryRoomId;
        private bool _destroying;
        private Vector2 _roomScroll;
        private GUIStyle _roomText, _lobbyField;
        public ILanDiscoveryService Discovery => _discovery;
        private string ConfigDigest => LanDiscoveryProtocol.Digest(LubanConfigService.Current.ContentSignature);
        private LanRoomAdvertisement DiscoverySnapshot()
        {
            if (!InRoom || Manager == null || !Manager.IsHost || Session.State != NetworkSessionState.Hosting || PrototypeMatch.Current == null) return null;
            return new LanRoomAdvertisement
            {
                RoomId = _discoveryRoomId, GamePort = _activePort,
                ModeId = GameplayConfig.Mode.Id, ModeName = GameplayConfig.Mode.Name,
                MapId = GameplayConfig.Map.Id, MapName = GameplayConfig.Map.Name,
                PlayerCount = Manager.ConnectedClientsIds.Count, MaxPlayers = GameplayConfig.Mode.MaxPlayers,
                Phase = PrototypeMatch.Current.State.Value.Phase, ConfigDigest = ConfigDigest,
                PlayerProtocol = (int)PlayerSnapshot.ProtocolVersion, PaintProtocol = GameplayContentSignature.PaintProtocolVersion
            };
        }
        private bool Compatible(LanRoomAdvertisement a) => a.IsCompatible((int)PlayerSnapshot.ProtocolVersion, GameplayContentSignature.PaintProtocolVersion, ConfigDigest) &&
            a.ModeId == GameplayConfig.Mode.Id && a.MapId == GameplayConfig.Map.Id;
        public async UniTask JoinDiscoveredRoom(Guid roomId)
        {
            if (!Ready || Busy || InRoom) return;
            LanRoomInfo selected = null;
            foreach (var room in _discovery.Rooms) if (room.Advertisement.RoomId == roomId) { selected = room; break; }
            if (selected?.BestEndpoint == null || _discovery.Now - selected.BestEndpoint.LastSeen >= LanDiscoveryProtocol.ExpirySeconds)
            { Error = "房间已离线，请刷新列表。"; return; }
            if (!Compatible(selected.Advertisement)) { Error = "房间的协议、配置或地图不兼容，请使用相同版本。"; return; }
            if (selected.Advertisement.PlayerCount >= selected.Advertisement.MaxPlayers) { Error = "房间已满，请选择其他房间。"; return; }
            var endpoint = selected.BestEndpoint;
            await Connect(false, endpoint.Address, endpoint.Port);
        }
        private void DrawLobby()
        {
            _roomText ??= new GUIStyle(_small) { fontSize = 15, richText = false, clipping = TextClipping.Clip, wordWrap = false };
            _lobbyField ??= new GUIStyle(_field) { fontSize = 18, richText = false, padding = new RectOffset(8,8,6,6) };
            Panel(new Rect(0,0,1280,720),new Color(.055f,.075f,.10f));
            Panel(new Rect(147,34,6,52),PrototypeArena.Pink);
            GUI.Label(new Rect(170,30,675,66),"喷墨对战 / 局域网",_title);
            DrawUsernameField();
            Panel(new Rect(30,151,815,431),new Color(.085f,.115f,.15f));
            GUI.Label(new Rect(48,160,280,32),$"局域网房间  ·  {_discovery.Rooms.Count}",_label);
            GUI.enabled = Ready && !Busy;
            if (GUI.Button(new Rect(714,160,110,33),"刷新列表",_small)) _discovery.StartBrowsing();
            GUI.enabled = true;
            string[] labels = {"房间", "模式", "地图", "人数", "探测延迟", "状态"};
            float[] columns = {12,112,252,365,425,515};
            float[] widths = {94,134,107,54,84,123};
            for (int i=0;i<labels.Length;i++) GUI.Label(new Rect(42+columns[i],208,widths[i],27),labels[i],_roomText);
            _roomScroll = GUI.BeginScrollView(new Rect(42,240,790,324),_roomScroll,new Rect(0,0,767,Mathf.Max(324,_discovery.Rooms.Count*58)));
            Guid join = Guid.Empty;
            for (int index=0;index<_discovery.Rooms.Count;index++)
            {
                var row = _discovery.Rooms[index]; var a = row.Advertisement;
                bool compatible = Ready && Compatible(a), full = a.PlayerCount >= a.MaxPlayers;
                double? ping = row.ProbeMilliseconds(_discovery.Now);
                string phase = a.Phase switch {MatchPhase.Practice=>"热身",MatchPhase.Playing=>"比赛中",_=>"已结算"};
                string status = !compatible ? "内容不兼容" : full ? phase+" · 满员" : phase;
                float y=index*58;
                Panel(new Rect(0,y,767,54),index%2==0?new Color(.12f,.16f,.20f):new Color(.10f,.135f,.18f));
                string[] values = {a.RoomId.ToString("N").Substring(0,8),a.ModeName,a.MapName,$"{a.PlayerCount}/{a.MaxPlayers}",ping.HasValue?$"{ping.Value:0} ms":"—",status};
                for(int col=0;col<values.Length;col++) GUI.Label(new Rect(columns[col],y+15,widths[col],26),new GUIContent(values[col],values[col]),_roomText);
                GUI.enabled = Ready && !Busy && compatible && !full;
                if(GUI.Button(new Rect(646,y+9,105,36),"加入",_button)) join=a.RoomId;
                GUI.enabled = true;
            }
            if(_discovery.Rooms.Count==0) GUI.Label(new Rect(30,90,710,80),Busy?"正在进入房间…":"暂未发现房间。可先创建房间，或使用房间码 / IP 加入。",_small);
            GUI.EndScrollView();
            if(join!=Guid.Empty) JoinDiscoveredRoom(join).Forget();
            GUI.Label(new Rect(42,591,790,57),string.IsNullOrEmpty(_discovery.LastError)?"每 2 秒自动刷新 · 探测延迟为加入前的往返时间\n仅显示广播可达的局域网房间。":_discovery.LastError,_small);
            Panel(new Rect(868,34,382,548),new Color(.10f,.135f,.18f));
            GUI.Label(new Rect(890,51,330,34),"创建房间",_label);
            GUI.Label(new Rect(890,91,340,26),"房主地址（点击切换） / 游戏端口",_small);
            GUI.enabled=Ready&&!Busy;
            if(GUI.Button(new Rect(890,122,230,42),_localAddresses[_hostAddressIndex],_small)) CycleHostAddress();
            _port=GUI.TextField(new Rect(1130,122,96,42),_port,5,_lobbyField);
            if(GUI.Button(new Rect(890,175,336,43),"创建房间",_button)) ConnectFromUI(true);
            GUI.Label(new Rect(890,240,330,30),"房间码加入",_label);
            _roomCodeInput=GUI.TextField(new Rect(890,280,254,42),_roomCodeInput,80,_lobbyField);
            if(GUI.Button(new Rect(1154,280,72,42),"粘贴",_small)) _roomCodeInput=GUIUtility.systemCopyBuffer;
            if(GUI.Button(new Rect(890,334,336,42),"使用房间码加入",_button)) ConnectRoomCode(_roomCodeInput).Forget();
            if(GUI.Button(new Rect(890,394,336,32),_advanced?"收起 IP 连接":"高级：直接 IP 连接",_small)) _advanced=!_advanced;
            if(_advanced)
            {
                _ip=GUI.TextField(new Rect(890,439,230,42),_ip,45,_lobbyField);
                if(GUI.Button(new Rect(1130,439,96,42),"加入",_button)) ConnectFromUI(false);
                GUI.Label(new Rect(890,489,340,44),"使用上方端口 · 同机可填 127.0.0.1",_small);
            }
#if UNITY_EDITOR
            GUI.enabled = Ready && !Busy;
            if (GUI.Button(new Rect(890, 536, 336, 36), "单机武器调试", _button)) StartWeaponDebugRoom().Forget();
#endif
            GUI.enabled=true;
            GUI.Label(new Rect(888,593,Busy?224:340,36),Status,_small);
            if(Busy)
            {
                Panel(new Rect(890,633,336*Mathf.Max(.04f,_progress),4),PrototypeArena.Blue);
                if(Ready&&GUI.Button(new Rect(1118,592,110,33),"取消连接",_small)) CancelConnection();
            }
            if(!Ready&&!Busy&&GUI.Button(new Rect(1118,592,110,33),"重试启动",_small)) Initialize().Forget();
            if(!string.IsNullOrEmpty(Error)) GUI.Label(new Rect(40,650,1190,62),Error,_small);
            else GUI.Label(new Rect(42,653,1180,54),"WASD 移动 · 鼠标瞄准 / 射击 · 空格跳跃 · Shift 潜墨 · Esc 菜单 · 房主回车开始",_small);
        }
    }
}
