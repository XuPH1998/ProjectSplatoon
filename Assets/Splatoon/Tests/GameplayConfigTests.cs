#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Config;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class GameplayConfigTests
    {
        private static cfg.Tables Tables(Action<Dictionary<string, JSONNode>> edit = null)
        {
            var data = new Dictionary<string, JSONNode>();
            foreach (string file in Directory.GetFiles(Path.Combine(Application.dataPath, "GameResource/Bootstrap/Config/Luban"), "*.json"))
                data[Path.GetFileNameWithoutExtension(file)] = JSONNode.Parse(File.ReadAllText(file));
            edit?.Invoke(data); return new cfg.Tables(name => data[name]);
        }
        [Test] public void GeneratedDefaultsResolveFourTablesAndBallisticValues()
        {
            var t = Tables(); GameplayConfig.Validate(t);
            Assert.That(t.TbHero.Get(1).MaxHealth, Is.EqualTo(100));
            Assert.That(t.TbHero.Get(1).FireRate, Is.EqualTo(15));
            Assert.That(t.TbHero.Get(1).Damage, Is.EqualTo(36));
            Assert.That(t.TbHero.Get(1).CharacterPrefabAddress, Is.EqualTo("Character/RifleGirl"));
            Assert.That(t.TbHero.Get(1).WeaponPrefabAddress, Is.EqualTo("Weapon/RifleGirlRifle"));
            Assert.That(t.TbMap.Get(1).CellSize, Is.EqualTo(.125f));
            Assert.That(t.TbGlobal.Get(1).ProjectileStepRate, Is.EqualTo(120));
            Assert.That(t.TbGlobal.Get(1).AimCorrectionDistance, Is.EqualTo(6));
        }
        [Test] public void InvalidCrossTableReferenceIsRejected()
        {
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbroommode"][0]["heroId"] = 999)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbroommode"][0]["mapId"] = 999)));
        }
        [Test] public void ImpossibleProjectileAndNetworkSettingsAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbhero"][0]["lifetime"] = 0)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbglobal"][0]["projectileStepRate"] = 121)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbhero"][0]["speedMax"] = 10)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbglobal"][0]["aimCorrectionDistance"] = 0)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbglobal"][0]["aimCorrectionDistance"] = -1)));
        }
    }
}
#endif
