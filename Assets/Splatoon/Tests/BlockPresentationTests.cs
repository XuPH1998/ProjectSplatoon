#if UNITY_EDITOR
using System.Linq;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using Splatoon.Blocks;
using Splatoon.Combat;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    public sealed class BlockPresentationTests
    {
        GameObject root;
        BlockFlightPresentation flight;
        WeaponRuntimeConfig rifle, bubble, explosher;
        [SetUp] public void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); HeroMigrationTests.Load();
            SpecialWeaponConfigService.Current.Clear();
            foreach (var row in LubanConfigService.Current.Tables.TbSpecialWeapon.DataList)
                SpecialWeaponConfigService.Current.Set(row.Id, AssetDatabase.LoadAssetAtPath<SpecialWeaponConfigAsset>(row.ConfigPath));
            root = new GameObject("Block projectile acceptance"); root.AddComponent<BlockTerrain>().Initialize(null);
            rifle = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/RifleGirl/RifleGirlWeaponConfig.asset").Snapshot();
            bubble = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>("Assets/GameResource/Weapons/BubbleGirl/BubbleGirlWeaponConfig.asset").Snapshot();
            explosher = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>(GameplayConfig.GetHero(3).WeaponConfigPath).Snapshot();
            Assert.That(WeaponSimulation.IsExplosher(explosher), Is.True, "Use the live Explosher profile rather than the Blaster profile");
            flight = new BlockFlightPresentation();
        }
        [TearDown] public void Cleanup()
        {
            flight?.Dispose(); Object.DestroyImmediate(root); SpecialWeaponConfigService.Current.Clear(); SubWeaponConfigService.Current.Clear(); LubanConfigService.Current.Reset();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        InkShot Shot(uint id = 1, WeaponRuntimeConfig weapon = null) => new() { Id = id, Round = 1, Team = 1, Born = 10, Seed = id + 17,
            Configuration = weapon ?? rifle, Origin = new Vector3(0, 2, 0), Velocity = Vector3.forward * 32 };
        [Test] public void ProjectileResourceRetainsInstancingForPackagedRendering()
        {
            var template = Resources.Load<Material>("BlockProjectileInstanced");
            Assert.That(template, Is.Not.Null, "The build needs a serialized instancing material");
            Assert.That(AssetDatabase.GetAssetPath(template), Is.EqualTo("Assets/Splatoon/Runtime/Blocks/Resources/BlockProjectileInstanced.mat"));
            Assert.That(template.enableInstancing, Is.True);
            Assert.That(template.shader.name, Is.EqualTo("Splatoon/BlockProjectile"));
        }
        [Test] public void CubesFollowTheAuthorityTrajectoryWithoutFluidObjectsOrColliders()
        {
            var shot = Shot(); shot.FirstSegmentLength = 2; shot.PostCorrectionVelocity = new Vector3(.3f, 0, 1).normalized * 32; shot.GravityStartAge = .04f;
            Assert.That(flight.Spawn(shot, 10), Is.True);
            foreach (double age in new[] { .1, .4, .7 })
            {
                Assert.That(flight.TryPosition(1, 1, 10 + age, out var position), Is.True);
                Assert.That(Vector3.Distance(position, InkBallistics.Position(shot, rifle, age)), Is.LessThan(.0001));
                flight.Update(10 + age, null); Assert.That(flight.CubeCount, Is.EqualTo(1));
            }
            Assert.That(flight.CubeMesh.vertexCount, Is.EqualTo(24)); Assert.That(flight.CubeMesh.triangles.Length, Is.EqualTo(36));
            Assert.That(root.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<Collider>(true).All(c => !c.enabled), Is.True, "Only the terrain's disabled falling-hit proxy may exist");
        }
        [Test] public void CompletionClearRoundAndRestorationCannotResurrectOldCubes()
        {
            flight.Complete(new InkImpact { Id = 1, Round = 1 }); Assert.That(flight.Spawn(Shot(), 10), Is.False);
            Assert.That(flight.Spawn(Shot(2), 10), Is.True); Assert.That(flight.Spawn(Shot(2), 10), Is.False);
            flight.Complete(new InkImpact { Id = 2, Round = 1, ContinuesProjectile = true }); Assert.That(flight.ActiveShots, Is.EqualTo(1));
            flight.Complete(new InkImpact { Id = 2, Round = 1 }); Assert.That(flight.ActiveShots, Is.Zero);
            flight.Clear(10.2); Assert.That(flight.Spawn(Shot(3), 10.3), Is.False); Assert.That(flight.Spawn(Shot(3), 10.3, true), Is.True);
            var next = Shot(4); next.Round = 2; next.Born = 10.3; Assert.That(flight.Spawn(next, 10.3), Is.True);
            Assert.That(flight.Spawn(Shot(5), 10.3), Is.False); Assert.That(flight.ActiveShots, Is.EqualTo(1));
        }
        [Test] public void EarlyAndOutOfOrderBounceEventsKeepTheSameAuthoritativeSegments()
        {
            var shot = Shot(1, bubble);
            var later = new InkBounce { Id = 1, Round = 1, Sequence = 2, Time = 10.2, Position = new Vector3(2, 1, 2), Velocity = Vector3.left * 8, Normal = Vector3.right };
            flight.Bounce(later); Assert.That(flight.Spawn(shot, 10), Is.True);
            var earlier = later; earlier.Sequence = 1; earlier.Time = 10.1; earlier.Position = Vector3.up; earlier.Velocity = Vector3.forward * 12;
            flight.Bounce(earlier); flight.Bounce(later);
            foreach (double time in new[] { 10.15, 10.25 })
            {
                Assert.That(flight.TryPosition(1, 1, time, out var actual), Is.True);
                Assert.That(Vector3.Distance(actual, (time < later.Time ? earlier : later).PositionAt(time, shot)), Is.LessThan(.0001));
            }
        }
        [Test] public void FlightAndExplosionDebrisHaveHardCapsAndReleaseTheirResources()
        {
            for (uint i = 0; i < BlockFlightPresentation.ShotLimit + 25; i++) flight.Spawn(Shot(i + 1), 10);
            Assert.That(flight.ActiveShots, Is.EqualTo(BlockFlightPresentation.ShotLimit)); Assert.That(flight.Dropped, Is.EqualTo(25));
            for (uint i = 0; i < 80; i++) flight.Explosion(new InkExplosionEvent { ShotId = 10000 + i, Round = 1, Team = 1, Time = 10, Seed = i + 1, Position = Vector3.up }, 4);
            Assert.That(flight.DebrisCount, Is.EqualTo(BlockFlightPresentation.DebrisLimit));
            flight.Update(10.05, null); Assert.That(flight.CubeCount, Is.EqualTo(BlockFlightPresentation.ShotLimit + BlockFlightPresentation.DebrisLimit));
            flight.Update(11, null); Assert.That(flight.DebrisCount, Is.Zero);
            flight.Clear(); Assert.That(flight.ActiveShots + flight.CubeCount + flight.DebrisCount, Is.Zero);
            var mesh = flight.CubeMesh; flight.Dispose(); Assert.That(mesh == null, Is.True); flight.Dispose();
        }
        [Test] public void DefaultPresentationRoutesEveryWeaponToCubesAndSkipsLegacyPools()
        {
            var presentation = root.AddComponent<InkPresentation>();
            Assert.That(presentation.BlockMode, Is.True); Assert.That(presentation.TryPrepareAmmo(rifle.Ammo), Is.True);
            presentation.Spawn(Shot(1)); presentation.Spawn(Shot(2, bubble)); presentation.Spawn(Shot(3, explosher)); presentation.UpdateFlights(10.1, null);
            Assert.That(presentation.ActiveShots, Is.EqualTo(3));
            Assert.That(presentation.BlockCubeCount, Is.EqualTo(2 + BlockFlightPresentation.CubesPerShot(explosher)));
            Assert.That(presentation.ParticleCount + presentation.ActiveGroups + presentation.VersionPoolCount, Is.Zero);
            Assert.That(presentation.Streams.Any(), Is.False);
            Assert.That(presentation.CreateMuzzle(root.transform, rifle), Is.Null);
            Assert.That(presentation.HasCompositeContent(1 << InkFlightPresentation.Layer), Is.False); Assert.That(presentation.HasCompositeContent(1 << 9), Is.False);
            presentation.Impact(new InkImpact { Id = 1, Round = 1, Hit = true, Team = 1, Time = 10.1, Position = Vector3.up, Normal = Vector3.up });
            presentation.UpdateFlights(10.11, null); Assert.That(presentation.BlockDebrisCount, Is.EqualTo(8));
            Assert.That(root.GetComponentsInChildren<ParticleSystem>(true), Is.Empty); Assert.That(root.GetComponentsInChildren<InkImpactEffect>(true), Is.Empty);
            presentation.Clear(); Assert.That(presentation.BlockCubeCount + presentation.BlockDebrisCount, Is.Zero);
        }
        [TestCase("RocketLauncherGirl", .3f, .3f, .42f, 1)]
        [TestCase("ShotgunGirl", 1.05f, 1.05f, 1.05f, 4)]
        [TestCase("BubbleShotgunGirl", 1.2f, 1.2f, 1.2f, 1)]
        [TestCase("BubbleGirl", .45f, .45f, .45f, 1)]
        [TestCase("RifleGirl", .09f, .09f, .09f, 1)]
        public void RenderedWholeProjectileMatchesConfiguredSizeAndOtherWeaponsKeepDefaults(string weapon, float x, float y, float z, int count)
        {
            var config = AssetDatabase.LoadAssetAtPath<WeaponConfigAsset>($"Assets/GameResource/Weapons/{weapon}/{weapon}WeaponConfig.asset").Snapshot();
            var presentation = root.AddComponent<InkPresentation>(); var shot = Shot(1, config);
            presentation.Spawn(shot);
            double time = shot.Born + (WeaponSimulation.UsesBubbleMesh(config) ? BubbleFlightPresentation.RenderDelay : 0);
            presentation.UpdateFlights(time, null);
            var instances = BlockDefaultContractFixture.RenderedInstances(presentation, 1, count);
            // Inspect actual submitted vertices in the trajectory frame, rather than the sizing helper.
            var frame = Matrix4x4.TRS(shot.Origin, instances[0].rotation, Vector3.one).inverse;
            var bounds = new Bounds(); bool first = true;
            foreach (var instance in instances) for (int corner = 0; corner < 8; corner++)
            {
                var local = new Vector3((corner & 1) == 0 ? -.5f : .5f, (corner & 2) == 0 ? -.5f : .5f, (corner & 4) == 0 ? -.5f : .5f);
                var point = frame.MultiplyPoint3x4(instance.MultiplyPoint3x4(local));
                if (first) { bounds = new Bounds(point, Vector3.zero); first = false; } else bounds.Encapsulate(point);
            }
            Assert.That(bounds.size.x, Is.EqualTo(x).Within(.0001));
            Assert.That(bounds.size.y, Is.EqualTo(y).Within(.0001));
            Assert.That(bounds.size.z, Is.EqualTo(z).Within(.0001));
            Assert.That(presentation.VersionPoolCount + presentation.ParticleCount, Is.Zero);
            Assert.That(root.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);
        }
        [TestCase(3)]
        [TestCase(5)]
        [TestCase(9)]
        public void ExplosiveMainWeaponsKeepOriginalPooledBurstsAlongsideCubes(int hero)
        {
            var presentation = root.AddComponent<InkPresentation>();
            var match = root.AddComponent<PrototypeMatch>(); match.enabled = false;
            var current = typeof(PrototypeMatch).GetField("<Current>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            var oldMatch = current.GetValue(null); var config = GameplayConfig.GetWeapon(hero); int previousInstance = 0; uint id = 100;
            try
            {
                foreach (bool collision in new[] { false, true }) foreach (byte team in new byte[] { 1, 2 })
                {
                    presentation.Clear();
                    var explosion = new InkExplosionEvent { ShotId = ++id, HeroId = hero, ConfigurationRevision = WeaponConfigService.Current.Revision(hero),
                        Round = match.State.Value.Round, Team = team, Position = Vector3.up * 2, Normal = Vector3.up, Seed = id, Time = 10, Collision = collision };
                    current.SetValue(null, match);
                    try { presentation.Explosion(explosion); presentation.Explosion(explosion); }
                    finally { current.SetValue(null, oldMatch); }
                    if (config.Ammo.BubblePrefab != null)
                    {
                        presentation.UpdateFlights(10 + BubbleFlightPresentation.RenderDelay - .001, null);
                        Assert.That(presentation.transform.Cast<Transform>().Any(t => t.name == "Ink explosion" && t.gameObject.activeSelf), Is.False);
                    }
                    presentation.UpdateFlights(10.001 + (config.Ammo.BubblePrefab != null ? BubbleFlightPresentation.RenderDelay : 0), null);
                    var burst = presentation.transform.Cast<Transform>().Single(t => t.name == "Ink explosion" && t.gameObject.activeSelf);
                    if (previousInstance != 0) Assert.That(burst.GetInstanceID(), Is.EqualTo(previousInstance), "Clear reuses the original burst across colors and blast radii");
                    previousInstance = burst.GetInstanceID();
                    Assert.That(burst.localScale.x, Is.EqualTo(InkExplosionRules.Radius(config.Ammo, collision)).Within(.0001));
                    var systems = burst.GetComponentsInChildren<ParticleSystem>();
                    Assert.That(systems.Length, Is.EqualTo(config.Ammo.ExplosionPrefab.GetComponentsInChildren<ParticleSystem>(true).Length));
                    Assert.That(systems.All(ps => ps.main.startColor.color == PrototypeArena.TeamColor(team)), Is.True);
                    Assert.That(systems.Select(ps => ps.randomSeed).Distinct().Count(), Is.EqualTo(systems.Length));
                    Assert.That(presentation.BlockDebrisCount, Is.EqualTo(Mathf.Clamp(Mathf.CeilToInt(InkExplosionRules.Radius(config.Ammo, collision) * 18), 16, 64)), "Duplicate event cannot emit another fragment burst");
                    Assert.That(presentation.VersionPoolCount, Is.Zero); Assert.That(presentation.Streams, Is.Empty);
                    presentation.Clear(); Assert.That(burst.gameObject.activeSelf, Is.False);
                }
            }
            finally { current.SetValue(null, oldMatch); }
        }
        [Test] public void ExplosiveSubEffectsRestoreOriginalImpactAndRecycleWithoutRestoringOtherEffectKinds()
        {
            var ink = root.AddComponent<InkPresentation>();
            typeof(InkPresentation).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ink, null);
            var presentation = root.AddComponent<SubWeaponPresentation>();
            var asset = AssetDatabase.LoadAssetAtPath<SubWeaponConfigAsset>(SubWeaponDefaults.Path(SubWeaponType.BurstBomb));
            int id = SubWeaponConfigService.Id(asset.type); SubWeaponConfigService.Current.SetById(id, asset);
            var e = new SubEffectEvent { Sequence = 1, SubWeaponId = id, ConfigRevision = SubWeaponConfigService.Current.RevisionById(id),
                Kind = SubEffectKind.Explosion, Team = 1, At = 10, Position = Vector3.up, Normal = Vector3.up, Radius = 2 };
            presentation.Effect(e); presentation.Effect(e); presentation.PresentAt(10.2, 1f / 60);
            var particles = presentation.GetComponentsInChildren<ParticleSystem>();
            Assert.That(particles.Length, Is.EqualTo(asset.visuals.impactPrefab.GetComponentsInChildren<ParticleSystem>(true).Length));
            Assert.That(particles.Length, Is.GreaterThan(0));
            Assert.That(particles.All(ps => ps.main.startColor.color == PrototypeArena.TeamColor(1)), Is.True);
            var original = particles[0].gameObject;
            presentation.PresentAt(12, 1f / 60); Assert.That(original.activeInHierarchy, Is.False);
            e.Sequence++; e.Team = 2; e.At = 12;
            presentation.Effect(e); presentation.PresentAt(12.2, 1f / 60);
            Assert.That(original.activeInHierarchy, Is.True, "Sub explosions reuse the existing pool");
            Assert.That(presentation.GetComponentsInChildren<ParticleSystem>().All(ps => ps.main.startColor.color == PrototypeArena.TeamColor(2)), Is.True);
            presentation.PresentAt(14, 1f / 60);
            e.Sequence++; e.Kind = SubEffectKind.Hit; e.At = 14;
            presentation.Effect(e); presentation.PresentAt(14.2, 1f / 60);
            Assert.That(presentation.GetComponentsInChildren<ParticleSystem>(), Is.Empty, "Non-explosion events keep the existing block presentation");
            presentation.Clear(); Assert.That(presentation.GetComponentsInChildren<ParticleSystem>(), Is.Empty);
        }
        [Test] public void RestoreUsesCubesAndNeverReplaysOldMuzzleFeedback()
        {
            var presentation = root.AddComponent<InkPresentation>(); presentation.ClearFlights(10.1);
            var bubbleShot = Shot(1, bubble); var segment = InkBounce.Initial(bubbleShot); segment.Sequence = 1; segment.Time = 10.2; segment.Position = Vector3.up; segment.Velocity = Vector3.left * 4;
            presentation.RestoreBubble(new InkBubbleState { Shot = bubbleShot, Segment = segment }); presentation.RestoreExplosher(Shot(2, explosher), 10.2);
            presentation.UpdateFlights(10.21, null);
            Assert.That(presentation.BubbleRestoredCount, Is.EqualTo(1)); Assert.That(presentation.ExplosherRestoredCount, Is.EqualTo(1)); Assert.That(presentation.ActiveShots, Is.EqualTo(2));
            Assert.That(presentation.VersionPoolCount, Is.Zero); Assert.That(root.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);
        }
        static object SpecialView(SpecialWeaponPresentation presentation, uint id) =>
            ((IDictionary)typeof(SpecialWeaponPresentation).GetField("_views", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(presentation))[id];
        static SpecialEntityState Special(uint id, SpecialWeaponType type) => new() { Id = id, Version = 1, Type = type, Team = 1, Phase = SpecialEntityPhase.Active,
            LiveLobes = 7, Born = 10, Changed = 10, SampledAt = 10, Expires = 20, Position = Vector3.up * 3, Velocity = Vector3.forward * 3,
            P0 = new Vector3(1, 3, 0), P1 = new Vector3(2, 3, 0), P2 = new Vector3(3, 3, 0), Health = 480 };
        [Test] public void FiveSpecialPresentationsUseSolidCubesAndReplaceFluidRainAndTrails()
        {
            var presentation = root.AddComponent<SpecialWeaponPresentation>(); presentation.Match = root.AddComponent<PrototypeMatch>(); presentation.Match.enabled = false;
            for (uint id = 1; id <= 5; id++)
            {
                var state = Special(id, (SpecialWeaponType)id); presentation.Lifecycle(new SpecialLifecycleEvent { Sequence = id, State = state });
                typeof(SpecialWeaponPresentation).GetMethod("Present", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(presentation, new[] { SpecialView(presentation, id), (object)10.3 });
            }
            Assert.That(presentation.Count, Is.EqualTo(5));
            var specialRoots = Enumerable.Range(1, 5).Select(id => presentation.transform.Find("Special_" + (SpecialWeaponType)id + "_" + id)).ToArray();
            Assert.That(specialRoots.All(value => value != null), Is.True);
            var renderers = specialRoots.SelectMany(value => value.GetComponentsInChildren<MeshRenderer>(true)).ToArray(); Assert.That(renderers.Length, Is.GreaterThan(40));
            Assert.That(renderers.All(r => r.sharedMaterial.shader.name == "Splatoon/BlockProjectile"), Is.True,
                "Unexpected special materials: " + string.Join(", ", renderers.Where(r => r.sharedMaterial.shader.name != "Splatoon/BlockProjectile").Select(r => r.name + " [" + r.sharedMaterial.shader.name + "]")));
            var meshes = specialRoots.SelectMany(value => value.GetComponentsInChildren<MeshFilter>(true)).ToArray();
            Assert.That(meshes.All(m => m.sharedMesh.vertexCount == 24), Is.True,
                "Unexpected special meshes: " + string.Join(", ", meshes.Where(m => m.sharedMesh.vertexCount != 24).Select(m => m.name + " [" + m.sharedMesh.vertexCount + " vertices]")));
            Assert.That(presentation.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);
            var trizooka = presentation.transform.Find("Special_Trizooka_1");
            Assert.That(trizooka.GetComponentsInChildren<LineRenderer>(true).All(l => l.positionCount == 0), Is.True, "No fluid tail behind the three blocks");
            var storm = presentation.transform.Find("Special_InkStorm_4");
            Assert.That(storm.GetComponentsInChildren<LineRenderer>(true).Count(l => l.positionCount > 0), Is.EqualTo(1), "Only the area warning remains; rain is cubes");
            presentation.Clear(); Assert.That(specialRoots.All(value => value == null), Is.True);
        }
        [Test] public void AuthorityLobeTerminationEmitsOncePerContactAndCannotReplayDebris()
        {
            var ink = root.AddComponent<InkPresentation>(); var special = root.AddComponent<SpecialWeaponPresentation>();
            // EditMode does not run MonoBehaviour.Awake; the real match invokes it before consuming events.
            typeof(InkPresentation).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ink, null);
            Assert.That(InkPresentation.Current, Is.SameAs(ink));
            var state = Special(1, SpecialWeaponType.Trizooka); special.Lifecycle(new SpecialLifecycleEvent { Sequence = 1, State = state });
            state.Version = 2; state.SampledAt = 10.1; state.LiveLobes = 6;
            special.Lifecycle(new SpecialLifecycleEvent { Sequence = 2, State = state }); ink.UpdateFlights(10.11, null); int first = ink.BlockDebrisCount;
            Assert.That(first, Is.InRange(16, 64));
            special.Lifecycle(new SpecialLifecycleEvent { Sequence = 2, State = state }); Assert.That(ink.BlockDebrisCount, Is.EqualTo(first));
            state.Version = 3; state.SampledAt = 10.12; state.LiveLobes = 0; state.Phase = SpecialEntityPhase.Removed;
            special.Lifecycle(new SpecialLifecycleEvent { Sequence = 3, State = state }); Assert.That(ink.BlockDebrisCount, Is.EqualTo(first * 3));
            special.Lifecycle(new SpecialLifecycleEvent { Sequence = 3, State = state }); Assert.That(ink.BlockDebrisCount, Is.EqualTo(first * 3));
            Assert.That(root.GetComponent<BlockTerrain>().PinkCount + root.GetComponent<BlockTerrain>().BlueCount, Is.Zero, "Skill effects cannot mutate terrain");
        }
    }
}
#endif
