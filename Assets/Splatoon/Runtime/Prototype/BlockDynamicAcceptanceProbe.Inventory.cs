#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Splatoon.Blocks;
using Splatoon.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Splatoon.Prototype
{
    public sealed partial class BlockDynamicAcceptanceProbe
    {
        [Serializable] public sealed class NativePhysicsObjectIdentity
        {
            public int instanceId, parentInstanceId, sceneHandle, layer, hideFlags;
            public string name, hierarchy, sceneName, scenePath;
            public bool activeSelf, activeInHierarchy;
        }
        [Serializable] public sealed class NativeRigidbodyInventoryRow
        {
            public NativePhysicsObjectIdentity item;
            public int bodyInstanceId, blockComponentInstanceId, worldInstanceId;
            public bool kinematic, gravity, detectCollisions, sleepingAvailable, sleeping;
            public bool blockComponent, liveBlock, worldAuthority, worldFrozen, parked;
            public ulong blockId;
            public uint blockRound;
            public byte blockOwner;
            public float mass;
            public string collisionMode, expectedLiveBlockMode;
            public bool expectedModeAvailable, modeMatches;
            public Vector3 position;
            public Quaternion rotation;
        }
        [Serializable] public sealed class NativeArticulationInventoryRow
        {
            public NativePhysicsObjectIdentity item;
            public int bodyInstanceId;
            public string jointType;
            public bool root;
        }
        [Serializable] public sealed class NativeColliderInventoryRow
        {
            public NativePhysicsObjectIdentity item;
            public int colliderInstanceId, attachedRigidbodyInstanceId, ancestorArticulationInstanceId, playerAncestorInstanceId;
            public string colliderType;
            public bool enabled, trigger;
        }
        [Serializable] public sealed class NativeControllerInventoryRow
        {
            public NativePhysicsObjectIdentity item;
            public int controllerInstanceId, playerAncestorInstanceId;
            public bool enabled, detectCollisions, overlapRecovery;
            public Vector3 center;
            public float radius, height, skinWidth, stepOffset, slopeLimit, minMoveDistance;
        }
        [Serializable] public sealed class NativePlayerInventoryRow
        {
            public NativePhysicsObjectIdentity item;
            public int playerComponentInstanceId;
            public bool spawned, authorityServer, owner;
            public ulong ownerClientId;
        }
        [Serializable] public sealed class NativePhysicsInventoryReport
        {
            public string source = "one explicit development visual-proof snapshot of all loaded scene components, including inactive objects; no mutation, asset loading or timed-combat scanning";
            public string limitation = "snapshot inventory, not a proof about future loads or already-destroyed transient objects; ancestor articulation identity is hierarchy evidence, not Collider native attachment";
            public string error, stage, utc, authorityMode, frozenAuthorityMode, replicaMode;
            public bool complete;
            public int unityFrame, loadedSceneCount, scannedRigidbodies, scannedArticulations, scannedColliders, scannedControllers, scannedPlayers;
            public int liveBlockBodies, nonLiveBlockBodies, otherRigidbodies, otherDynamicRigidbodies, liveBlockModeMismatches;
            public int weaponSimulation, collisionPolicyVersion;
            public float cellSize;
            public double realtimeSeconds;
            public long stopwatchStamp, stopwatchFrequency;
            public List<NativeRigidbodyInventoryRow> rigidbodies = new();
            public List<NativeArticulationInventoryRow> articulations = new();
            public List<NativeColliderInventoryRow> colliders = new();
            public List<NativeControllerInventoryRow> controllers = new();
            public List<NativePlayerInventoryRow> players = new();
        }

        static bool NativeInventorySceneObject(Component component)
            => component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded;

        static NativePhysicsObjectIdentity NativeInventoryIdentity(GameObject obj)
        {
            var hierarchy = new Stack<string>();
            for (var cursor = obj.transform; cursor != null; cursor = cursor.parent)
                hierarchy.Push(cursor.name + "[" + cursor.GetSiblingIndex() + "]");
            var scene = obj.scene;
            return new NativePhysicsObjectIdentity { instanceId = obj.GetInstanceID(),
                parentInstanceId = obj.transform.parent != null ? obj.transform.parent.gameObject.GetInstanceID() : 0,
                name = obj.name, hierarchy = string.Join("/", hierarchy), sceneHandle = scene.handle,
                sceneName = scene.name, scenePath = scene.path, layer = obj.layer, hideFlags = (int)obj.hideFlags,
                activeSelf = obj.activeSelf, activeInHierarchy = obj.activeInHierarchy };
        }

        NativePhysicsInventoryReport CaptureNativePhysicsInventory()
        {
            var report = new NativePhysicsInventoryReport { stage = _stage, unityFrame = Time.frameCount,
                realtimeSeconds = Time.realtimeSinceStartupAsDouble,
                stopwatchStamp = System.Diagnostics.Stopwatch.GetTimestamp(), stopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
                utc = DateTime.UtcNow.ToString("O"), loadedSceneCount = SceneManager.sceneCount,
                weaponSimulation = GameplayContentSignature.WeaponSimulationVersion,
                collisionPolicyVersion = BlockDynamicWorld.CollisionPolicyVersion, cellSize = BlockWorld.CellSize,
                authorityMode = BlockDynamicWorld.AuthorityCollisionMode.ToString(),
                frozenAuthorityMode = BlockDynamicWorld.FrozenAuthorityCollisionMode.ToString(),
                replicaMode = BlockDynamicWorld.ReplicaCollisionMode.ToString() };
            // Only the explicit visual-proof path may call this; no natural sampler
            // or lifecycle/pose handler calls it. No SyncTransforms or physics step.
            if (_stage != "visual-proof" || !Requested)
            { report.error = "Native inventory requires explicit visual-proof probe stage."; return report; }
            try
            {
                foreach (var body in Resources.FindObjectsOfTypeAll<Rigidbody>())
                {
                    if (!NativeInventorySceneObject(body)) continue;
                    var block = body.GetComponent<BlockDynamicBody>();
                    var world = block != null ? block.World : null;
                    bool live = world != null && world.Contains(block);
                    var row = new NativeRigidbodyInventoryRow { item = NativeInventoryIdentity(body.gameObject),
                        bodyInstanceId = body.GetInstanceID(), kinematic = body.isKinematic, gravity = body.useGravity,
                        detectCollisions = body.detectCollisions, sleepingAvailable = !body.isKinematic,
                        sleeping = !body.isKinematic && body.IsSleeping(), collisionMode = body.collisionDetectionMode.ToString(),
                        mass = body.mass, position = body.position, rotation = body.rotation,
                        blockComponent = block != null, blockComponentInstanceId = block != null ? block.GetInstanceID() : 0,
                        liveBlock = live, worldInstanceId = world != null ? world.GetInstanceID() : 0,
                        worldAuthority = world != null && world.IsAuthority, worldFrozen = world != null && world.Frozen,
                        blockId = block != null ? block.Id : 0, blockRound = world != null ? world.Round : 0,
                        blockOwner = block != null ? block.Owner : (byte)0, parked = block != null && block.Parked };
                    if (live)
                    {
                        var expected = !world.IsAuthority ? BlockDynamicWorld.ReplicaCollisionMode
                            : world.Frozen ? BlockDynamicWorld.FrozenAuthorityCollisionMode : BlockDynamicWorld.AuthorityCollisionMode;
                        row.expectedModeAvailable = true; row.expectedLiveBlockMode = expected.ToString();
                        row.modeMatches = body.collisionDetectionMode == expected;
                        report.liveBlockBodies++;
                        if (!row.modeMatches) report.liveBlockModeMismatches++;
                    }
                    else if (block != null) report.nonLiveBlockBodies++;
                    else { report.otherRigidbodies++; if (!body.isKinematic) report.otherDynamicRigidbodies++; }
                    report.rigidbodies.Add(row);
                }
                foreach (var body in Resources.FindObjectsOfTypeAll<ArticulationBody>())
                {
                    if (!NativeInventorySceneObject(body)) continue;
                    report.articulations.Add(new NativeArticulationInventoryRow { item = NativeInventoryIdentity(body.gameObject),
                        bodyInstanceId = body.GetInstanceID(), root = body.isRoot, jointType = body.jointType.ToString() });
                }
                foreach (var collider in Resources.FindObjectsOfTypeAll<Collider>())
                {
                    if (!NativeInventorySceneObject(collider)) continue;
                    var player = collider.GetComponentInParent<PrototypePlayer>(true);
                    var articulation = collider.GetComponentInParent<ArticulationBody>(true);
                    int playerId = player != null ? player.GetInstanceID() : 0;
                    report.colliders.Add(new NativeColliderInventoryRow { item = NativeInventoryIdentity(collider.gameObject),
                        colliderInstanceId = collider.GetInstanceID(), colliderType = collider.GetType().FullName,
                        attachedRigidbodyInstanceId = collider.attachedRigidbody != null ? collider.attachedRigidbody.GetInstanceID() : 0,
                        ancestorArticulationInstanceId = articulation != null ? articulation.GetInstanceID() : 0,
                        playerAncestorInstanceId = playerId, enabled = collider.enabled, trigger = collider.isTrigger });
                    if (collider is CharacterController controller)
                        report.controllers.Add(new NativeControllerInventoryRow { item = NativeInventoryIdentity(controller.gameObject),
                            controllerInstanceId = controller.GetInstanceID(), playerAncestorInstanceId = playerId,
                            enabled = controller.enabled, detectCollisions = controller.detectCollisions,
                            overlapRecovery = controller.enableOverlapRecovery, center = controller.center,
                            radius = controller.radius, height = controller.height, skinWidth = controller.skinWidth,
                            stepOffset = controller.stepOffset, slopeLimit = controller.slopeLimit, minMoveDistance = controller.minMoveDistance });
                }
                foreach (var player in Resources.FindObjectsOfTypeAll<PrototypePlayer>())
                {
                    if (!NativeInventorySceneObject(player)) continue;
                    report.players.Add(new NativePlayerInventoryRow { item = NativeInventoryIdentity(player.gameObject),
                        playerComponentInstanceId = player.GetInstanceID(), spawned = player.IsSpawned,
                        authorityServer = player.IsSpawned && player.IsServer, owner = player.IsSpawned && player.IsOwner,
                        ownerClientId = player.IsSpawned ? player.OwnerClientId : 0 });
                }
                report.rigidbodies.Sort((a, b) => a.bodyInstanceId.CompareTo(b.bodyInstanceId));
                report.articulations.Sort((a, b) => a.bodyInstanceId.CompareTo(b.bodyInstanceId));
                report.colliders.Sort((a, b) => a.colliderInstanceId.CompareTo(b.colliderInstanceId));
                report.controllers.Sort((a, b) => a.controllerInstanceId.CompareTo(b.controllerInstanceId));
                report.players.Sort((a, b) => a.playerComponentInstanceId.CompareTo(b.playerComponentInstanceId));
                report.scannedRigidbodies = report.rigidbodies.Count; report.scannedArticulations = report.articulations.Count;
                report.scannedColliders = report.colliders.Count; report.scannedControllers = report.controllers.Count;
                report.scannedPlayers = report.players.Count; report.complete = true;
            }
            catch (Exception exception) { report.error = exception.ToString(); }
            return report;
        }
    }
}
#endif
