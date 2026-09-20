#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Networking;
using Splatoon.Prototype;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class SubWeaponTests
    {
        static readonly (int hero, string path, SubWeaponKind kind, SubWeaponDeploymentMode mode)[] Expected =
        {
            (1, "Assets/GameResource/SubWeapons/Torpedo/TorpedoConfig.asset", SubWeaponKind.Torpedo, SubWeaponDeploymentMode.Immediate),
            (2, "Assets/GameResource/SubWeapons/SpeedPad/SpeedPadConfig.asset", SubWeaponKind.SpeedPad, SubWeaponDeploymentMode.HoldPreviewRelease),
            (3, "Assets/GameResource/SubWeapons/JumpPad/JumpPadConfig.asset", SubWeaponKind.JumpPad, SubWeaponDeploymentMode.HoldPreviewRelease),
            (5, "Assets/GameResource/SubWeapons/CurlingBomb/CurlingBombConfig.asset", SubWeaponKind.CurlingBomb, SubWeaponDeploymentMode.Immediate),
            (6, "Assets/GameResource/SubWeapons/InkCurtain/InkCurtainConfig.asset", SubWeaponKind.InkCurtain, SubWeaponDeploymentMode.HoldPreviewRelease),
            (7, "Assets/GameResource/SubWeapons/InkMine/InkMineConfig.asset", SubWeaponKind.InkMine, SubWeaponDeploymentMode.HoldPreviewRelease),
            (8, "Assets/GameResource/SubWeapons/Sprinkler/SprinklerConfig.asset", SubWeaponKind.Sprinkler, SubWeaponDeploymentMode.HoldPreviewRelease),
        };

        [Test]
        public void HeroTableMapsSevenConfigsAndLeavesTwoHeroesEmpty()
        {
            var rows = JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/tbhero.json")).AsArray;
            foreach (var expected in Expected)
                Assert.That(rows[expected.hero - 1]["subWeaponConfigPath"].Value, Is.EqualTo(expected.path));
            Assert.That(rows[3]["subWeaponConfigPath"].Value, Is.Empty);
            Assert.That(rows[8]["subWeaponConfigPath"].Value, Is.Empty);
        }

        [Test]
        public void SevenAssetsValidateAndUseConfiguredDeploymentModes()
        {
            foreach (var expected in Expected)
            {
                var asset = AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(expected.path);
                Assert.That(asset, Is.Not.Null, expected.path);
                var config = asset.Snapshot();
                Assert.DoesNotThrow(() => SubWeaponConfigValidation.Validate(config));
                Assert.That(config.Kind, Is.EqualTo(expected.kind));
                Assert.That(config.DeploymentMode, Is.EqualTo(expected.mode));
                Assert.That(config.MaxActive, Is.EqualTo(1));
            }
        }

        [Test]
        public void DeploymentRejectsDeadSwimLowInkCooldownAndStaleHero()
        {
            var asset = AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(Expected[0].path);
            var config = asset.Snapshot();
            var state = new PlayerSnapshot { Health = 100, Ink = 100, HeroRevision = 7 };
            var input = new PlayerInputFrame { HeroRevision = 7 };
            Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.True);
            state.Health = 0; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.False);
            state.Health = 100; state.Swimming = true; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.False);
            state.Swimming = false; state.Ink = config.InkCost - .01f; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.False);
            state.Ink = 100; state.SubWeaponCooldownUntil = 10.01; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.False);
            state.SubWeaponCooldownUntil = 0; input.HeroRevision = 6; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.False);
            input.HeroRevision = 7; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Finished, 10), Is.False);
            input.CancelFire = true; Assert.That(SubWeaponRules.CanDeploy(state, input, config, MatchPhase.Playing, 10), Is.False);
        }

        [Test]
        public void ReplacementAndAttachmentRulesMatchPlan()
        {
            Assert.That(SubWeaponRules.ReplacementsAfterSpawn(1, 1), Is.Zero);
            Assert.That(SubWeaponRules.ReplacementsAfterSpawn(2, 1), Is.EqualTo(1));
            Assert.That(SubWeaponRules.Supports(SubWeaponSurfaceMask.Floor, Vector3.up), Is.True);
            Assert.That(SubWeaponRules.Supports(SubWeaponSurfaceMask.Floor, Vector3.right), Is.False);
            Assert.That(SubWeaponRules.Supports(SubWeaponSurfaceMask.All, Vector3.down), Is.True);
            Assert.That(SubWeaponRules.Supports(SubWeaponSurfaceMask.All, Vector3.forward), Is.True);
        }

        [Test]
        public void FriendlyProjectilesIgnoreFriendlyDeployableProxy()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var collider = go.GetComponent<Collider>(); collider.isTrigger = true;
                var proxy = go.AddComponent<SubWeaponHitProxy>(); proxy.Team = 1; proxy.EntityId = 9;
                Assert.That(TpsAimSolver.Valid(collider, 99, 1), Is.False);
                Assert.That(TpsAimSolver.Valid(collider, 99, 2), Is.True);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void InputAndEntityStateRoundTripWithoutLoss()
        {
            var input = new PlayerInputFrame { Sequence = 9, SubWeaponPressSequence = 4, SubWeaponReleaseSequence = 3, SubWeaponHeld = true };
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteNetworkSerializable(input);
                using var reader = new FastBufferReader(writer, Allocator.None); reader.ReadNetworkSerializable(out PlayerInputFrame copy);
                Assert.That(copy.SubWeaponPressSequence, Is.EqualTo(4)); Assert.That(copy.SubWeaponReleaseSequence, Is.EqualTo(3)); Assert.That(copy.SubWeaponHeld, Is.True);
            }
            var state = new SubWeaponEntityState { Id = 12, Round = 2, OwnerId = 44, Team = 2, Kind = SubWeaponKind.InkCurtain,
                Phase = SubWeaponEntityPhase.Active, Position = new Vector3(1, 2, 3), Scale = new Vector3(4, 2.5f, .12f), Rotation = Quaternion.Euler(0, 30, 0), Health = 200 };
            using var stateWriter = new FastBufferWriter(1024, Allocator.Temp); stateWriter.WriteNetworkSerializable(state);
            using var stateReader = new FastBufferReader(stateWriter, Allocator.None); stateReader.ReadNetworkSerializable(out SubWeaponEntityState restored);
            Assert.That(restored.Id, Is.EqualTo(state.Id)); Assert.That(restored.Kind, Is.EqualTo(state.Kind));
            Assert.That(restored.Position, Is.EqualTo(state.Position)); Assert.That(restored.Scale, Is.EqualTo(state.Scale)); Assert.That(restored.Health, Is.EqualTo(200));
        }

        [Test]
        public void SubWeaponBatchesHaveARegisteredTrafficCategory()
        {
            var states = new[] { new SubWeaponEntityState { Id = 1 } };
            var events = new[] { new SubWeaponEvent { Id = 1, Type = SubWeaponEventType.Spawned } };
            Assert.DoesNotThrow(() =>
            {
                using var stateWriter = new FastBufferWriter(1024, Allocator.Temp);
                stateWriter.WriteNetworkSerializable(new NetworkBatch<SubWeaponEntityState>(states));
                using var eventWriter = new FastBufferWriter(1024, Allocator.Temp);
                eventWriter.WriteNetworkSerializable(new NetworkBatch<SubWeaponEvent>(events));
            });
        }
    }
}
#endif
