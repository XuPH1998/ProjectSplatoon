#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using SimpleJSON;
using Splatoon.Config;

namespace Splatoon.Tests
{
    // Complete independent Git inputs; expected migration records stay unchanged.
    public static class HistoricalHeroFixture
    {
        const string DirectoryPath = "Tools/ValidationData/HistoricalHeroes/";
        static JSONNode Read(string file, string expectedSha256, int count)
        {
            var path = DirectoryPath + file;
            var bytes = File.ReadAllBytes(path);
            using var hash = SHA256.Create();
            var actual = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (actual != expectedSha256) throw new InvalidOperationException("Historical hero input changed: " + path);
            var rows = JSONNode.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            Assert.That(rows.Count, Is.EqualTo(count), path);
            CollectionAssert.AreEqual(Enumerable.Range(1, count), rows.Children.Select(row => row["id"].AsInt), path);
            return rows;
        }
        // bfb059a: the actual full six-row output immediately after MachineGun was added.
        public static JSONNode MachineGunRows() => Read("MachineGun-After.json",
            "e3dff4205ea299003a66b9529a24788f02c9f2e2d111cfcdb5305cbd81812486", 6);
        // 8659ca2: all hero-owned fields after approved replacement tuning, before recovery tuning.
        static JSONNode WeaponAssetHeroRows() => Read("WeaponAsset-Heroes.json",
            "ffc43ca275229036793e795f8f0673909aa50ee1aa9a1e76c4eab3d544e95e49", 9);

        public static cfg.HeroConfig[] OriginalSixHeroes() => MachineGunRows().Children
            .Select(row => GameplayConfig.GetHero(row["id"].AsInt)).ToArray();

        public static void LoadMachineGunEra(Action<JSONNode> edit = null)
        {
            var rows = MachineGunRows();
            var schema = WeaponAssetHeroRows();
            foreach (var row in rows.Children)
            {
                var compatible = schema.Children.Single(hero => hero["id"].AsInt == row["id"].AsInt);
                // Add only fields the old schema did not have. Never overwrite historical balance.
                foreach (string key in compatible.Keys) if (!row.HasKey(key)) row[key] = compatible[key];
            }
            edit?.Invoke(rows);
            var data = System.IO.Directory.GetFiles("Assets/GameResource/Bootstrap/Config/Luban", "*.json")
                .ToDictionary(Path.GetFileNameWithoutExtension, file => JSONNode.Parse(File.ReadAllText(file)));
            data["tbhero"] = rows;
            // Use the existing test adapter for fields introduced after this historical schema.
            // This loader covers historical movement/display; it does not validate retired weapon assets.
            HeroMigrationTests.InstallWeapons(rows);
            typeof(LubanConfigService).GetProperty("Tables").SetValue(LubanConfigService.Current, new cfg.Tables(name => data[name]));
        }

        public static JSONNode WeaponAssetRows()
        {
            // Read current weapon assets exactly as before. Replace the complete hero-owned input,
            // not expected-oracle values or weapon fields, using the separate pinned Git table.
            var rows = HeroMigrationTests.CombinedHeroes();
            foreach (var hero in WeaponAssetHeroRows().Children)
            {
                var row = rows.Children.Single(current => current["id"].AsInt == hero["id"].AsInt);
                foreach (string key in hero.Keys) row[key] = hero[key];
            }
            return rows;
        }
    }
}
#endif
