#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using UnityEngine;
using UnityEngine.TestTools;

namespace Splatoon.Tests
{
    public sealed partial class BlockFixedGridPaidGameplayTests
    {
        [UnityTest, Timeout(600000)]
        public IEnumerator OwnFixedTopSwimRecoversActualMaterialAtAuthoredRate()
            => Run("T01",()=>RecoverOnTop(true));
        [UnityTest, Timeout(600000)]
        public IEnumerator EnemyFixedTopCannotGrantFriendlySwimRateOrSurfaceSource()
            => Run("T02",()=>RecoverOnTop(false));
        void RecoverOnTop(bool friendly)
        {
            var pad=PreparePad(friendly?Team:Enemy); LandOn(pad);
            for(int i=0;i<3;i++){Drive(Neutral(true),"top.swim-entry");AssertLegalPose("top.entry-pose");}
            AssertFixedGround(pad.Owner);
            const int ticks=12; var s=_host.Snapshot.Value; var hero=GameplayConfig.GetHero(s.HeroId);
            float rate=friendly?hero.SwimRecoverInk:hero.RecoverInk;
            Assert.That(hero.SwimRecoverInk, Is.Not.EqualTo(hero.RecoverInk), "Authored rates must distinguish the expected source.");
            s.Ink=hero.MaxInk*.2f; s.InkRecoverAt=0;
            Assert.That(s.Ink+rate*ticks*Dt, Is.LessThan(hero.MaxInk), "Do not hide the rate behind the material cap.");
            Restore(s); float baseline=s.Ink; uint action=s.SubAction;
            int paid=_payments.Count; string occupancy=WorldIdentity();
            _phase="measured-resource-outer-step";
            for(int i=1;i<=ticks;i++)
            {
                Drive(Neutral(true),"resource.tick"); s=_host.Snapshot.Value; AssertFixedGround(pad.Owner);
                AssertLegalPose("resource.pose");
                Assert.That(s.Ink-baseline, Is.EqualTo(rate*i*Dt).Within(.002f), "Actual ResourceSimulation delta at each tick");
                Assert.That(s.FriendlyInkContact, Is.EqualTo(friendly)); Assert.That(s.HasInkRecovery, Is.EqualTo(friendly));
                if(friendly){Assert.That(s.Movement,Is.EqualTo(MovementMode.GroundInk));Assert.That(s.SwimSource,Is.EqualTo(SwimSurface.Friendly));}
                else Assert.That(s.SwimSource, Is.Not.EqualTo(SwimSurface.Friendly));
            }
            Assert.That(_payments.Count, Is.EqualTo(paid)); Assert.That(s.SubAction, Is.EqualTo(action));
            Assert.That(WorldIdentity(), Is.EqualTo(occupancy), "Recovery never creates or moves terrain.");
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator OwnFixedWallActuallyClimbsThenJumpsAway()
            => Run("T03",()=>
            {
                PrepareAuthoredEdgeWall(); EnterFixedWall();
                string occupancy=WorldIdentity(); int growth=0;
                var entry=_host.Snapshot.Value;
                _phase="actual-fixed-wall-climb";
                for(int i=0;i<12&&growth<2;i++)
                {
                    var before=_host.Snapshot.Value; Drive(Neutral(true,Vector2.up),"wall.climb");
                    var after=_host.Snapshot.Value; AssertFixedWall(); AssertLegalPose("wall.climb-pose");
                    float delta=after.Position.y-before.Position.y;
                    if(delta>.001f)growth++;
                    Assert.That(delta, Is.InRange(-.001f,GameplayConfig.GetHero(after.HeroId).WallSwimSpeed*Dt+.005f));
                }
                Assert.That(growth, Is.GreaterThanOrEqualTo(2), "Two different actual fixed ticks must gain height before mantle.");
                var climbed=_host.Snapshot.Value; var jump=Neutral(true,Vector2.up); jump.JumpSequence=climbed.ConsumedJump+1;
                _phase="actual-fixed-wall-jump"; Drive(jump,"wall.jump"); var jumped=_host.Snapshot.Value;
                Assert.That(jumped.ConsumedJump,Is.EqualTo(jump.JumpSequence));
                Assert.That(jumped.Movement,Is.EqualTo(MovementMode.Air)); Assert.That(jumped.VerticalSpeed,Is.GreaterThan(0));
                Assert.That(Vector3.Dot(jumped.PlanarVelocity,climbed.WallNormal),Is.GreaterThan(0));
                Assert.That(Vector3.Dot(jumped.Position-climbed.Position,climbed.WallNormal),Is.GreaterThan(0));
                Assert.That(jumped.Grounded||jumped.SupportIsFixedGrid||jumped.HasInkRecovery||jumped.FriendlyInkContact,Is.False);
                AssertLegalPose("wall.jump-pose");
                Assert.That(jumped.Position.y,Is.GreaterThan(entry.Position.y));
                Assert.That(WorldIdentity(),Is.EqualTo(occupancy));
                Assert.That(_payments,Is.Empty);
            });
        [UnityTest, Timeout(600000)]
        public IEnumerator OwnFourLayerWallMantlesOntoItsDataTop()
            => Run("T04",()=>
            {
                PrepareAuthoredEdgeWall(); EnterFixedWall();
                string occupancy=WorldIdentity(); bool mantle=false; int mantleTicks=0;
                _phase="actual-fixed-wall-mantle";
                for(int i=0;i<180;i++)
                {
                    Drive(Neutral(true,Vector2.up),"wall.mantle-input"); var s=_host.Snapshot.Value;
                    if(s.Movement==MovementMode.Mantle){mantle=true;mantleTicks++;}
                    AssertLegalPose("wall.mantle-pose");
                    if(mantle&&s.Movement!=MovementMode.Mantle&&s.Grounded&&s.SupportIsFixedGrid)break;
                }
                var landed=_host.Snapshot.Value;
                Assert.That(mantle&&mantleTicks>0,Is.True,"Actual outer-Step snapshots must observe the production Mantle branch.");
                Assert.That(landed.Movement,Is.EqualTo(MovementMode.GroundInk));
                AssertFixedGround(Team); Assert.That(landed.Position.y,Is.EqualTo(3.3f).Within(.08f));
                Assert.That(World.TryGet(landed.SupportFixedId,out var support),Is.True);
                Assert.That(support.Cell.y-support.Support.BaseCellY,Is.EqualTo(3));
                Assert.That(landed.HasInkRecovery&&landed.FriendlyInkContact,Is.True);
                Assert.That(landed.PaperPose,Is.EqualTo(PaperPose.Ground));
                Assert.That(World.Blocks.Values.All(b=>b.Cell.y-b.Support.BaseCellY<4&&BlockFixedGridWorld.GetBounds(b).max.y-b.Support.Height<=1.20001f),Is.True);
                Assert.That(WorldIdentity(),Is.EqualTo(occupancy)); Assert.That(_payments,Is.Empty);
            });
        void PrepareAuthoredEdgeWall()
        {
            _phase="diagnostic-authored-edge-wall";
            // CenterCover_1_-1 is an ORIGINAL TrainingGround box: x=[3,5], y=[0,2.2], z=[-9.5,-6.5].
            // Its x=3 edge aligns to the global .30m lattice. The approach is outside the original
            // cover, over original y=0 ground; no added platform, collider override or map replacement.
            ResetPose(new Vector3(0,1,-20.25f),0);
            var edge=MapHit(new Vector3(3.15f,5,-7.95f),6,"wall.original-cover-top");
            Assert.That(edge.collider.name,Is.EqualTo("CenterCover_1_-1"));
            Assert.That(edge.point.y,Is.EqualTo(2.2f).Within(.002f));
            Assert.That(edge.collider.bounds.min.x,Is.EqualTo(3).Within(.002f));
            var approach=MapHit(new Vector3(2.48f,1,-7.95f),2,"wall.original-approach-floor");
            Assert.That(approach.point.y,Is.EqualTo(0).Within(.002f));
            for(int x=10;x<=15;x++)for(int z=-31;z<=-23;z++)PrepareColumn(new Vector2Int(x,z),4,Team,5);
            Assert.That(World.Count,Is.EqualTo(6*9*4));
            float baseY=BlockFixedGridMapSupport.GridBaseY(edge.point.y)*BlockWorld.CellSize;
            ResetPose(new Vector3(3-.52f,baseY+.02f,-7.95f),90);
            AssertLegalPose("wall.initial-full-pose");
            Assert.That(PlayerMotorSimulation.Query(_host.Snapshot.Value.Position+Vector3.up*(HeroBodyShape.For(_host.Snapshot.Value.HeroId).CompactHeight*.5f),
                Vector3.right,GameplayConfig.GetHero(_host.Snapshot.Value.HeroId).WallProbeDistance,out var contact),Is.True);
            Assert.That(contact.Kind,Is.EqualTo(BlockContactKind.FixedGrid)); Assert.That(contact.Owner,Is.EqualTo(Team));
            LogWorld("wall.scaffold-complete");
        }
        void EnterFixedWall()
        {
            _phase="ordinary-fixed-wall-entry";
            for(int i=0;i<90&&_host.Snapshot.Value.Movement!=MovementMode.WallInk;i++)
            {Drive(Neutral(true,Vector2.up),"wall.enter");AssertLegalPose("wall.entry-pose");}
            AssertFixedWall();
        }
        void AssertFixedWall()
        {
            var s=_host.Snapshot.Value; var hero=GameplayConfig.GetHero(s.HeroId);
            Assert.That(s.Movement,Is.EqualTo(MovementMode.WallInk)); Assert.That(s.PaperPose,Is.EqualTo(PaperPose.Wall));
            Assert.That(s.HasInkRecovery&&s.FriendlyInkContact,Is.True); Assert.That(s.SwimSource,Is.EqualTo(SwimSurface.Friendly));
            var origin=s.Position+Vector3.up*(hero.CompactHeight*.5f)+s.WallNormal*.08f;
            Assert.That(PlayerMotorSimulation.Query(origin,-s.WallNormal,hero.WallProbeDistance+.08f,out var wall),Is.True);
            Assert.That(wall.Kind,Is.EqualTo(BlockContactKind.FixedGrid)); Assert.That(wall.Stable&&wall.Climbable,Is.True);
            Assert.That(wall.Owner,Is.EqualTo(Team)); Assert.That(World.TryGet(wall.FixedHandle,out _),Is.True);
            Assert.That(wall.Point.x,Is.EqualTo(3).Within(.002f));
            Log("wall.fixed-contact",new WallTrace{handle=new HandleTrace(wall.FixedHandle),cell=wall.Cell,owner=wall.Owner,point=wall.Point,normal=wall.Normal});
        }

        [Serializable] sealed class LaunchTrace
        { public PlayerSnapshot candidate; public SubPreviewResult preview; public HandleTrace target; public int candidates; }
        [Serializable] sealed class FuseTrace
        { public uint entity,round,action; public double original,current,at; public bool attached,removed; public HandleTrace attachment; }
        [Serializable] sealed class StimulusTrace
        { public StateTrace before,after; public BlockMutationResult result; public int relocated,converted; public double fuse; }
        sealed class PaidAttached
        {
            public SubWeaponService.Entity Entity;
            public BlockFixedGridState[] Stack;
            public BlockQueryContact OriginalQuery;
            public int PaymentCount;
            public uint Action;
            public double OriginalFuse;
        }
        PlayerSnapshot FindNaturalSuctionLaunch(BlockFixedGridState target)
        {
            var aim=new TpsAimSolver(); var points=new List<Vector3>(); var basis=_host.Snapshot.Value;
            var config=PlayerLoadout.SubWeapon(basis); var center=BlockWorld.Center(target.Cell); int tried=0;
            // Bounded diagnostic preview search only. Preview cannot charge, spawn, paint or damage.
            // Actual measured launch below still uses real input -> outer Step -> production SolveLaunch.
            foreach(float distance in new[]{2.5f,3f,3.5f,4f,5f})
            for(int pitch=0;pitch<=65;pitch++)
            {
                tried++; var candidate=basis; candidate.Position=new Vector3(center.x,.04f,center.z-distance);
                candidate.Yaw=candidate.BodyYaw=0;candidate.Pitch=pitch;candidate.Grounded=true;candidate.Movement=MovementMode.Human;
                var launch=SubWeaponService.SolveLaunch(_host,candidate,config,0);
                var identity=new SubEntityState{Owner=_host.PlayerId,Team=candidate.Team,Hero=candidate.HeroId,Type=SubWeaponType.SuctionBomb};
                var preview=SubWeaponMotion.Preview(launch,config,identity,points,aim);
                if(!preview.Valid||preview.End!=SubPreviewEnd.Deployment)continue;
                if(!Terrain.TryCastFixed(preview.Position+preview.Normal*.02f,-preview.Normal,.25f,0,out var hit)||hit.State.Handle!=target.Handle)continue;
                if(Vector3.Distance(preview.Position,hit.Point+preview.Normal*SubWeaponMotion.SurfaceOffset)>.003f)continue;
                Log("diagnostic.preview-launch",new LaunchTrace{candidate=candidate,preview=preview,target=new HandleTrace(target.Handle),candidates=tried});
                return candidate;
            }
            Assert.Fail("No real authored launch reaches the required top handle in the bounded preview search; actual paid stage NOT REACHED.");
            return default;
        }
        PaidAttached PayAndNaturallyAttach(bool prepareStack=true,BlockFixedGridState[] existing=null)
        {
            _phase="diagnostic-suction-scaffold";
            var stack=prepareStack?PrepareColumn(new Vector2Int(0,-68),4,Team):existing;
            Assert.That(stack,Is.Not.Null); Assert.That(stack.Length,Is.EqualTo(4));
            var target=stack[3]; ResetPose(new Vector3(0,1,-23.5f),0,suction:true);
            var launch=FindNaturalSuctionLaunch(target);
            ResetPose(launch.Position,launch.Yaw,launch.Pitch,true);
            for(int i=0;i<60&&!_host.Snapshot.Value.Grounded;i++)Drive(Neutral(),"suction.actor-settle");
            Assert.That(_host.Snapshot.Value.Grounded,Is.True);AssertLegalPose("suction.actor-pose");
            // The real input below launches from the ACTUALLY settled pose; the diagnostic preview
            // is not substituted for the real collision/attachment assertions after automatic Spawn.
            var config=PlayerLoadout.SubWeapon(_host.Snapshot.Value);Assert.That(config.Type,Is.EqualTo(SubWeaponType.SuctionBomb));
            Assert.That(config.InkLock(0),Is.GreaterThan(Dt),"This authored lock keeps the one payment-step ink delta unambiguous.");
            var before=_host.Snapshot.Value;int paidBefore=_payments.Count;
            var priorEntities=_match.SubWeapons.Entities.Select(e=>(e.State.Round,e.State.Id)).ToHashSet();
            _phase="measured-real-sub-input-payment";
            var press=Neutral();press.SubHeld=true;press.SubPressSequence=before.SubConsumedPress+1;Drive(press,"suction.press");
            Assert.That(_host.Snapshot.Value.SubPhase,Is.EqualTo(SubWeaponPhase.Holding));
            Assert.That(_payments.Count,Is.EqualTo(paidBefore));
            var release=Neutral();release.SubHeld=false;release.SubReleaseSequence=_host.Snapshot.Value.SubConsumedRelease+1;Drive(release,"suction.release");
            float paidDelta=float.NaN; PlayerSnapshot paidState=default;
            // A zero-startup config could commit on release; this case deliberately records that as well.
            if(_host.Snapshot.Value.SubAction==before.SubAction+1){paidState=_host.Snapshot.Value;paidDelta=before.Ink-paidState.Ink;}
            for(int i=0;i<180&&_host.Snapshot.Value.SubAction==before.SubAction;i++)
            {
                var stepBefore=_host.Snapshot.Value;Drive(Neutral(),"suction.startup");
                if(_host.Snapshot.Value.SubAction!=stepBefore.SubAction){paidState=_host.Snapshot.Value;paidDelta=stepBefore.Ink-paidState.Ink;}
            }
            Assert.That(paidState.SubAction,Is.EqualTo(before.SubAction+1));
            Assert.That(paidDelta,Is.EqualTo(config.Common.inkCost).Within(.0002f),"One actual material debit in the outer Step");
            Assert.That(_payments.Count,Is.EqualTo(paidBefore+1));var payment=_payments.Last();
            Assert.That(payment.Source.Kind,Is.EqualTo(PaintAttackKind.Sub));Assert.That(payment.Source.Owner,Is.EqualTo(_host.PlayerId));
            Assert.That(payment.Source.Round,Is.EqualTo(_match.State.Value.Round));Assert.That(payment.Source.Action,Is.EqualTo((ulong)paidState.SubAction));
            Assert.That(payment.Material,Is.EqualTo(config.Common.inkCost).Within(1e-6));
            var spawned=_match.SubWeapons.Entities.Where(e=>!priorEntities.Contains((e.State.Round,e.State.Id))).ToArray();
            Assert.That(spawned.Length,Is.EqualTo(1),"Automatic production Spawn must create exactly one new entity.");var entity=spawned[0];
            Assert.That(entity.State.Action,Is.EqualTo(paidState.SubAction));Assert.That(entity.BlockAttack,Is.Not.Null);
            Assert.That(entity.BlockAttack.BuildLimit,Is.EqualTo((int)Math.Floor(config.Common.inkCost*(double)BlockGameplaySettings.BlocksPerMaterial+1e-8)));
            Assert.That(entity.BlockAttack.DirectLimit,Is.EqualTo(4096));Assert.That(entity.BlockAttack.Added,Is.Zero);Assert.That(entity.BlockAttack.RemovedEnemy,Is.Zero);
            _phase="measured-natural-suction-flight";
            for(int i=0;i<240&&!entity.Attached&&!entity.Removed;i++)Drive(Neutral(),"suction.flight");
            Assert.That(entity.Removed,Is.False);Assert.That(entity.Attached&&entity.BlockAttachment,Is.True);
            Assert.That(entity.State.Phase,Is.EqualTo(SubEntityPhase.Active));Assert.That(entity.FixedAttachmentHandle,Is.EqualTo(target.Handle));
            Assert.That(ReferenceEquals(entity.FixedAttachmentWorld,World),Is.True);Assert.That(entity.Fuse,Is.GreaterThan(_now));
            Assert.That(_lifecycle.Count(e=>e.Kind==SubLifecycleKind.Spawn&&e.State.Round==entity.State.Round&&e.State.Id==entity.State.Id),Is.EqualTo(1));
            Assert.That(_lifecycle.Any(e=>e.Kind==SubLifecycleKind.Spawn&&e.State.Id==entity.State.Id&&e.State.Phase==SubEntityPhase.Flying&&e.State.Velocity.sqrMagnitude>1),Is.True,
                "Natural flight starts at the real production launch, never a manually attached entity.");
            Assert.That(_payments.Count,Is.EqualTo(paidBefore+1));Assert.That(_host.Snapshot.Value.SubAction,Is.EqualTo(paidState.SubAction));
            Assert.That(new TpsAimSolver().ClosestCast(BlockWorld.Center(target.Cell)+Vector3.up*.6f,Vector3.down,.8f,0,_host.PlayerId,out var targetHit,false),Is.True);
            Assert.That(targetHit.BlockQuery.IsKnown,Is.True);
            Assert.That(targetHit.BlockQuery.TryResolveCurrent(out var contact),Is.True);
            Assert.That(contact.FixedHandle,Is.EqualTo(target.Handle));
            LogEntity("suction.naturally-attached",entity);LogWorld("suction.measured-baseline");
            return new PaidAttached{Entity=entity,Stack=stack,OriginalQuery=targetHit.BlockQuery,PaymentCount=_payments.Count,Action=paidState.SubAction,OriginalFuse=entity.Fuse};
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator PaidSuctionNaturallyAttachesAndFollowsTheSameRelocatedHandle()
            => Run("D01",()=>
            {
                var a=PayAndNaturallyAttach();var e=a.Entity;var old=a.Stack[3];var oldPosition=e.State.Position;var offset=e.FixedAttachmentOffset;
                int added=e.BlockAttack.Added,removed=e.BlockAttack.RemovedEnemy;double points=_host.Snapshot.Value.SpecialPoints;
                _phase="diagnostic-support-removal-stimulus";
                var result=World.Destroy(a.Stack.Take(3).Select(s=>s.Handle),Team,maxEnemy:0,maxFriendly:3);
                Assert.That(result.RemovedFriendly,Is.EqualTo(3));Assert.That(World.TryGet(old.Handle,out var moved),Is.True);
                Assert.That(moved.Cell,Is.EqualTo(a.Stack[0].Cell));Assert.That(moved.Owner,Is.EqualTo(old.Owner));
                Assert.That(World.LastRelocatedCount,Is.EqualTo(1));Assert.That(World.LastConvertedCount,Is.Zero);
                Assert.That(a.OriginalQuery.TryResolveCurrent(out _),Is.False,"Old location/revision query is stale despite retained stable handle.");
                Log("suction.relocation-stimulus",new StimulusTrace{before=new StateTrace(old),after=new StateTrace(moved),result=result,relocated=World.LastRelocatedCount,converted=World.LastConvertedCount,fuse=e.Fuse});
                _phase="measured-attachment-follow";Drive(Neutral(),"suction.follow-relocated-handle");
                Assert.That(e.FixedAttachmentHandle,Is.EqualTo(old.Handle));Assert.That(e.FixedAttachmentOffset,Is.EqualTo(offset));
                Assert.That(Vector3.Distance(e.State.Position,BlockWorld.Center(moved.Cell)+offset),Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(e.State.Position,oldPosition),Is.GreaterThan(.5f));
                Assert.That(e.Fuse,Is.EqualTo(a.OriginalFuse).Within(1e-8));
                var captured=_match.SubWeapons.Capture().Single(s=>s.Id==e.State.Id&&s.Round==e.State.Round);
                Assert.That(captured.Position,Is.EqualTo(e.State.Position));Assert.That(captured.FuseAt,Is.EqualTo(a.OriginalFuse).Within(1e-8));
                // Suction has authored durability=0 and no native hit target. The authoritative capture
                // is its actual object/blast point; there must be no surviving state at the old pose.
                Assert.That(_match.SubWeapons.Capture().Any(s=>s.Id==e.State.Id&&Vector3.Distance(s.Position,oldPosition)<.01f),Is.False);
                Assert.That(e.HitTarget,Is.Null,"Do not invent a native suction hit collider to prove a vacuous old-volume assertion.");
                Assert.That(e.BlockAttack.Added,Is.EqualTo(added));Assert.That(e.BlockAttack.RemovedEnemy,Is.EqualTo(removed));
                Assert.That(_host.Snapshot.Value.SpecialPoints,Is.EqualTo(points));Assert.That(_payments.Count,Is.EqualTo(a.PaymentCount));
            });
        [UnityTest, Timeout(600000)]
        public IEnumerator PaidSuctionDeletedTargetCannotAttachToSameCellReplacementOrExtendFuse()
            => Run("D02",()=>
            {
                var a=PayAndNaturallyAttach();var old=a.Stack[3];_phase="diagnostic-delete-replace-stimulus";
                Assert.That(World.Destroy(new[]{old.Handle},Team,maxEnemy:0,maxFriendly:1).RemovedFriendly,Is.EqualTo(1));
                Assert.That(World.TryGet(old.Handle,out _),Is.False);
                Assert.That(World.Build(new[]{old.Cell},old.Owner,1).Added,Is.EqualTo(1),World.LastFailure);
                Assert.That(World.TryGet(old.Cell,out var replacement),Is.True);Assert.That(replacement.Handle,Is.Not.EqualTo(old.Handle));
                Assert.That(a.OriginalQuery.TryResolveCurrent(out _),Is.False);
                ObserveNaturalDetachAndLifetime(a,replacement.Handle);
            });
        [UnityTest, Timeout(600000)]
        public IEnumerator PaidSuctionPaintConversionInvalidatesAttachmentAndKeepsOneLifecycle()
            => Run("D03",()=>
            {
                var a=PayAndNaturallyAttach();var old=a.Stack[3];_phase="diagnostic-paint-conversion-stimulus";
                Assert.That(World.Paint(new[]{old.Support.SurfaceKey},Enemy,1).Added,Is.EqualTo(1));
                Assert.That(World.Surfaces.TryGet(old.Support.SurfaceKey,out var enemyPaint)&&enemyPaint.Owner==Enemy,Is.True);
                var result=World.Destroy(a.Stack.Take(3).Select(s=>s.Handle),Team,maxEnemy:0,maxFriendly:3);
                Assert.That(result.RemovedFriendly,Is.EqualTo(3));Assert.That(World.LastConvertedCount,Is.EqualTo(1));
                Assert.That(World.TryGet(old.Handle,out _),Is.False);Assert.That(World.Count,Is.Zero);
                Assert.That(World.Surfaces.TryGet(old.Support.SurfaceKey,out var ownPaint)&&ownPaint.Owner==old.Owner,Is.True);
                Assert.That(a.OriginalQuery.TryResolveCurrent(out _),Is.False);Assert.That(_payments.Count,Is.EqualTo(a.PaymentCount));
                ObserveNaturalDetachAndLifetime(a,default);
            });
        static void AssertAll(params Action[] assertions)
        {
            var failures=new List<string>();
            for(int i=0;i<assertions.Length;i++)
            {
                try { assertions[i](); }
                catch(AssertionException error) { failures.Add("Assertion "+(i+1)+": "+error.Message); }
            }
            if(failures.Count!=0)Assert.Fail(string.Join("\n\n",failures));
        }
        void ObserveNaturalDetachAndLifetime(PaidAttached a,BlockFixedGridHandle replacement)
        {
            var e=a.Entity;double first=a.OriginalFuse,maxFuse=first;int lifecycleStart=_lifecycle.Count;
            int added=e.BlockAttack.Added,removed=e.BlockAttack.RemovedEnemy;uint round=e.State.Round,id=e.State.Id,action=e.State.Action;
            _phase="measured-natural-detach-recontact-and-fuse";bool detached=false,recontact=false;double detachAt=_now+Dt;
            // Do not abort at the first suspected fuse reset. Retain the complete natural lifecycle,
            // then report ALL strong original-fuse / explosion / removal assertions together.
            int maximumTicks=(int)Math.Ceiling((e.Config.Flight.lifetime+e.Config.Suction.fuse*2+1)/Dt);
            for(int i=0;i<maximumTicks&&!e.Removed;i++)
            {
                Drive(Neutral(),"suction.detach-natural-lifetime");maxFuse=Math.Max(maxFuse,e.Fuse);
                detached|=_lifecycle.Skip(lifecycleStart).Any(x=>x.State.Round==round&&x.State.Id==id&&x.Kind==SubLifecycleKind.Motion&&x.State.Phase==SubEntityPhase.Flying);
                if(detached&&e.Attached){recontact=true;Assert.That(e.FixedAttachmentHandle,Is.Not.EqualTo(a.Stack[3].Handle));
                    if(replacement.IsValid&&e.FixedAttachmentHandle.IsValid)Assert.That(e.FixedAttachmentHandle,Is.EqualTo(replacement));}
                Log("suction.fuse-history",new FuseTrace{entity=id,round=round,action=action,original=first,current=e.Fuse,at=_now,attached=e.Attached,removed=e.Removed,attachment=new HandleTrace(e.FixedAttachmentHandle)});
            }
            var events=_lifecycle.Skip(lifecycleStart).Where(x=>x.State.Round==round&&x.State.Id==id).ToArray();
            var detachedEvent=events.FirstOrDefault(x=>x.Kind==SubLifecycleKind.Motion&&x.State.Phase==SubEntityPhase.Flying);
            var activeAfter=events.FirstOrDefault(x=>x.Kind==SubLifecycleKind.Phase&&x.State.Phase==SubEntityPhase.Active&&x.Sequence>detachedEvent.Sequence);
            var explosions=_effects.Where(x=>x.Round==round&&x.EntityId==id&&x.Action==action&&x.Kind==SubEffectKind.Explosion).ToArray();
            AssertAll(
                ()=>Assert.That(detached,Is.True,"Actual service must publish a Flying transition after old handle invalidation."),
                ()=>Assert.That(detachedEvent.State.Changed,Is.EqualTo(detachAt).Within(1e-7)),
                ()=>Assert.That(recontact,Is.True,"Natural gravity/contact must actually exercise the suspected recontact fuse path."),
                ()=>Assert.That(activeAfter.Sequence,Is.GreaterThan(detachedEvent.Sequence),"New attachment follows actual detach/flight, not old ID inheritance."),
                ()=>Assert.That(maxFuse,Is.EqualTo(first).Within(1e-7),"Original fuse MUST NOT extend on natural map/new-block contact."),
                ()=>Assert.That(e.Removed,Is.True),
                ()=>Assert.That(explosions.Length,Is.EqualTo(1)),
                ()=>{if(explosions.Length==1)Assert.That(explosions[0].At,Is.InRange(first-1e-7,first+Dt+1e-5));},
                ()=>Assert.That(_lifecycle.Count(x=>x.State.Round==round&&x.State.Id==id&&x.Kind==SubLifecycleKind.Remove),Is.EqualTo(1)),
                ()=>Assert.That(_payments.Count,Is.EqualTo(a.PaymentCount)),
                ()=>Assert.That(_host.Snapshot.Value.SubAction,Is.EqualTo(a.Action)),
                ()=>Assert.That(e.BlockAttack.Added,Is.EqualTo(added)),
                ()=>Assert.That(e.BlockAttack.RemovedEnemy,Is.EqualTo(removed)),
                ()=>Assert.That(_match.SubWeapons.Capture().Any(x=>x.Id==id&&x.Round==round),Is.False));
        }
        [UnityTest, Timeout(600000)]
        public IEnumerator PaidSuctionRoundClearInvalidatesOldEntityAndBlockHandles()
            => Run("D04",()=>
            {
                var a=PayAndNaturallyAttach();var old=a.Entity;uint oldRound=old.State.Round,oldId=old.State.Id;var oldHandle=a.Stack[3].Handle;
                _phase="diagnostic-round-deadline-initial-condition";
                // Debug rooms intentionally disallow StartRound. Set only a documented short Playing
                // deadline as initial condition, then exercise REAL HasEnded/FinishRound and public
                // ReturnToRoom. Never call ResetPaint/ClearSubWeapons directly as the test action.
                var state=_match.State.Value;state.Phase=MatchPhase.Playing;state.StartsAt=_now-1;state.EndsAt=_now+Dt*.5;_match.State.Value=state;
                Drive(Neutral(),"round.actual-finish");Assert.That(_match.State.Value.Phase,Is.EqualTo(MatchPhase.Finished));
                Assert.That(World.Frozen,Is.True);Assert.That(old.Removed,Is.True);
                Assert.That(_match.SubWeapons.Capture().Count,Is.Zero);
                _phase="actual-public-return-to-room";_match.ReturnToRoom();HarvestServiceEvents("round.return-to-room");
                Assert.That(_match.State.Value.Phase,Is.EqualTo(MatchPhase.Practice));Assert.That(_match.State.Value.Round,Is.EqualTo(oldRound+1));
                Assert.That(World.Round,Is.EqualTo(oldRound+1));Assert.That(World.Count,Is.Zero);Assert.That(World.Frozen,Is.False);
                Assert.That(World.TryGet(oldHandle,out _),Is.False);Assert.That(a.OriginalQuery.TryResolveCurrent(out _),Is.False);
                LogWorld("round.cleared-paired-world");
                var stack=PrepareColumn(new Vector2Int(0,-68),4,Team);
                Assert.That(stack[3].Cell,Is.EqualTo(a.Stack[3].Cell));Assert.That(stack[3].Handle,Is.Not.EqualTo(oldHandle));
                var next=PayAndNaturallyAttach(false,stack);
                Assert.That(next.Entity.State.Round,Is.EqualTo(oldRound+1));Assert.That(next.Entity.State.Id,Is.Not.EqualTo(oldId));
                Assert.That(next.Entity.FixedAttachmentHandle,Is.EqualTo(stack[3].Handle));
                while(_now<=a.OriginalFuse+Dt*2)Drive(Neutral(),"round.past-old-fuse");
                AssertAll(
                    ()=>Assert.That(old.Removed,Is.True),
                    ()=>Assert.That(World.TryGet(oldHandle,out _),Is.False),
                    ()=>Assert.That(_match.SubWeapons.Capture().Any(x=>x.Round==oldRound&&x.Id==oldId),Is.False),
                    ()=>Assert.That(_effects.Any(x=>x.Round==oldRound&&x.EntityId==oldId),Is.False,"Old round entity cannot emit a delayed explosion/effect."),
                    ()=>Assert.That(_actual.Any(x=>x.Source.Round==oldRound&&x.Source.Action==(ulong)a.Action&&x.Source.Kind==PaintAttackKind.Sub&&x.HadChanges),Is.False),
                    ()=>Assert.That(_lifecycle.Count(x=>x.State.Round==oldRound&&x.State.Id==oldId&&x.Kind==SubLifecycleKind.Remove),Is.EqualTo(1)),
                    ()=>Assert.That(_payments.Count,Is.EqualTo(a.PaymentCount+1)),
                    ()=>Assert.That(_payments.Last().Source.Round,Is.EqualTo(oldRound+1)));
            });
    }
}
#endif
