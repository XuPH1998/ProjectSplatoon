using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Combat;
using Splatoon.Networking;

namespace Splatoon.Prototype
{
    public sealed partial class PrototypeMatch
    {
        const int FoamPartBytes=1024;
        readonly SortedList<uint,FoamCommitData> _foamJournal=new(256);
        readonly SortedList<uint,FoamCommitData> _foamBuffered=new(256);
        readonly Dictionary<uint,FoamIncoming> _foamIncoming=new();
        readonly List<uint> _foamObsolete=new(256);
        readonly List<FoamActor> _foamActors=new(8);
        readonly Stack<FoamIncoming> _foamIncomingPool=new();
        uint _foamTick;
        double _foamProgress;
        public uint FoamRevision=>Arena?.Foam?.Revision??0;
        public long FoamBytesSent {get;private set;}
        sealed class FoamIncoming
        {public FoamCommitData Commit;public bool[] Parts;public int Received,PartCount;}
        struct FoamActor {public Vector3 Feet;public float Radius,Headroom;public bool Grounded;}
        void PrepareFoamActors()
        {
            _foamActors.Clear();
            foreach(var player in Players)
            {
                var state=player.Snapshot.Value;if(state.Health<=0)continue;
                var shape=HeroBodyShape.For(state.HeroId);float height=state.CompactBody||state.Swimming?shape.CompactHeight:shape.Height;
                float radius=shape.Radius;
                if(state.ShowsSwimBody&&player.SwimBody?.Profile!=null)radius=Mathf.Max(radius,player.SwimBody.Profile.Size.magnitude*.5f);
                var head=state.Position+Vector3.up*(height-shape.Radius+.035f);
                float room=GameplayConfig.Map.FoamMaxHeight;
                if(Physics.SphereCast(head,Mathf.Max(.05f,shape.Radius-.02f),Vector3.up,out var hit,room,PlayerMotorSimulation.WorldMask,QueryTriggerInteraction.Ignore))room=Mathf.Max(0,hit.distance-.035f);
                _foamActors.Add(new FoamActor{Feet=state.Position,Radius=radius+.125f,Headroom=room,Grounded=state.Grounded});
            }
        }
        float FoamGrowthLimit(Vector3 point,float currentHeight)
        {
            float limit=GameplayConfig.Map.FoamMaxHeight;
            foreach(var actor in _foamActors)
            {
                var delta=new Vector2(point.x-actor.Feet.x,point.z-actor.Feet.z);
                if(delta.sqrMagnitude>actor.Radius*actor.Radius||point.y>actor.Feet.y+.15f)continue;
                float allowed=actor.Feet.y-point.y+(actor.Grounded?actor.Headroom:-.025f);
                limit=Mathf.Min(limit,Mathf.Max(currentHeight,allowed));
            }
            return limit;
        }
        void CommitFoam(bool force=false)
        {
            var foam=Arena.Foam;if(foam==null)return;
            _foamTick++;
            if(!force && _foamTick%(uint)(GameplayConfig.Global.SimulationRate/GameplayConfig.Global.FoamCommitRate)!=0)return;
            PrepareFoamActors();foam.LimitGrowth??=FoamGrowthLimit;
            var borrowed=foam.Commit(false);if(borrowed==null)return;
            var commit=FoamCommitData.Rent(foam.LastDeltaBytes);commit.Revision=foam.Revision;commit.Tick=_foamTick;commit.PaintSequence=PaintSequence;
            Buffer.BlockCopy(borrowed,0,commit.Data,0,commit.Count);var bytes=commit.Data;
            _foamJournal.Add(commit.Revision,commit);
            for(int offset=0;offset<commit.Count;offset+=FoamPartBytes)
            {
                int count=Math.Min(FoamPartBytes,commit.Count-offset);
                FoamPartClientRpc(_paintRound,commit.Revision,commit.Tick,commit.PaintSequence,commit.Count,offset,new NetworkBytes(bytes,offset,count));
                FoamBytesSent+=count+28;
            }
        }
        [ClientRpc]
        void FoamPartClientRpc(uint round,uint revision,uint tick,uint paintSequence,int length,int offset,NetworkBytes bytes)
        {
            try
            {
                if(IsServer||round!=_paintRound||revision<=FoamRevision)return;
                int limit=(Arena.Foam?.NodeCount??0)*9+4;
                if(length<4||length>limit||offset<0||offset%FoamPartBytes!=0||offset>=length||bytes.Count!=Math.Min(FoamPartBytes,length-offset))throw new InvalidDataException("泡沫网络分片无效");
                if(!_foamIncoming.TryGetValue(revision,out var incoming))
                {
                    if(_foamIncoming.Count+_foamBuffered.Count>=256)throw new InvalidDataException("泡沫补同步缓存超限");
                    incoming=_foamIncomingPool.Count>0?_foamIncomingPool.Pop():new FoamIncoming();incoming.Commit=FoamCommitData.Rent(length);
                    incoming.Commit.Revision=revision;incoming.Commit.Tick=tick;incoming.Commit.PaintSequence=paintSequence;incoming.PartCount=(length+FoamPartBytes-1)/FoamPartBytes;incoming.Received=0;
                    if(incoming.Parts==null||incoming.Parts.Length<incoming.PartCount)incoming.Parts=new bool[incoming.PartCount];else Array.Clear(incoming.Parts,0,incoming.PartCount);
                    _foamIncoming.Add(revision,incoming);
                }
                if(incoming.Commit.Count!=length||incoming.Commit.Tick!=tick||incoming.Commit.PaintSequence!=paintSequence)throw new InvalidDataException("泡沫网络分片边界不一致");
                int part=offset/FoamPartBytes;
                if(!incoming.Parts[part]){Buffer.BlockCopy(bytes.Buffer,bytes.Offset,incoming.Commit.Data,offset,bytes.Count);incoming.Parts[part]=true;incoming.Received++;}
                _foamProgress=Time.unscaledTimeAsDouble;
                if(incoming.Received==incoming.PartCount)
                {
                    Arena.Foam.ValidateDelta(incoming.Commit.Data,incoming.Commit.Count);
                    if(_foamBuffered.TryGetValue(revision,out var duplicate))duplicate.Release();
                    _foamBuffered[revision]=incoming.Commit;incoming.Commit=null;_foamIncoming.Remove(revision);_foamIncomingPool.Push(incoming);DrainFoam();
                }
            }
            catch(Exception e){Debug.LogError(e.Message);_syncRetryPending=true;}
            finally{bytes.Dispose();}
        }
        void DrainFoam()
        {
            if(IsServer||!InitialSyncComplete||Arena.Foam==null)return;
            while(_foamBuffered.TryGetValue(FoamRevision+1,out var commit)&&commit.PaintSequence<=_appliedSequence)
            {Arena.Foam.ApplyDelta(commit.Data,commit.Revision,commit.Count);_foamBuffered.Remove(commit.Revision);commit.Release();_foamProgress=Time.unscaledTimeAsDouble;}
        }
        bool FoamSyncStalled()=>InitialSyncComplete&&(_foamBuffered.Count>0||_foamIncoming.Count>0)&&Time.unscaledTimeAsDouble-_foamProgress>2;
        void ResetFoamSync()
        {
            foreach(var c in _foamJournal.Values)c.Release();foreach(var c in _foamBuffered.Values)c.Release();
            foreach(var incoming in _foamIncoming.Values){incoming.Commit.Release();incoming.Commit=null;_foamIncomingPool.Push(incoming);}
            _foamJournal.Clear();_foamBuffered.Clear();_foamIncoming.Clear();_foamTick=0;_foamProgress=Time.unscaledTimeAsDouble;_foamActors.Clear();
        }
        void PruneFoamJournal(uint through)
        {_foamObsolete.Clear();foreach(var pair in _foamJournal){if(pair.Key>through)break;_foamObsolete.Add(pair.Key);}foreach(uint revision in _foamObsolete){_foamJournal[revision].Release();_foamJournal.Remove(revision);}}
        byte[] EncodeTransferCheckpoint(PaintCheckpoint checkpoint)
        {
            checkpoint.FoamJournal.Clear();
            foreach(var pair in _foamJournal)if(pair.Key>checkpoint.FoamRevision)checkpoint.FoamJournal.Add(pair.Value);
            checkpoint.FoamEndRevision=FoamRevision;
            var bytes=PaintSnapshotCodec.Encode(checkpoint);checkpoint.FoamJournal.Clear();return bytes;
        }
        void ValidateFoamCheckpoint(PaintCheckpoint checkpoint)
        {
            if(Arena.Foam==null){if(checkpoint.Foam.Length!=0)throw new InvalidDataException("地图不支持泡沫");return;}
            Arena.Foam.ValidateSnapshot(checkpoint.Foam);uint revision=checkpoint.FoamRevision;
            foreach(var commit in checkpoint.FoamJournal)
            {if(commit.Revision!=++revision)throw new InvalidDataException("泡沫补同步日志不连续");Arena.Foam.ValidateDelta(commit.Data,commit.Count);}
            if(revision!=checkpoint.FoamEndRevision)throw new InvalidDataException("泡沫补同步边界不一致");
        }
        void RestoreFoamCheckpoint(PaintCheckpoint checkpoint)
        {
            if(Arena.Foam==null)return;
            Arena.Foam.Restore(checkpoint.Foam,checkpoint.FoamRevision);
            foreach(var commit in checkpoint.FoamJournal)Arena.Foam.ApplyDelta(commit.Data,commit.Revision,commit.Count);
            _foamObsolete.Clear();foreach(var pair in _foamBuffered)if(pair.Key<=FoamRevision)_foamObsolete.Add(pair.Key);foreach(uint r in _foamObsolete){_foamBuffered[r].Release();_foamBuffered.Remove(r);}
            _foamObsolete.Clear();foreach(var pair in _foamIncoming)if(pair.Key<=FoamRevision)_foamObsolete.Add(pair.Key);foreach(uint r in _foamObsolete){var incoming=_foamIncoming[r];incoming.Commit.Release();incoming.Commit=null;_foamIncomingPool.Push(incoming);_foamIncoming.Remove(r);}
        }
    }
}
