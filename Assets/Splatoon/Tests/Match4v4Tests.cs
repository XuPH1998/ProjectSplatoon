#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;

namespace Splatoon.Tests
{
    public sealed class Match4v4Tests
    {
        [Test] public void GeneratedModeLoadsEightPlayersAndMinimumTwo()
        {
            HeroMigrationTests.Load();
            try
            {
                Splatoon.Config.GameplayConfig.Validate();
                Assert.That(Splatoon.Config.GameplayConfig.Mode.MaxPlayers,Is.EqualTo(8));
                Assert.That(Splatoon.Config.GameplayConfig.Mode.MinPlayers,Is.EqualTo(2));
            }
            finally { Splatoon.Config.LubanConfigService.Current.Reset(); }
        }
        [Test] public void EightPlayersHaveUniqueSlotsAndDeparturesCanBeReused()
        {
            var roster = new List<PlayerSnapshot>();
            int pink=0,blue=0;
            for(int i=0;i<8;i++)
            {
                byte team=PrototypeRules.ChooseTeam(pink,blue);
                Assert.That(TeamSelectionRules.TryFindSlot(team,roster,out byte slot),Is.True);
                Assert.That(slot,Is.EqualTo(i/2));
                roster.Add(new PlayerSnapshot {Team=team,Slot=slot});
                if(team==1) pink++; else blue++;
            }
            Assert.That(pink,Is.EqualTo(4)); Assert.That(blue,Is.EqualTo(4));
            Assert.That(TeamSelectionRules.TryFindSlot(1,roster,out _),Is.False);
            Assert.That(TeamSelectionRules.TryFindSlot(2,roster,out _),Is.False);
            roster.RemoveAt(4);
            Assert.That(TeamSelectionRules.TryFindSlot(1,roster,out byte freed),Is.True); Assert.That(freed,Is.EqualTo(2));
        }
        [TestCase(1,1,true)] [TestCase(2,1,true)] [TestCase(4,4,true)] [TestCase(4,0,false)] [TestCase(0,2,false)] [TestCase(5,1,false)]
        public void StartRequiresBothTeamsWithinCapacity(int pink,int blue,bool allowed) =>
            Assert.That(PrototypeRules.CanStart(pink,blue,MatchPhase.Practice),Is.EqualTo(allowed));
        static MatchCombatStats Ledger()
        {
            var ledger=new MatchCombatStats(); ledger.Reset(9);
            for(ulong id=0;id<5;id++) ledger.BeginLife(id,(byte)(id==4?2:1),1);
            return ledger;
        }
        [Test] public void HostZeroGetsKillAndFiveSecondAssistsAreUniqueAndInclusive()
        {
            var ledger=Ledger();
            ledger.RecordDamage(4,1,1,1,10,false,10,MatchPhase.Playing);
            ledger.RecordDamage(4,1,1,1,10,false,10,MatchPhase.Playing);
            ledger.RecordDamage(4,1,2,1,10,false,9.999,MatchPhase.Playing);
            ledger.RecordDamage(4,1,3,1,10,false,14,MatchPhase.Playing);
            ledger.RecordDamage(4,1,0,1,60,true,15,MatchPhase.Playing);
            Assert.That(ledger.Get(0).Kills,Is.EqualTo(1)); Assert.That(ledger.Get(0).Assists,Is.Zero);
            Assert.That(ledger.Get(1).Assists,Is.EqualTo(1)); Assert.That(ledger.Get(2).Assists,Is.Zero);
            Assert.That(ledger.Get(3).Assists,Is.EqualTo(1)); Assert.That(ledger.Get(4).Deaths,Is.EqualTo(1));
            ledger.RecordDamage(4,1,0,1,10,true,15,MatchPhase.Playing);
            ledger.RecordDeath(4,1,null,15,MatchPhase.Playing);
            Assert.That(ledger.Get(4).Deaths,Is.EqualTo(1)); Assert.That(ledger.Get(0).Kills,Is.EqualTo(1));
        }
        [Test] public void WarmupZeroDamageFriendlyAndExpiredLifeCannotEarnAssists()
        {
            var ledger=Ledger();
            ledger.RecordDamage(4,1,1,1,10,false,0,MatchPhase.Practice);
            ledger.RecordDamage(4,1,2,1,0,false,0,MatchPhase.Playing);
            ledger.RecordDamage(4,1,3,2,10,false,0,MatchPhase.Playing);
            ledger.RecordDamage(4,0,1,1,10,false,0,MatchPhase.Playing);
            ledger.RecordDamage(4,1,0,1,100,true,1,MatchPhase.Playing);
            for(ulong id=1;id<4;id++) Assert.That(ledger.Get(id).Assists,Is.Zero);
            ledger.RecordDamage(0,1,4,2,100,true,2,MatchPhase.Finished);
            Assert.That(ledger.Get(0).Deaths,Is.Zero);
        }
        [Test] public void RespawnPreservesStatsButClearsVictimContributionsAndRoundResetClearsAll()
        {
            var ledger=Ledger();
            ledger.RecordDamage(4,1,1,1,10,false,0,MatchPhase.Playing);
            ledger.RecordDeath(4,1,null,1,MatchPhase.Playing);
            Assert.That(ledger.Get(1).Assists,Is.Zero); Assert.That(ledger.Get(4).Deaths,Is.EqualTo(1));
            ledger.BeginLife(4,2,2);
            ledger.RecordDamage(4,2,0,1,100,true,2,MatchPhase.Playing);
            Assert.That(ledger.Get(4).Deaths,Is.EqualTo(2)); Assert.That(ledger.Get(1).Assists,Is.Zero);
            ledger.BeginLife(0,1,2); Assert.That(ledger.Get(0).Kills,Is.EqualTo(1));
            ledger.Reset(10);
            for(ulong id=0;id<5;id++) Assert.That(ledger.Get(id).Equals(new PlayerCombatStats {Round=10}),Is.True);
        }
        [Test] public void DisconnectedContributorIsRemovedAndRejoiningStartsAtZero()
        {
            var ledger=Ledger(); ledger.RecordDamage(4,1,1,1,10,false,0,MatchPhase.Playing);
            ledger.Remove(1); ledger.BeginLife(1,1,2);
            ledger.RecordDamage(4,1,0,1,100,true,1,MatchPhase.Playing);
            Assert.That(ledger.Get(1).Assists,Is.Zero);
            ledger.Remove(0); ledger.BeginLife(0,1,2); Assert.That(ledger.Get(0).Kills,Is.Zero);
        }
        [Test] public void StatsAndAuthoritativeStartTimeRoundTrip()
        {
            var expected=new PlayerCombatStats {Round=19,Kills=6,Deaths=3,Assists=12};
            using var writer=new FastBufferWriter(256,Allocator.Temp); writer.WriteNetworkSerializable(expected);
            var state=new MatchStateSnapshot {Round=19,Phase=MatchPhase.Playing,StartsAt=80.5,EndsAt=260.5};
            writer.WriteNetworkSerializable(state);
            using var reader=new FastBufferReader(writer,Allocator.Temp); reader.ReadNetworkSerializable(out PlayerCombatStats stats);
            reader.ReadNetworkSerializable(out MatchStateSnapshot actual);
            Assert.That(stats.Equals(expected),Is.True); Assert.That(actual.StartsAt,Is.EqualTo(80.5));
            Assert.That(PrototypeApp.StartNoticeActive(actual,80.49),Is.False);
            Assert.That(PrototypeApp.StartNoticeActive(actual,80.5),Is.True);
            Assert.That(PrototypeApp.StartNoticeActive(actual,82.49),Is.True);
            Assert.That(PrototypeApp.StartNoticeActive(actual,82.5),Is.False);
            actual.Phase=MatchPhase.Practice; Assert.That(PrototypeApp.StartNoticeActive(actual,81),Is.False);
        }
    }
}
#endif
