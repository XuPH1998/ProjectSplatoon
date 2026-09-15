using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

namespace Splatoon.Networking
{
    // 房主监听所有网卡；端口默认 7777。客户端地址由房间码解码或手动输入。
    public readonly struct LanHostOptions { public readonly ushort Port; public readonly bool LoopbackOnly; public LanHostOptions(ushort port = 7777, bool loopbackOnly = false) { Port = port; LoopbackOnly = loopbackOnly; } }
    public readonly struct LanJoinOptions { public readonly string Address; public readonly ushort Port; public LanJoinOptions(string address, ushort port = 7777) { Address = address; Port = port; } }
    public enum NetworkSessionState { [InspectorName("离线")] Offline, [InspectorName("正在启动")] Starting, [InspectorName("房主")] Hosting, [InspectorName("正在加入")] Joining, [InspectorName("已连接")] Connected, [InspectorName("失败")] Failed }
    public readonly struct HostResult { public readonly bool Success; public readonly string Error; public HostResult(bool success, string error = null) { Success = success; Error = error; } }
    public readonly struct JoinResult { public readonly bool Success; public readonly string Error; public JoinResult(bool success, string error = null) { Success = success; Error = error; } }
    public interface ILanSessionService
    {
        NetworkSessionState State { get; }
        string LastError { get; }
        event Action<NetworkSessionState> StateChanged;
        UniTask<HostResult> StartHostAsync(LanHostOptions options, CancellationToken token = default);
        UniTask<JoinResult> JoinAsync(LanJoinOptions options, CancellationToken token = default);
        UniTask ShutdownAsync();
    }
    [Serializable]
    public struct PlayerInputFrame : INetworkSerializable
    {
        // 模拟帧、输入序号和跳跃边沿序号，用于去重与房主校验。
        public uint Tick, Sequence, JumpSequence, FireSequence, Revision, HeroRevision, ReleaseSequence;
        public Vector2 Move, Look;
        public bool Fire, Swim, CancelFire;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Tick); s.SerializeValue(ref Sequence); s.SerializeValue(ref JumpSequence);
            s.SerializeValue(ref FireSequence); s.SerializeValue(ref Revision);
            s.SerializeValue(ref HeroRevision); s.SerializeValue(ref ReleaseSequence); s.SerializeValue(ref CancelFire);
            s.SerializeValue(ref Move); s.SerializeValue(ref Look); s.SerializeValue(ref Fire); s.SerializeValue(ref Swim);
        }
    }
    public enum MatchPhase : byte { [InspectorName("热身")] Practice, [InspectorName("比赛中")] Playing, [InspectorName("已结算")] Finished }
    public struct MatchStateSnapshot : INetworkSerializable
    {
        public uint Tick, Round;
        public int PlayerCount;
        public double PinkArea, BlueArea, TotalArea;
        public double StartsAt, EndsAt;
        public MatchPhase Phase;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Tick); s.SerializeValue(ref Round); s.SerializeValue(ref PlayerCount);
            s.SerializeValue(ref PinkArea); s.SerializeValue(ref BlueArea); s.SerializeValue(ref TotalArea);
            s.SerializeValue(ref StartsAt); s.SerializeValue(ref EndsAt); s.SerializeValue(ref Phase);
        }
    }
    public interface IMatchStateReplicator { void ApplySnapshot(MatchStateSnapshot snapshot); void SubmitInput(PlayerInputFrame input); }
}
