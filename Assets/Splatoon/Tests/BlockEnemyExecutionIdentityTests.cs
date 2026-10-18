#if UNITY_EDITOR
using System.Reflection;
using System.IO;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class BlockEnemyExecutionIdentityTests
    {
        cfg.Tables _previousTables;
        [OneTimeSetUp] public void LoadTokenConfiguration()
        {
            _previousTables = LubanConfigService.Current.Tables;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText(
                Path.Combine(UnityEngine.Application.dataPath, "GameResource/Bootstrap/Config/Luban", name + ".json"))));
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, tables);
        }
        [OneTimeTearDown] public void RestoreTokenConfiguration()
            => typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, _previousTables);
        static bool Matches(string method, BlockDynamicAcceptanceProbe.EnemyExecutionSample sample, InkShot shot, int generation)
            => (bool)typeof(BlockDynamicAcceptanceProbe).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { sample, shot, generation });
        static BlockAttackToken Token() => new(new PaintCredit(0, 1, 1, 4, PaintAttackKind.Main, true), 1, 10, 10, 10);
        static BlockDynamicAcceptanceProbe.EnemyExecutionSample Sample(BlockAttackToken token) => new() {
            plan = new BlockDynamicAcceptanceProbe.EnemyPlanIdentity { round = 1, owner = 0, connectionGeneration = 2 },
            life = 7, heroRevision = 4, action = 13, shotSequence = 8, entity = 99, pellet = 2, token = token
        };
        static InkShot Shot(BlockAttackToken token) => new() {
            Round = 1, Shooter = 0, Lifecycle = 7, HeroRevision = 4, ActionId = 13,
            ShotSequence = 8, Id = 99, PelletIndex = 2, BlockAttack = token
        };
        [Test] public void ExactAuthorityEmissionAndProjectileIdentityRemainAssociated()
        {
            var token = Token(); var sample = Sample(token); var shot = Shot(token);
            Assert.That(Matches("EnemyEmissionMatches", sample, shot, 2), Is.True);
            Assert.That(Matches("EnemyProjectileMatches", sample, shot, 2), Is.True);
        }
        [TestCase("round")][TestCase("owner")][TestCase("life")][TestCase("hero")]
        [TestCase("action")][TestCase("shot")][TestCase("generation")]
        public void ReusedCountersCannotBorrowAnEarlierEmission(string changed)
        {
            var token = Token(); var sample = Sample(token); var shot = Shot(token); int generation = 2;
            switch (changed)
            {
                case "round": shot.Round++; break; case "owner": shot.Shooter++; break;
                case "life": shot.Lifecycle++; break; case "hero": shot.HeroRevision++; break;
                case "action": shot.ActionId++; break; case "shot": shot.ShotSequence++; break;
                case "generation": generation++; break;
            }
            Assert.That(Matches("EnemyEmissionMatches", sample, shot, generation), Is.False);
            Assert.That(Matches("EnemyProjectileMatches", sample, shot, generation), Is.False);
        }
        [TestCase("entity")][TestCase("pellet")][TestCase("token")]
        public void ActualContactRequiresTheSamePelletEntityAndPaidToken(string changed)
        {
            var token = Token(); var sample = Sample(token); var shot = Shot(token);
            if (changed == "entity") shot.Id++; else if (changed == "pellet") shot.PelletIndex++; else shot.BlockAttack = Token();
            Assert.That(Matches("EnemyProjectileMatches", sample, shot, 2), Is.False);
        }
    }
}
#endif
