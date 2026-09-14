#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Splatoon.Combat;

namespace Splatoon.Tests
{
    public sealed class InkImpactTests
    {
        const string Prefab = "Assets/GameResource/Effects/Ink/Prefabs/InkImpact.prefab";
        readonly List<GameObject> _objects = new();
        InkImpactEffect Create()
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab));
            _objects.Add(go); return go.GetComponent<InkImpactEffect>();
        }
        [TearDown] public void Cleanup()
        { foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go); _objects.Clear(); }

        static ParticleSystem.Particle[] Particles(ParticleSystem system)
        { var all = new ParticleSystem.Particle[32]; int count = system.GetParticles(all); return all.Take(count).ToArray(); }
        static string Fingerprint(InkImpactEffect effect)
        {
            var data = new List<Vector4>(); effect.Splash.GetCustomParticleData(data, ParticleSystemCustomData.Custom1);
            return string.Join("|", new[] { effect.Splash, effect.Streaks, effect.Droplets }.SelectMany(Particles).Select(p =>
                $"{p.position:F6};{p.velocity:F6};{p.rotation3D:F6};{p.startSize3D:F6};{p.startLifetime:F6};{p.startColor};{p.randomSeed}")) +
                string.Join(";", data.Select(d => d.ToString("F0")));
        }

        [Test] public void RecycledInstancesReproduceParticlesAndAtlasWithoutGlobalRandom()
        {
            var first = Create(); var second = Create();
            var impact = new InkImpact { Id = 913, Round = 4, PelletIndex = 3 };
            var savedRandom = Random.state;
            first.Play(Color.magenta, InkImpactEffect.Seed(impact)); string expected = Fingerprint(first);
            Assert.That(Random.state, Is.EqualTo(savedRandom), "Cosmetic randomness must not consume the gameplay/global RNG");
            for (uint i = 1; i < 22; i++) second.Play(Color.cyan, InkImpactEffect.Seed(new InkImpact { Id = i, Round = 2 }));
            second.Play(Color.magenta, InkImpactEffect.Seed(impact));
            Assert.That(Fingerprint(second), Is.EqualTo(expected), "Pool history must not change replayed emissions or leave the previous team color");
        }

        [Test] public void TwentyImpactsVarySilhouetteAndScatterWithinBoundedOutwardHemisphere()
        {
            var effect = Create(); var unique = new HashSet<string>(); var atlasTiles = new HashSet<int>();
            foreach (var normal in new[] { Vector3.forward, Vector3.up, new Vector3(0, 1, 1).normalized })
            {
                effect.transform.rotation = Quaternion.LookRotation(normal);
                for (uint i = 1; i <= 20; i++)
                {
                    effect.Play(Color.magenta, InkImpactEffect.Seed(new InkImpact { Id = i, Round = 7 }));
                    unique.Add(Fingerprint(effect));
                    var data = new List<Vector4>(); effect.Splash.GetCustomParticleData(data, ParticleSystemCustomData.Custom1);
                    foreach (var d in data) atlasTiles.Add((int)d.x);
                    Assert.That(effect.Splash.particleCount, Is.InRange(1, 2));
                    Assert.That(effect.Streaks.particleCount, Is.InRange(8, 12));
                    Assert.That(effect.Droplets.particleCount, Is.InRange(12, 18));
                    Assert.That(effect.Splash.particleCount + effect.Streaks.particleCount + effect.Droplets.particleCount, Is.LessThanOrEqualTo(32));
                    foreach (var particle in Particles(effect.Streaks).Concat(Particles(effect.Droplets)))
                    {
                        Assert.That(Vector3.Dot(particle.velocity, normal), Is.GreaterThan(0), "Drops start outside the struck surface");
                        Assert.That(particle.startLifetime, Is.LessThan(.8f));
                    }
                }
            }
            Assert.That(unique.Count, Is.EqualTo(60)); Assert.That(atlasTiles.Count, Is.GreaterThanOrEqualTo(8));
        }

        [Test] public void FullEffectExpiresAndCannotPaintOrEnterInkFusion()
        {
            var effect = Create(); effect.Play(Color.cyan, 123);
            foreach (var ps in new[] { effect.Splash, effect.Streaks, effect.Droplets })
            {
                Assert.That(ps.gameObject.layer, Is.EqualTo(LayerMask.NameToLayer("TransparentFX")));
                Assert.That(ps.collision.enabled || ps.trigger.enabled || ps.subEmitters.enabled || ps.emission.enabled, Is.False);
                Assert.That(ps.main.simulationSpace, Is.EqualTo(ParticleSystemSimulationSpace.World));
                ps.Simulate(.75f, false, false, true);
            }
            Assert.That(effect.IsAlive, Is.False);
            effect.Play(Color.magenta, 123); effect.Stop();
            Assert.That(effect.Splash.particleCount + effect.Streaks.particleCount + effect.Droplets.particleCount, Is.Zero);
        }

        [UnityTest] public IEnumerator PoolRecyclesAfterDenseHitsWithoutAMatch()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var go = new GameObject("Impact pool acceptance"); go.SetActive(false);
            var presentation = go.AddComponent<InkPresentation>();
            presentation.StreamPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameResource/Effects/Ink/Prefabs/InkStream.prefab").GetComponent<ParticleSystem>();
            presentation.ImpactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab).GetComponent<ParticleSystem>();
            go.SetActive(true);
            var pool = go.GetComponentsInChildren<InkImpactEffect>(true);
            Assert.That(pool.Length, Is.EqualTo(96));
            for (uint i = 0; i < 160; i++) presentation.Impact(new InkImpact { Id = i, Round = 1, Team = (byte)(i % 2 + 1), Hit = true, Normal = Vector3.up });
            Assert.That(pool.Count(e => e.gameObject.activeSelf), Is.EqualTo(96));
            // This suite uses EnterPlayMode from an EditMode runner; advance actual player frames.
            float start = Time.time, timeout = Time.realtimeSinceStartup + 5;
            while (Time.time - start < .9f && Time.realtimeSinceStartup < timeout) yield return null;
            yield return null;
            Assert.That(Time.time - start, Is.GreaterThanOrEqualTo(.9f), "Play Mode clock must advance");
            Assert.That(pool.Count(e => e.gameObject.activeSelf), Is.Zero, "Recycling must work even during match teardown");
            presentation.Impact(new InkImpact { Id = 200, Round = 2, Team = 2, Hit = true, Normal = Vector3.forward });
            Assert.That(pool.Count(e => e.gameObject.activeSelf), Is.EqualTo(1));
            presentation.Clear();
            Assert.That(pool.Any(e => e.IsAlive || e.gameObject.activeSelf), Is.False);
            Object.Destroy(go);
            yield return null;
            yield return new ExitPlayMode();
        }
    }
}
#endif
