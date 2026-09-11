using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Splatoon.Networking
{
    public readonly struct LanHostOptions { public readonly ushort Port; public LanHostOptions(ushort port = 7777) => Port = port; }
    public readonly struct LanJoinOptions { public readonly string Address; public readonly ushort Port; public LanJoinOptions(string address, ushort port = 7777) { Address = address; Port = port; } }
    public enum NetworkSessionState { Offline, Starting, Hosting, Joining, Connected, Failed }
    public readonly struct HostResult { public readonly bool Success; public readonly string Error; public HostResult(bool success, string error = null) { Success = success; Error = error; } }
    public readonly struct JoinResult { public readonly bool Success; public readonly string Error; public JoinResult(bool success, string error = null) { Success = success; Error = error; } }

    public interface ILanSessionService
    {
        NetworkSessionState State { get; }
        UniTask<HostResult> StartHostAsync(LanHostOptions options);
        UniTask<JoinResult> JoinAsync(LanJoinOptions options);
        UniTask ShutdownAsync();
    }

    [Serializable] public struct PlayerInputFrame { public uint Tick; public Vector2 Move; public Vector2 Look; public bool Fire; }
    [Serializable] public struct MatchStateSnapshot { public uint Tick; public int PlayerCount; }
    public interface IMatchStateReplicator { void ApplySnapshot(MatchStateSnapshot snapshot); void SubmitInput(PlayerInputFrame input); }
}
