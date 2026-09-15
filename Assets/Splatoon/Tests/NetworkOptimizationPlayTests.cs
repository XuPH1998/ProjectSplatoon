#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;
using Splatoon.Networking;
using Splatoon.Prototype;

namespace Splatoon.Tests
{
    public sealed class NetworkOptimizationPlayTests
    {
        bool _previousEnabled;
        EnterPlayModeOptions _previousOptions;

        [UnityTest]
        public IEnumerator RestartWithoutDomainReloadReinstallsSnapshotCodec()
        {
            _previousEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (int session = 0; session < 2; session++)
            {
                yield return new EnterPlayMode();
                Assert.That(UserNetworkVariableSerialization<PlayerSnapshot>.WriteDelta, Is.Not.Null);
                Assert.That(PlayerSnapshotDelta.EncodedBytes, Is.Zero, "Subsystem registration resets the previous session.");
                var state = new PlayerSnapshot { Revision = 1, Health = 100 };
                var next = state; next.Ink = 83.125f;
                using (var writer = new FastBufferWriter(4096, Allocator.Temp))
                {
                    UserNetworkVariableSerialization<PlayerSnapshot>.WriteDelta(writer, next, state);
                    using var reader = new FastBufferReader(writer, Allocator.None);
                    UserNetworkVariableSerialization<PlayerSnapshot>.ReadDelta(reader, ref state);
                    Assert.That(state.Ink, Is.EqualTo(next.Ink));
                }
                yield return new ExitPlayMode();
            }
        }

        [UnityTearDown]
        public IEnumerator RestoreSettings()
        {
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
            EditorSettings.enterPlayModeOptionsEnabled = _previousEnabled;
            EditorSettings.enterPlayModeOptions = _previousOptions;
        }
    }
}
#endif
