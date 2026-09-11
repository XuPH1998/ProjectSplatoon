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
        [Test] public void GeneratedDefaultsResolveFiveTablesAndBallisticValues()
        {
            var t = Tables(); GameplayConfig.Validate(t);
            Assert.That(t.TbCharacter.Get(1).MaxHealth, Is.EqualTo(100));
            Assert.That(t.TbWeapon.Get(1).FireRate * t.TbWeapon.Get(1).Damage, Is.EqualTo(150));
            Assert.That(t.TbArena.Get(1).Size / t.TbArena.Get(1).CellSize, Is.EqualTo(256));
            Assert.That(t.TbGlobal.Get(1).ProjectileStepRate, Is.EqualTo(120));
        }
        [Test] public void InvalidCrossTableReferenceIsRejected()
        { Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbroommode"][0]["weaponId"] = 999))); }
        [Test] public void ImpossibleProjectileAndNetworkSettingsAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbweapon"][0]["lifetime"] = 0)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbglobal"][0]["projectileStepRate"] = 121)));
            Assert.Throws<InvalidOperationException>(() => GameplayConfig.Validate(Tables(d => d["tbweapon"][0]["speedMax"] = 10)));
        }
    }
}
#endif
