#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    // Ordinary EditMode BlockTerrain instances do not receive runtime OnDestroy.
    // Release their synchronous fixed-mode resources before the fixture destroys its own objects.
    internal static class BlockFixedGridEditFixtureCleanup
    {
        static readonly MethodInfo DisposeFixedGrid = typeof(BlockTerrain).GetMethod("DisposeFixedGrid",
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        internal static void ReleaseOwnedRoot(GameObject ownedRoot)
        {
            RequireEditMode();
            if (ownedRoot == null) return;
            ReleaseOwnedTerrains(ownedRoot.GetComponentsInChildren<BlockTerrain>(true));
        }

        internal static void ReleaseOwnedTerrains(IReadOnlyList<BlockTerrain> ownedTerrains)
        {
            RequireEditMode();
            if (ownedTerrains == null) throw new ArgumentNullException(nameof(ownedTerrains));
            var failures = new List<Exception>();
            foreach (var terrain in ownedTerrains)
            {
                try { Release(terrain); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0)
                throw new AggregateException("One or more fixture-owned fixed terrains could not release their resources.", failures);
        }

        internal static void Release(BlockTerrain terrain)
        {
            RequireEditMode();
            if (ReferenceEquals(terrain, null)) return;
            if (terrain == null)
                throw new InvalidOperationException("The fixture destroyed its tracked terrain before releasing fixed resources.");
            if (DisposeFixedGrid == null) throw new MissingMethodException(typeof(BlockTerrain).FullName, "DisposeFixedGrid");
            try { DisposeFixedGrid.Invoke(terrain, null); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        static void RequireEditMode()
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Explicit fixture cleanup cannot replace real runtime destruction coverage.");
        }
    }
}
#endif
