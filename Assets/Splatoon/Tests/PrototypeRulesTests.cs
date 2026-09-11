#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using Splatoon.Prototype;
using Splatoon.Networking;

namespace Splatoon.Tests
{
    public sealed class PrototypeRulesTests
    {
        [Test] public void PaintOverwriteAndClearPreserveDenominator()
        {
            var grid=new PaintGrid(8,.5f,p=>p.x>1);
            int total=grid.Total;Assert.That(total,Is.EqualTo(48));
            grid.Paint(Vector3.zero,1,1,null);int orange=grid.Orange;Assert.That(orange,Is.GreaterThan(0));
            grid.Paint(Vector3.zero,1,1,null);Assert.That(grid.Orange,Is.EqualTo(orange));
            grid.Paint(Vector3.zero,1,2,null);Assert.That(grid.Orange,Is.Zero);Assert.That(grid.Blue,Is.EqualTo(orange));
            grid.Clear(null);Assert.That(grid.Orange+grid.Blue,Is.Zero);Assert.That(grid.Total,Is.EqualTo(total));
        }
        [Test] public void LateJoinStateReconstructsIdenticalPaintAndScore()
        {
            var host=new PaintGrid(64,.5f);var client=new PaintGrid(64,.5f);
            host.Paint(new Vector3(3,0,2),1.2f,1,null);host.Paint(new Vector3(3.5f,0,2),1.2f,2,null);
            for(int i=0;i<host.Cells.Length;i++)client.Set(i,host.Cells[i]);
            Assert.That(client.Hash(),Is.EqualTo(host.Hash()));Assert.That(client.Orange,Is.EqualTo(host.Orange));Assert.That(client.Blue,Is.EqualTo(host.Blue));
        }
        [Test] public void PaintBoundsAndBlockedCellsAreExcluded()
        {
            var grid=new PaintGrid(4,.5f,p=>p.x<0);
            Assert.That(grid.At(new Vector3(-10,0,0)),Is.EqualTo(255));
            grid.Paint(Vector3.zero,10,1,null);Assert.That(grid.Orange,Is.EqualTo(8));Assert.That(grid.Total,Is.EqualTo(8));
        }
        [Test] public void InkCannotGoNegativeAndRecoveryCapsAtCapacity()
        {
            float ink=3;Assert.That(PrototypeRules.Spend(ref ink,2),Is.True);Assert.That(PrototypeRules.Spend(ref ink,2),Is.False);Assert.That(ink,Is.EqualTo(1));
            Assert.That(PrototypeRules.Recover(90,100,35,1),Is.EqualTo(100));
        }
        [Test] public void DamageRespectsTeamsProtectionAndDeath()
        {
            Assert.That(PrototypeRules.Damage(100,25,true,0,3),Is.EqualTo(100));
            Assert.That(PrototypeRules.Damage(100,25,false,4,3),Is.EqualTo(100));
            Assert.That(PrototypeRules.Damage(100,25,false,4,4),Is.EqualTo(75));
            Assert.That(PrototypeRules.Damage(10,25,false,0,3),Is.Zero);
            Assert.That(PrototypeRules.CanRespawn(0,5.9,6),Is.False);Assert.That(PrototypeRules.CanRespawn(0,6,6),Is.True);
        }
        [Test] public void TeamsBalanceAndRoundTransitionsRequirePlayersAndDeadline()
        {
            Assert.That(PrototypeRules.ChooseTeam(0,0),Is.EqualTo(1));Assert.That(PrototypeRules.ChooseTeam(2,1),Is.EqualTo(2));
            Assert.That(PrototypeRules.CanStart(1,MatchPhase.Practice),Is.False);Assert.That(PrototypeRules.CanStart(2,MatchPhase.Finished),Is.True);
            Assert.That(PrototypeRules.CanStart(2,MatchPhase.Playing),Is.False);
            Assert.That(PrototypeRules.HasEnded(MatchPhase.Playing,179.99,180),Is.False);Assert.That(PrototypeRules.HasEnded(MatchPhase.Playing,180,180),Is.True);
            Assert.That(PrototypeRules.Winner(50,50),Is.Zero);Assert.That(PrototypeRules.Winner(51,50),Is.EqualTo(1));
        }
    }
}
#endif
