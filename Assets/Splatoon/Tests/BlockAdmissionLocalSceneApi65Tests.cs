#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Splatoon.Tests
{
    [Category("BlockAdmissionLocalSceneApi65")]
    public sealed class BlockAdmissionLocalSceneApi65Tests
    {
        [Serializable] sealed class Sample
        {
            public string stage;
            public bool returned, helperEnabled, helperActive, helperTrigger, roofEnabled;
            public Vector3 direction, helperPosition, explicitPosition, helperSize, helperScale, roofPosition, roofCenter, roofSize;
            public float depth;
            public int helperId, roofId, helperScene, roofScene;
            public bool helperPhysicsDefault, roofPhysicsDefault, defaultOverlapContainsHelper, defaultOverlapContainsRoof;
            public bool defaultRayContainsHelper, defaultBoxCastContainsHelper, ownSceneOverlapContainsHelper;
            public int ownSceneOverlapCount;
        }
        [Serializable] sealed class Report
        {
            public string utc, unityVersion, prefix, experimentError, cleanupError;
            public bool formalAcceptance, enteredActualPlayMode, localSceneCreated, localScenePhysicsDefault;
            public bool helperDestroyed, roofDestroyed, localSceneUnloaded, activeSceneUnchanged, autoSyncUnchanged;
            public bool noTestObjectsRemain;
            public int sceneCountBefore, sceneCountAfter;
            public Sample sameDefault, runtimeLocal, returnedDefault;
        }
        static readonly Vector3 C = new Vector3(1.35f, 2.25f, 2.25f);
        static readonly Vector3 Far = new Vector3(0, 1032, 0);

        static Sample Read(string stage, BoxCollider helper, BoxCollider roof)
        {
            var own = helper.gameObject.scene.GetPhysicsScene();
            var sample = new Sample {
                stage = stage, helperEnabled = helper.enabled, helperActive = helper.gameObject.activeInHierarchy,
                helperTrigger = helper.isTrigger, roofEnabled = roof.enabled,
                helperPosition = helper.transform.position, explicitPosition = C,
                helperSize = helper.size, helperScale = helper.transform.lossyScale,
                roofPosition = roof.transform.position, roofCenter = roof.center, roofSize = roof.size,
                helperId = helper.GetInstanceID(), roofId = roof.GetInstanceID(),
                helperScene = (int)helper.gameObject.scene.handle, roofScene = (int)roof.gameObject.scene.handle,
                helperPhysicsDefault = own.Equals(Physics.defaultPhysicsScene),
                roofPhysicsDefault = roof.gameObject.scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene)
            };
            sample.returned = Physics.ComputePenetration(helper, C, Quaternion.identity,
                roof, roof.transform.position, roof.transform.rotation, out sample.direction, out sample.depth);
            sample.defaultOverlapContainsHelper = Physics.OverlapBox(Far, Vector3.one * .2f, Quaternion.identity,
                ~0, QueryTriggerInteraction.Collide).Contains(helper);
            sample.defaultOverlapContainsRoof = Physics.OverlapBox(C, Vector3.one * .2f, Quaternion.identity,
                ~0, QueryTriggerInteraction.Collide).Contains(roof);
            sample.defaultRayContainsHelper = Physics.RaycastAll(Far - Vector3.right, Vector3.right, 2,
                ~0, QueryTriggerInteraction.Collide).Any(h => h.collider == helper);
            sample.defaultBoxCastContainsHelper = Physics.BoxCastAll(Far - Vector3.right, Vector3.one * .05f,
                Vector3.right, Quaternion.identity, 2, ~0, QueryTriggerInteraction.Collide).Any(h => h.collider == helper);
            // Only the two test shapes can inhabit this remote region. A full buffer is an explicit failure.
            var buffer = new Collider[8];
            sample.ownSceneOverlapCount = own.OverlapBox(Far, Vector3.one * .2f, buffer, Quaternion.identity,
                ~0, QueryTriggerInteraction.Collide);
            if (sample.ownSceneOverlapCount == buffer.Length) throw new InvalidOperationException("API probe overlap buffer full.");
            sample.ownSceneOverlapContainsHelper = buffer.Take(sample.ownSceneOverlapCount).Contains(helper);
            return sample;
        }

        [UnityTest] public IEnumerator ActualPlayModeLocalSceneCrossPenetrationHasSameAndReturnedSceneControls()
        {
            // The test deliberately enters the real Unity runtime, rather than emulating its lifecycle.
            yield return new EnterPlayMode();
            var report = new Report { utc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion,
                formalAcceptance = false, enteredActualPlayMode = Application.isPlaying,
                prefix = "AdmissionRuntimeLocalApi65_" + Guid.NewGuid().ToString("N") };
            Scene original = SceneManager.GetActiveScene(), local = default(Scene);
            report.sceneCountBefore = SceneManager.sceneCount;
            bool autoSyncBefore = Physics.autoSyncTransforms;
            GameObject helperObject = null, roofObject = null;
            try
            {
                if (!Application.isPlaying || !original.IsValid() || !original.isLoaded ||
                    !original.GetPhysicsScene().Equals(Physics.defaultPhysicsScene))
                    throw new InvalidOperationException("Needs an actual PlayMode default-physics active scene.");
                roofObject = new GameObject(report.prefix + "_roof") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(roofObject, original);
                roofObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var roof = roofObject.AddComponent<BoxCollider>();
                roof.center = new Vector3(1.65f, 2.21f, 2.55f); roof.size = new Vector3(1.5f, .02f, 1.5f);
                helperObject = new GameObject(report.prefix + "_helper") { hideFlags = HideFlags.HideAndDontSave, layer = 29 };
                SceneManager.MoveGameObjectToScene(helperObject, original);
                helperObject.transform.SetPositionAndRotation(Far, Quaternion.identity);
                var helper = helperObject.AddComponent<BoxCollider>();
                helper.size = Vector3.one * .2998f; helper.isTrigger = true; helper.enabled = true;
                Physics.SyncTransforms();
                report.sameDefault = Read("same-default-scene", helper, roof);

                local = SceneManager.CreateScene(report.prefix + "_local", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                report.localSceneCreated = local.IsValid() && local.isLoaded;
                report.localScenePhysicsDefault = local.GetPhysicsScene().Equals(Physics.defaultPhysicsScene);
                SceneManager.MoveGameObjectToScene(helperObject, local); Physics.SyncTransforms();
                report.runtimeLocal = Read("runtime-local-cross-default-scene", helper, roof);

                SceneManager.MoveGameObjectToScene(helperObject, original); Physics.SyncTransforms();
                report.returnedDefault = Read("returned-default-scene", helper, roof);
            }
            catch (Exception error) { report.experimentError = error.ToString(); }

            // Retire test shapes before waiting for deferred Destroy and local-scene unload.
            if (helperObject != null) { helperObject.SetActive(false); Object.Destroy(helperObject); }
            if (roofObject != null) { roofObject.SetActive(false); Object.Destroy(roofObject); }
            AsyncOperation unload = null;
            if (local.IsValid() && local.isLoaded)
            {
                try { unload = SceneManager.UnloadSceneAsync(local); }
                catch (Exception error) { report.cleanupError = error.ToString(); }
            }
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            do { yield return null; }
            while ((helperObject != null || roofObject != null || local.IsValid() && local.isLoaded || unload != null && !unload.isDone) &&
                Time.realtimeSinceStartupAsDouble < deadline);
            report.helperDestroyed = helperObject == null; report.roofDestroyed = roofObject == null;
            report.localSceneUnloaded = !local.IsValid() || !local.isLoaded;
            report.activeSceneUnchanged = SceneManager.GetActiveScene().handle == original.handle;
            report.autoSyncUnchanged = Physics.autoSyncTransforms == autoSyncBefore;
            report.sceneCountAfter = SceneManager.sceneCount;
            report.noTestObjectsRemain = !Resources.FindObjectsOfTypeAll<GameObject>().Any(go => go != null &&
                go.name.StartsWith(report.prefix, StringComparison.Ordinal));
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Reports/BlockSimulation20261018/AdmissionPenetrationApiPlay04");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log("[BLOCK API65] actual PlayMode API observation written: " + path);

            Assert.That(report.enteredActualPlayMode, Is.True);
            Assert.That(report.experimentError, Is.Null.Or.Empty);
            Assert.That(report.cleanupError, Is.Null.Or.Empty);
            Assert.That(report.sameDefault, Is.Not.Null); Assert.That(report.runtimeLocal, Is.Not.Null); Assert.That(report.returnedDefault, Is.Not.Null);
            foreach (var control in new[] { report.sameDefault, report.returnedDefault })
            {
                Assert.That(control.returned, Is.True, control.stage + " geometry positive control");
                Assert.That(control.direction, Is.EqualTo(Vector3.up)); Assert.That(control.depth, Is.EqualTo(.1199f).Within(.0001f));
                Assert.That(control.defaultOverlapContainsHelper && control.ownSceneOverlapContainsHelper, Is.True);
            }
            Assert.That(report.localSceneCreated && !report.localScenePhysicsDefault, Is.True);
            Assert.That(report.runtimeLocal.ownSceneOverlapContainsHelper, Is.True);
            Assert.That(report.runtimeLocal.defaultOverlapContainsRoof, Is.True);
            Assert.That(report.runtimeLocal.defaultOverlapContainsHelper || report.runtimeLocal.defaultRayContainsHelper ||
                report.runtimeLocal.defaultBoxCastContainsHelper, Is.False);
            Assert.That(report.helperDestroyed && report.roofDestroyed && report.localSceneUnloaded && report.noTestObjectsRemain, Is.True);
            Assert.That(report.activeSceneUnchanged && report.autoSyncUnchanged, Is.True);
            // Cross-scene returned/direction/depth are deliberately raw observations. Only a true,
            // correct result can justify a production cross-scene design; this API test's pass cannot.
            yield return new ExitPlayMode();
        }

        [UnityTearDown] public IEnumerator EnsureEditModeAfterAnyFailedApiAssertion()
        {
            if (Application.isPlaying) yield return new ExitPlayMode();
        }
    }
}
#endif
