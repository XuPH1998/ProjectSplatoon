#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Splatoon.Painting;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class FoamTerrainTests
    {
        readonly List<UnityEngine.Object> _objects=new();
        FoamTerrainWorld _world;
        [SetUp]public void Setup(){HeroMigrationTests.Load();InkShapeAtlas.Configure(AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));}
        [TearDown]public void Cleanup()
        {
            _world?.Dispose();_world=null;
            for(int i=_objects.Count-1;i>=0;i--)if(_objects[i]!=null)UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();LubanConfigService.Current.Reset();
        }
        T Keep<T>(T value)where T:UnityEngine.Object{_objects.Add(value);return value;}
        [Test]public void EnemyRetainsOwnershipUntilDissolved()
        {
            float h=.3f;byte owner=2;FoamRules.Deposit(ref h,ref owner,1,.1f,1.5f,3);
            Assert.That(h,Is.EqualTo(.15f).Within(.00001));Assert.That(owner,Is.EqualTo(2));
            FoamRules.Deposit(ref h,ref owner,1,.2f,1.5f,3);
            Assert.That(h,Is.EqualTo(.1f).Within(.00001));Assert.That(owner,Is.EqualTo(1));
        }
        [Test]public void RoundedDepositConservesVolumeAndSleepsWithoutNewInput()
        {
            var setup=World();var stamp=Stamp(setup.floor,1);
            for(int i=0;i<12;i++){stamp.ShapeSeed=InkShapeAtlas.Pack(0,(uint)(123+i));_world.Queue(setup.floor,stamp,.02f);_world.Commit(false);}
            Assert.That(_world.PendingVolume,Is.EqualTo(.24).Within(.00024));
            Assert.That(Math.Abs(_world.CommittedVolume-_world.PendingVolume),Is.LessThanOrEqualTo(_world.QuantizationVolumeBound+.000001));
            uint rounds=_world.RoundingPassCount;
            for(int i=0;i<200;i++)_world.Commit(false);
            Assert.That(_world.RoundingPassCount,Is.EqualTo(rounds));
            uint revision=_world.Revision;for(int i=0;i<10;i++)_world.Commit(false);
            Assert.That(_world.Revision,Is.EqualTo(revision));
        }
        [Test]public void RoundedShapeIsSeededAndRepeatable()
        {
            var setup=World();var stamp=Stamp(setup.floor,1);
            for(int i=0;i<5;i++){_world.Queue(setup.floor,stamp,.03f);_world.Commit(false);}
            var expected=_world.Capture();_world.Clear();
            for(int i=0;i<5;i++){_world.Queue(setup.floor,stamp,.03f);_world.Commit(false);}
            Assert.That(_world.Capture(),Is.EqualTo(expected));
            _world.Clear();stamp.ShapeSeed=InkShapeAtlas.Pack(0,8765);
            for(int i=0;i<5;i++){_world.Queue(setup.floor,stamp,.03f);_world.Commit(false);}
            Assert.That(_world.Capture(),Is.Not.EqualTo(expected));
        }
        [Test]public void InstalledChangesNotifyOnlyLiveAndSignificantUpdates()
        {
            World();int notifications=0;uint observedRevision=0;
            _world.SurfaceChanged+=change=>{notifications++;observedRevision=_world.Revision;};
            byte[] Delta(ushort height)
            {
                using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
                writer.Write(1);writer.Write(8*17+8);writer.Write((ushort)1);writer.Write(height);writer.Write((byte)(height==0?0:1));return stream.ToArray();
            }
            _world.ApplyDelta(Delta(9),1);Assert.That(notifications,Is.Zero);
            _world.ApplyDelta(Delta(25),2);Assert.That(notifications,Is.EqualTo(1));Assert.That(observedRevision,Is.EqualTo(2));
            _world.ApplyDelta(Delta(200),3,present:false);Assert.That(notifications,Is.EqualTo(1));
            var snapshot=_world.Capture();_world.Clear();_world.Restore(snapshot,3);Assert.That(notifications,Is.EqualTo(1));
            _world.ApplyDelta(Delta(0),4);Assert.That(notifications,Is.EqualTo(2));
        }
        [Test]public void HeightOnlyChangesDoNotUploadOwnershipAndMetricsObserveEveryCommit()
        {
            World();var bytes=_world.Capture();for(int i=0;i<289;i++){bytes[i*3]=100;bytes[i*3+2]=1;}_world.Restore(bytes,1);
            int observed=0;_world.Updated+=m=>observed++;
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(1);writer.Write(8*17+8);writer.Write((ushort)1);writer.Write((ushort)130);writer.Write((byte)1);
            _world.ApplyDelta(stream.ToArray(),2);
            Assert.That(_world.LastMetrics.OwnerUploads,Is.Zero);
            Assert.That(_world.LastMetrics.ColliderRebuilds,Is.EqualTo(1));
            for(int i=0;i<5;i++)_world.Commit(false);
            Assert.That(observed,Is.EqualTo(6));
        }
        [Test]public void NeighbourNormalRefreshDoesNotRecookUnchangedChunk()
        {
            var go=Keep(new GameObject("FoamChunkBoundaryTest"));var arena=go.AddComponent<PrototypeArena>();
            var floor=Surface(go.transform,1,0);floor.WalkableSize=new Vector2(8,4);floor.InitializeOwnership(.125f);arena.RegisterSurfaces();
            var data=Bake(floor);var bake=data.Patches[0];bake.Columns=33;bake.Rows=17;bake.Size=new Vector2(8,4);
            bake.CeilingMm=new ushort[33*17];bake.Edges=new byte[33*17];
            for(int i=0;i<bake.Edges.Length;i++){bake.CeilingMm[i]=3000;if(i%33<32)bake.Edges[i]|=1;if(i/33<16)bake.Edges[i]|=2;}
            _world=new FoamTerrainWorld(arena,data,Keep(new Material(Shader.Find("Splatoon/FoamTerrain"))),1.5f,35);
            var bytes=_world.Capture();for(int i=0;i<bytes.Length;i+=3){bytes[i]=100;bytes[i+2]=1;}_world.Restore(bytes,1);
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(1);writer.Write(8*33+15);writer.Write((ushort)1);writer.Write((ushort)140);writer.Write((byte)1);
            _world.ApplyDelta(stream.ToArray(),2);
            Assert.That(_world.LastMetrics.ColliderRebuilds,Is.EqualTo(1));
            Assert.That(_world.LastMetrics.NormalOnlyUpdates,Is.EqualTo(1));
            Assert.That(_world.LastMetrics.OwnerUploads,Is.Zero);
        }
        [Test]public void LocalRoundingDoesNotRedistributeIntoEnemyFoam()
        {
            var setup=World();var bytes=_world.Capture();
            for(int z=0;z<17;z++)for(int x=0;x<17;x++){int i=(z*17+x)*3;bytes[i]=100;bytes[i+2]=(byte)(x<8?1:2);}
            _world.Restore(bytes,0);
            var stamp=Stamp(setup.floor,1);stamp.Position.x-=.5f;stamp.Position.y=.1f;stamp.Radius=.24f;
            _world.Queue(setup.floor,stamp,.005f);_world.Commit(false);
            var result=_world.Capture();
            for(int z=0;z<17;z++)for(int x=8;x<17;x++){int i=(z*17+x)*3;Assert.That(result[i],Is.EqualTo(bytes[i]));Assert.That(result[i+1],Is.EqualTo(bytes[i+1]));Assert.That(result[i+2],Is.EqualTo(2));}
        }
        [Test]public void ExactDissolutionLeavesNeutralEmptyCell()
        {float h=.3f;byte owner=2;FoamRules.Deposit(ref h,ref owner,1,.2f,1.5f,3);Assert.That(h,Is.EqualTo(0).Within(.000001));Assert.That(owner,Is.Zero);}
        [Test]public void FriendlyGrowthHonorsCeiling()
        {float h=.3f;byte owner=1;FoamRules.Deposit(ref h,ref owner,1,2,1.5f,.4f);Assert.That(h,Is.EqualTo(.4f));Assert.That(owner,Is.EqualTo(1));}
        [TestCase(1)][TestCase(2)][TestCase(3)][TestCase(4)][TestCase(5)][TestCase(6)][TestCase(7)][TestCase(8)]
        public void EveryWeaponBudgetIsFiniteAndDuplicateSafe(int hero)
        {
            var w=GameplayConfig.GetWeapon(hero);var budget=new FoamPaintBudget(new InkShot{Configuration=w,Charge=1});
            float first=budget.Take(1,true);Assert.That(first,Is.GreaterThan(0));Assert.That(budget.Take(1,true),Is.Zero);
            for(uint i=2;i<4096;i++){budget.Take(i,true);budget.Take(i,false);}
            Assert.That(budget.Spent,Is.LessThanOrEqualTo(budget.Total+.00001f));
            Assert.That(budget.Spent,Is.EqualTo(budget.Total).Within(.00001f));
        }
        PaintSurface Surface(Transform parent,int id,float y)
        {
            var go=Keep(GameObject.CreatePrimitive(PrimitiveType.Quad));go.transform.SetParent(parent);go.transform.position=new Vector3(6000,y,6000);go.transform.rotation=Quaternion.Euler(90,0,0);go.transform.localScale=Vector3.one;
            var surface=go.AddComponent<PaintSurface>();surface.SurfaceId=id;surface.Scores=true;surface.WalkableSize=new Vector2(4,4);
            // The source plane's local X/Z are configured independently of the primitive's display mesh.
            go.transform.rotation=Quaternion.identity;surface.InitializeOwnership(.125f);
            surface.ShapeAtlas=InkShapeAtlas.Texture;
            return surface;
        }
        FoamTerrainData Bake(params PaintSurface[] surfaces)
        {
            var data=Keep(ScriptableObject.CreateInstance<FoamTerrainData>());data.CellSize=.25f;data.ChunkSize=4;data.MaxHeight=3;data.CeilingGap=.1f;
            var patches=new List<FoamPatchBake>();foreach(var s in surfaces)foreach(var r in s.GameplayRegions)
            {
                var b=new FoamPatchBake{RegionKey=r.Key(s),Size=r.Size,Columns=17,Rows=17,CeilingMm=new ushort[289],Edges=new byte[289]};
                for(int i=0;i<289;i++){b.CeilingMm[i]=3000;if(i%17<16)b.Edges[i]|=1;if(i/17<16)b.Edges[i]|=2;}patches.Add(b);
            }
            data.Patches=patches.ToArray();return data;
        }
        (PrototypeArena arena,PaintSurface floor,PaintSurface bridge) World()
        {
            var go=Keep(new GameObject("FoamTestArena"));var arena=go.AddComponent<PrototypeArena>();
            var floor=Surface(go.transform,1,0);var bridge=Surface(go.transform,2,3);arena.RegisterSurfaces();
            var shader=Shader.Find("Splatoon/FoamTerrain");var material=shader!=null?Keep(new Material(shader)):null;
            _world=new FoamTerrainWorld(arena,Bake(floor,bridge),material,1.5f,35);
            return(arena,floor,bridge);
        }
        [Test]public void LayeredSnapshotRestoresIndependentSolidTerrain()
        {
            var setup=World();var bytes=_world.Capture();
            // First patch center, the second patch remains empty at the identical X/Z.
            int center=8*17+8;bytes[center*3]=0xe8;bytes[center*3+1]=3;bytes[center*3+2]=1;
            _world.Restore(bytes,5);
            Assert.That(_world.Patches[256].Sample(new Vector3(6000,0,6000),out var top,out _,out var owner),Is.True);
            Assert.That(top.y,Is.EqualTo(1).Within(.0001));Assert.That(owner,Is.EqualTo(1));
            _world.Patches[512].Sample(new Vector3(6000,3,6000),out var upper,out _,out byte upperOwner);
            Assert.That(upper.y,Is.EqualTo(3));Assert.That(upperOwner,Is.Zero);
            var ray=new Ray(new Vector3(6000,2,6000),Vector3.down);
            Assert.That(_world.Patches[256].Chunks[0].Collider.Raycast(ray,out var hit,3),Is.True);
            Assert.That(hit.point.y,Is.EqualTo(top.y).Within(.001));
            Assert.That(_world.Patches[256].Region.Grid.At(Vector3.zero),Is.EqualTo(1));
            uint hash=_world.StateHash();_world.Clear();Assert.That(_world.Revision,Is.Zero);Assert.That(_world.StateHash(),Is.Not.EqualTo(hash));
            _world.Restore(bytes,5);Assert.That(_world.StateHash(),Is.EqualTo(hash));
        }
        [Test]public void MalformedDeltaCannotPartiallyMutateTerrain()
        {
            World();uint before=_world.StateHash();
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(2);writer.Write(0);writer.Write((ushort)2);writer.Write((ushort)100);writer.Write((byte)1);writer.Write((ushort)4000);writer.Write((byte)2);
            Assert.Throws<InvalidDataException>(()=>_world.ApplyDelta(stream.ToArray(),1));Assert.That(_world.StateHash(),Is.EqualTo(before));Assert.That(_world.Revision,Is.Zero);
        }
        [Test]public void CheckpointCarriesFoamAndContiguousCatchup()
        {
            World();var cp=new PaintCheckpoint{Round=3,Sequence=5,Topology="foam-test",Foam=_world.Capture(),FoamRevision=0,FoamEndRevision=1};
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
            writer.Write(1);writer.Write(0);writer.Write((ushort)1);writer.Write((ushort)250);writer.Write((byte)2);
            cp.FoamJournal.Add(new FoamCommitData{Revision=1,Tick=3,PaintSequence=5,Data=stream.ToArray()});
            var decoded=PaintSnapshotCodec.Decode(PaintSnapshotCodec.Encode(cp),"foam-test",new Dictionary<int,int>(),new Dictionary<int,int>(),cp.Foam.Length);
            _world.Restore(decoded.Foam,decoded.FoamRevision);foreach(var item in decoded.FoamJournal)_world.ApplyDelta(item.Data,item.Revision);
            Assert.That(_world.Revision,Is.EqualTo(1));Assert.That(_world.Capture()[2],Is.EqualTo(2));
        }
        PaintStamp Stamp(PaintSurface surface,byte team)=>new(){SurfaceId=surface.SurfaceId,Position=new Vector3(6000,0,6000),Normal=Vector3.up,Radius=1.4f,DepthScale=1,Hardness=1,Strength=1,Team=team,ShapeSeed=InkShapeAtlas.Pack(0,123)};
        [Test]public void RepeatedShotsBuildRealHeightWithoutPaintingBridge()
        {
            var setup=World();var stamp=Stamp(setup.floor,1);
            for(int i=0;i<30;i++){Assert.That(_world.Queue(setup.floor,stamp,.06f),Is.True);Assert.That(_world.Commit(),Is.Not.Null);}
            _world.Patches[256].Sample(stamp.Position,out var top,out _,out byte owner);
            Assert.That(top.y,Is.GreaterThan(.1f));Assert.That(owner,Is.EqualTo(1));
            _world.Patches[512].Sample(stamp.Position,out var bridge,out _,out byte bridgeOwner);Assert.That(bridge.y,Is.EqualTo(3));Assert.That(bridgeOwner,Is.Zero);
            float before=top.y;stamp.Team=2;_world.Queue(setup.floor,stamp,.04f);_world.Commit();
            _world.Patches[256].Sample(stamp.Position,out top,out _,out owner);Assert.That(top.y,Is.LessThan(before));Assert.That(owner,Is.EqualTo(1));
            var collider=_world.Patches[256].Chunks[0].Collider;
            Assert.That(collider.Raycast(new Ray(stamp.Position+Vector3.up*4,Vector3.down),out var hit,5),Is.True);Assert.That(hit.point.y,Is.EqualTo(top.y).Within(.001f));
        }
        [Test]public void DeltaReplicaMatchesCommittedHeightAndOwnership()
        {
            var setup=World();var empty=_world.Capture();var commits=new List<byte[]>();var stamp=Stamp(setup.floor,1);
            for(int i=0;i<12;i++){stamp.Team=i<8?(byte)1:(byte)2;_world.Queue(setup.floor,stamp,.12f);var delta=_world.Commit();if(delta!=null)commits.Add(delta);}
            uint hash=_world.StateHash();_world.Restore(empty,0);uint revision=0;
            foreach(var delta in commits)_world.ApplyDelta(delta,++revision);
            Assert.That(_world.StateHash(),Is.EqualTo(hash));Assert.That(_world.Revision,Is.EqualTo(commits.Count));
        }
        [Test]public void PaperSupportTouchesHighestPointWithinItsFootprint()
        {
            World();var bytes=_world.Capture();int peak=8*17+9;bytes[peak*3]=0x2c;bytes[peak*3+1]=1;bytes[peak*3+2]=1;_world.Restore(bytes,1);
            var profile=Keep(ScriptableObject.CreateInstance<PaperBodyProfile>());
            var state=new PlayerSnapshot{Position=new Vector3(6000,0,6000),Swimming=true,Grounded=true,Health=100};
            Assert.That(FoamSupport.Sample(_world.Patches[256],state,profile,out var support,out _,out _),Is.True);
            Assert.That(support.y,Is.GreaterThan(.1f));
        }
        [Test]public void InteriorAndOutsideEdgeSphereUseTheSolidFoamVolume()
        {
            World();var bytes=_world.Capture();for(int i=0;i<289;i++){bytes[i*3]=0xf4;bytes[i*3+1]=1;bytes[i*3+2]=1;}_world.Restore(bytes,1);
            Assert.That(FoamSurfaceQuery.InsideOrNear(_world,new Vector3(6000,.2f,6000),.02f,out _,out var top,out _,out float inside),Is.True);
            Assert.That(inside,Is.Zero);Assert.That(top.y,Is.EqualTo(.5f).Within(.001));
            Assert.That(FoamSurfaceQuery.InsideOrNear(_world,new Vector3(6002.05f,.25f,6000),.1f,out _,out var side,out _,out float gap),Is.True);
            Assert.That(side.x,Is.EqualTo(6002).Within(.001));Assert.That(gap,Is.EqualTo(.05f).Within(.001));
            Assert.That(FoamSurfaceQuery.InsideOrNear(_world,new Vector3(6002.2f,.25f,6000),.1f,out _,out _,out _,out _),Is.False);
        }
        [Test]public void SubMillimetreDropsAccumulateAcrossCommits()
        {
            var setup=World();var stamp=Stamp(setup.floor,1);
            for(int i=0;i<100;i++){_world.Queue(setup.floor,stamp,.0001f);_world.Commit();}
            _world.Patches[256].Sample(stamp.Position,out var top,out _,out _);Assert.That(top.y,Is.GreaterThan(.001f));
        }
        [Test]public void RigidPaperCanRestOnAnEdgeOutsideItsCenter()
        {
            World();var bytes=_world.Capture();for(int i=0;i<289;i++){bytes[i*3]=0x2c;bytes[i*3+1]=1;bytes[i*3+2]=1;}_world.Restore(bytes,1);
            var profile=Keep(ScriptableObject.CreateInstance<PaperBodyProfile>());
            var state=new PlayerSnapshot{Position=new Vector3(6002.2f,.3f,6000),Swimming=true,Grounded=true,Health=100};
            Assert.That(FoamSupport.Sample(_world.Patches[256],state,profile,out var support,out _,out _),Is.True);Assert.That(support.y,Is.EqualTo(.3f).Within(.001f));
        }
        [Test]public void WarmTerrainCommitHasNoTransientManagedAllocation()
        {
            var setup=World();var stamp=Stamp(setup.floor,1);int changed=0;
            for(int i=0;i<120;i++)
            {
                stamp.Team=(byte)(i%2+1);_world.Queue(setup.floor,stamp,.06f);
                byte[] delta=null;
                if(i>=80)Assert.That((TestDelegate)(()=>delta=_world.Commit(false)),new NUnit.Framework.Constraints.NotConstraint(new UnityEngine.TestTools.Constraints.AllocatingGCMemoryConstraint()));
                else delta=_world.Commit(false);
                if(i<80||delta==null)continue;
                changed++;
            }
            Assert.That(changed,Is.GreaterThan(20),"Measure changing geometry, not an idle world");
        }
        [Test]public void BorrowedDeltaAndPooledJournalKeepExactWireLength()
        {
            var setup=World();var empty=_world.Capture();_world.Queue(setup.floor,Stamp(setup.floor,1),.1f);
            var delta=_world.Commit(false);int count=_world.LastDeltaBytes;uint expected=_world.StateHash();
            var record=FoamCommitData.Rent(count);
            try
            {
                Buffer.BlockCopy(delta,0,record.Data,0,count);record.Revision=1;
                using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);record.Write(writer);stream.Position=0;
                using var reader=new BinaryReader(stream);var decoded=FoamCommitData.Read(reader,4096);Assert.That(decoded.Count,Is.EqualTo(count));
                _world.Restore(empty,0);_world.ApplyDelta(decoded.Data,1,decoded.Count);Assert.That(_world.StateHash(),Is.EqualTo(expected));
            }
            finally{record.Release();}
        }
    }
}
#endif
