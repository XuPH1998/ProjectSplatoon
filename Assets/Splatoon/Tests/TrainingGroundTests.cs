#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEditor.SceneManagement;
using Splatoon.Painting;
using Splatoon.Prototype;
namespace Splatoon.Tests
{
    public sealed class TrainingGroundTests
    {
        [Test] public void RectangularGroundHasCorrectAreaAndIndependentBounds()
        {
            var grid=new SurfaceOwnershipGrid(new Vector2(32,64),.125f,null);
            Assert.That(grid.Columns,Is.EqualTo(256));Assert.That(grid.Rows,Is.EqualTo(512));Assert.That(grid.TotalArea,Is.EqualTo(2048));
            Assert.That(grid.At(new Vector3(0,0,31.99f)),Is.Zero);
            Assert.That(grid.At(new Vector3(16,0,0)),Is.EqualTo(255));Assert.That(grid.At(new Vector3(0,0,32)),Is.EqualTo(255));
            Assert.That(grid.At(new Vector3(-16.01f,0,0)),Is.EqualTo(255));
        }
        [Test] public void SlopeCountsActualAreaIncludingPartialEdgeCells()
        {
            var grid=new SurfaceOwnershipGrid(new Vector2(6,Mathf.Sqrt(45)),.125f,null);
            Assert.That(grid.TotalArea,Is.EqualTo(6*Mathf.Sqrt(45)).Within(.0001));
            for(int i=0;i<grid.Cells.Length;i++)grid.Set(i,1);
            Assert.That(grid.PinkArea,Is.EqualTo(grid.TotalArea).Within(.000001));
            grid.Paint(Vector3.zero,1,2);Assert.That(grid.BlueArea,Is.GreaterThan(0));
            Assert.That(grid.PinkArea+grid.BlueArea,Is.EqualTo(grid.TotalArea).Within(.000001));
            grid.Clear();Assert.That(grid.PinkArea+grid.BlueArea,Is.Zero);
        }
        [Test] public void BlockedFootprintsCannotBePaintedOrRestoredAsWalkable()
        {
            var grid=new SurfaceOwnershipGrid(new Vector2(1,1),.5f,new[]{0});
            grid.Paint(Vector3.zero,3,1);Assert.That(grid.TotalArea,Is.EqualTo(.75));Assert.That(grid.PinkArea,Is.EqualTo(.75));
            Assert.Throws<InvalidOperationException>(()=>grid.Restore(new byte[]{0,1,1,1}));
            grid.Clear();Assert.That(grid.Cells[0],Is.EqualTo(255));
        }
        [Test] public void CheckpointSeparatesLayersAndRejectsTopologyOrInvalidOwners()
        {
            var state=new PaintCheckpoint{Topology="map-a",Ownership=new Dictionary<int,byte[]>{{1,new byte[]{1,0,0,0,0,0,0,0,0,0}},{2,new byte[]{2,0,0,0,0,0,0,0,0,0}}}};
            var sizes=new Dictionary<int,int>{{1,10},{2,10}};var masks=new Dictionary<int,int>();
            var data=PaintSnapshotCodec.Encode(state);var decoded=PaintSnapshotCodec.Decode(data,"map-a",sizes,masks);
            Assert.That(decoded.Ownership[1][0],Is.EqualTo(1));Assert.That(decoded.Ownership[2][0],Is.EqualTo(2));
            Assert.Throws<InvalidDataException>(()=>PaintSnapshotCodec.Decode(data,"map-b",sizes,masks));
            state.Ownership[1][0]=3;
            Assert.Throws<InvalidDataException>(()=>PaintSnapshotCodec.Decode(PaintSnapshotCodec.Encode(state),"map-a",sizes,masks));
        }
        [Test] public void AuthoredBridgeAndGroundResolveDifferentFootSurfaces()
        {
            var scene=EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity",OpenSceneMode.Single);
            var arena=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<PrototypeArena>()).Single();
            arena.RegisterSurfaces();foreach(var surface in arena.Surfaces.Values)surface.InitializeOwnership(.125f);
            Physics.SyncTransforms();
            var bridge=arena.Surfaces.Values.Single(s=>s.name=="Bridge");
            Vector3 below=new Vector3(1,0,1),above=new Vector3(1,3,1);
            Assert.That(Physics.Raycast(below+Vector3.up*.2f,Vector3.down,out var hit,.5f),Is.True);
            var ground=hit.collider.GetComponent<PaintSurface>();
            ground.Ownership.Paint(ground.transform.InverseTransformPoint(below),1,1);
            bridge.Ownership.Paint(bridge.transform.InverseTransformPoint(above),1,2);
            Assert.That(arena.FloorOwner(below),Is.EqualTo(1));Assert.That(arena.FloorOwner(above),Is.EqualTo(2));
            var checkpoint=arena.CaptureOwnership();arena.ClearPaint();arena.RestoreOwnership(checkpoint);
            Assert.That(arena.FloorOwner(below),Is.EqualTo(1));Assert.That(arena.FloorOwner(above),Is.EqualTo(2));
            Assert.That(arena.BakedTopology,Is.EqualTo(arena.ComputeTopology()));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        }
        [Test] public void CoplanarBrushCrossesTileSeamWithoutPaintingBridge()
        {
            var config = Splatoon.Config.LubanConfigService.Current;
            var tables = new cfg.Tables(name => SimpleJSON.JSONNode.Parse(File.ReadAllText("Assets/GameResource/Bootstrap/Config/Luban/" + name + ".json")));
            typeof(Splatoon.Config.LubanConfigService).GetProperty("Tables").SetValue(config, tables);
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity", OpenSceneMode.Single);
                var arena = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PrototypeArena>()).Single();
                arena.InitializeRuntime(); Physics.SyncTransforms();
                var point = new Vector3(-.2f, 0, 1);
                Assert.That(Physics.Raycast(point + Vector3.up * .2f, Vector3.down, out var hit, .5f), Is.True);
                var surface = hit.collider.GetComponent<PaintSurface>();
                arena.Apply(new PaintStamp { SurfaceId = surface.SurfaceId, Position = point, Normal = Vector3.up, Radius = 1, Hardness = .5f, Strength = 1, Team = 1 }, true);
                Assert.That(arena.FloorOwner(point), Is.EqualTo(1));
                Assert.That(arena.FloorOwner(new Vector3(.2f, 0, 1)), Is.EqualTo(1));
                Assert.That(arena.FloorOwner(new Vector3(.2f, 3, 1)), Is.Zero);
            }
            finally { config.Reset(); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        }
        [Test] public void AuthoredRoutesAllowHighPlatformJumpDropAndOuterLaneTraversal()
        {
            EditorSceneManager.OpenScene("Assets/GameResource/Gameplay/maps/TrainingGround.unity", OpenSceneMode.Single);
            var probe = new GameObject("Jump and lane probe"); probe.layer = 8;
            var cc = probe.AddComponent<CharacterController>(); cc.height = 1.8f; cc.center = Vector3.up * .9f; cc.radius = .35f; cc.skinWidth = .03f;
            try
            {
                cc.enabled = false; probe.transform.position = new Vector3(11, 3.05f, 3); cc.enabled = true; Physics.SyncTransforms();
                float vertical = 7.5f, highest = 3;
                for (int i = 0; i < 120; i++)
                {
                    vertical -= 22f / 60;
                    cc.Move((Vector3.left * 4.5f + Vector3.up * vertical) / 60);
                    highest = Mathf.Max(highest, probe.transform.position.y);
                    if (cc.isGrounded && vertical < 0) vertical = -2;
                }
                Assert.That(highest, Is.GreaterThan(4)); Assert.That(probe.transform.position.y, Is.InRange(-.05f, .2f));
                cc.enabled = false; probe.transform.position = new Vector3(14.5f, .05f, -30); cc.enabled = true; Physics.SyncTransforms();
                for (int i = 0; i < 600; i++) cc.Move(new Vector3(0, -.04f, .1f));
                Assert.That(probe.transform.position.z, Is.GreaterThan(29));
                Assert.That(probe.transform.position.y, Is.InRange(-.05f, .2f));
            }
            finally { UnityEngine.Object.DestroyImmediate(probe); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        }
    }
}
#endif
