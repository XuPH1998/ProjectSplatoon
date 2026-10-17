#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class BlockBenchmarkAcceptanceProfileTests
    {
        static object Invoke(string name, params object[] arguments)
            => typeof(BlockTerrainBenchmark).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);

        [Test]
        public void Prototype45RequiresOnlyTheFourApprovedWorkloadsAnd780FullSeconds()
        {
            const string profile = "prototype-v2-45fps";
            Assert.That((string[])Invoke("RequiredScenariosForProfile", profile),
                Is.EqualTo(new[] { "steady250k", "combat250k", "blast8", "scattered100k" }));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "steady250k"), Is.EqualTo(180d));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "combat250k"), Is.EqualTo(300d));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "blast8"), Is.EqualTo(120d));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "scattered100k"), Is.EqualTo(180d));
            Assert.That(Invoke("RequiredSamplingSecondsForProfile", profile), Is.EqualTo(780d));
        }

        [TestCase("tower32m")]
        [TestCase("cascade20k")]
        [TestCase("unknown")]
        public void DeferredAndUnknownWorkloadsCannotInitializeTheNewProfile(string scenario)
            => Assert.That(Invoke("RequiredSecondsForProfile", "prototype-v2-45fps", scenario), Is.Zero);

        [TestCase("strict", 600d, 1380d)]
        [TestCase("prototype-v1", 180d, 960d)]
        public void HistoricalProfilesKeepTheirSixWorkloadsAndDurations(string profile, double steady, double total)
        {
            Assert.That((string[])Invoke("RequiredScenariosForProfile", profile),
                Is.EqualTo(new[] { "steady250k", "combat250k", "tower32m", "blast8", "cascade20k", "scattered100k" }));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "steady250k"), Is.EqualTo(steady));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "tower32m"), Is.EqualTo(60d));
            Assert.That(Invoke("RequiredSecondsForProfile", profile, "cascade20k"), Is.EqualTo(120d));
            Assert.That(Invoke("RequiredSamplingSecondsForProfile", profile), Is.EqualTo(total));
        }

        [TestCase("strict", 15.5d, 16.67d)]
        [TestCase("prototype-v1", 15.5d, 16.67d)]
        [TestCase("prototype-v2-45fps", 22.2d, 33.3d)]
        public void OnlyTheNewProfileUsesTheApproved45FpsPercentiles(string profile, double p95, double p99)
        {
            Assert.That(Invoke("FrameP95LimitForProfile", profile), Is.EqualTo(p95));
            Assert.That(Invoke("FrameP99LimitForProfile", profile), Is.EqualTo(p99));
        }
    }
}
#endif
