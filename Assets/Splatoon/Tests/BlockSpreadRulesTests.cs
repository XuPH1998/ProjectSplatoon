using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Splatoon.Blocks;
using UnityEngine;

namespace Splatoon.Tests
{
    public sealed class BlockSpreadRulesTests
    {
        static readonly Vector2Int Source = new(0, 0), East = new(1, 0), Diagonal = new(1, 1);
        static Vector3Int Cell(Vector2Int column, int y) => new(column.x, y, column.y);
        static BlockWorld World() => new(new Vector2(32, 64));
        static void Stack(BlockWorld world, Vector2Int column, int bottom, int count, byte owner = 1)
        {
            for (int y = bottom; y < bottom + count; y++)
                Assert.That(world.Set(Cell(column, y), BlockCell.Create(owner)), Is.True);
        }
        static Func<Vector3Int, bool> Platform(int y, params Vector2Int[] columns)
        {
            var available = new HashSet<Vector2Int>(columns);
            return cell => cell.y == y && available.Contains(new Vector2Int(cell.x, cell.z));
        }
        static BlockSpreadMove[] Moves(BlockWorld world, Vector2Int source,
            Func<Vector3Int, bool> support, int maxStep = 1, int minStack = 2,
            Func<Vector3Int, bool> clear = null, Func<Vector3, Vector3, bool> route = null,
            Func<Vector2Int, bool> blocked = null, ISet<Vector3Int> arrivals = null,
            ISet<Vector2Int> receivedColumns = null)
            => BlockSpreadRules.Search(world, source, maxStep, minStack, support,
                clear ?? (_ => true), route ?? ((_, _) => true), blocked ?? (_ => false),
                arrivals ?? new HashSet<Vector3Int>(), receivedColumns).Where(move => move.HasValue).Select(move => move.Value).ToArray();

        [Test]
        public void TwoLayerStackCanMoveOneTopCellOntoEmptyContinuousMapGroundWithoutLosingPayloadOrCount()
        {
            var world = World(); Stack(world, Source, 10, 2);
            var value = new BlockCell { Owner = 2, Durability = 37, Flags = 0xa4 };
            world.Set(Cell(Source, 11), value);
            var before = world.EncodeSnapshot();
            var move = Moves(world, Source, Platform(10, Source, East)).Single();
            Assert.That(move.From, Is.EqualTo(Cell(Source, 11))); Assert.That(move.To, Is.EqualTo(Cell(East, 10)));
            Assert.That(move.Value.Owner, Is.EqualTo(2)); Assert.That(move.Value.Durability, Is.EqualTo(37));
            Assert.That(move.Value.Flags, Is.EqualTo(0xa4)); Assert.That(move.Value.Anchored, Is.False);
            CollectionAssert.AreEqual(before, world.EncodeSnapshot(), "Searching never edits its published source World.");
            var moved = world.Clone(); moved.Set(move.From, default); moved.Set(move.To, move.Value);
            Assert.That(moved.PinkCount, Is.EqualTo(world.PinkCount)); Assert.That(moved.BlueCount, Is.EqualTo(world.BlueCount));
            Assert.That(moved.Chunks.Values.Sum(chunk => chunk.Count), Is.EqualTo(2));
            Assert.That(moved.TryGet(move.From, out _), Is.False); Assert.That(moved.TryGet(move.To, out var arrived), Is.True);
            Assert.That(arrived.Owner, Is.EqualTo(value.Owner)); Assert.That(arrived.Durability, Is.EqualTo(value.Durability));
            Assert.That(arrived.Flags, Is.EqualTo(value.Flags));
            Assert.That(moved.TryGet(Cell(Source, 10), out var bottom), Is.True); Assert.That(bottom.Owner, Is.EqualTo(1));
            CollectionAssert.AreEqual(before, world.EncodeSnapshot(), "Applying a move to a draft cannot mutate the source.");
        }

        [Test]
        public void SearchYieldsBudgetUnitsBeforeReturningAtMostOneMoveAndCanResumeWithoutMutation()
        {
            var world = World(); Stack(world, Source, 10, 4);
            var before = world.EncodeSnapshot(); int budgetUnits = 0, moves = 0;
            using var search = BlockSpreadRules.Search(world, Source, 1, 2, cell => cell.y == 10,
                _ => true, (_, _) => true, _ => false, new HashSet<Vector3Int>()).GetEnumerator();
            Assert.That(search.MoveNext(), Is.True); Assert.That(search.Current.HasValue, Is.False);
            CollectionAssert.AreEqual(before, world.EncodeSnapshot());
            do
            {
                if (search.Current.HasValue) moves++; else budgetUnits++;
                Assert.That(budgetUnits, Is.LessThan(10000), "A bounded 8-neighbour search must eventually finish.");
            } while (search.MoveNext());
            Assert.That(budgetUnits, Is.GreaterThan(1)); Assert.That(moves, Is.EqualTo(1));
            CollectionAssert.AreEqual(before, world.EncodeSnapshot());
        }

        [Test]
        public void ThinLayersUnsupportedStacksAndAnchoredTopCellsRemainStill()
        {
            var thin = World(); Stack(thin, Source, 10, 1);
            Assert.That(Moves(thin, Source, Platform(10, Source, East)), Is.Empty);
            var unsupported = World(); Stack(unsupported, Source, 10, 3);
            Assert.That(Moves(unsupported, Source, Platform(10, East)), Is.Empty);
            var anchored = World(); Stack(anchored, Source, 10, 3);
            anchored.Set(Cell(Source, 12), BlockCell.Create(2, true));
            Assert.That(Moves(anchored, Source, Platform(10, Source, East), minStack: 1), Is.Empty,
                "The wall anchor cannot be carried away even when a single-cell source is permitted.");
        }

        [Test]
        public void ConfiguredSlopeRequiresEnoughHeightDifferenceBeforeAnOutwardMove()
        {
            var world = World(); Stack(world, Source, 10, 2);
            Assert.That(Moves(world, Source, Platform(10, Source, East), maxStep: 2), Is.Empty);
            Stack(world, Source, 12, 1);
            Assert.That(Moves(world, Source, Platform(10, Source, East), maxStep: 2).Single().To,
                Is.EqualTo(Cell(East, 10)));
        }

        [Test]
        public void ALowerExistingColumnCanReceiveTheTopWhileItsOriginalEnemyCellRemainsIntact()
        {
            var world = World(); Stack(world, Source, 10, 3); Stack(world, East, 10, 1, 2);
            var move = Moves(world, Source, Platform(10, Source, East)).Single();
            Assert.That(move.From, Is.EqualTo(Cell(Source, 12))); Assert.That(move.To, Is.EqualTo(Cell(East, 11)));
            Assert.That(world.TryGet(Cell(East, 10), out var enemy), Is.True); Assert.That(enemy.Owner, Is.EqualTo(2));
        }

        [Test]
        public void PlatformCliffsAndOccupiedColumnsWithDifferentFoundationsDoNotReceiveSpread()
        {
            var cliff = World(); Stack(cliff, Source, 10, 3);
            Assert.That(Moves(cliff, Source, cell => cell.x == 0 && cell.z == 0 && cell.y == 10
                || cell.x == 1 && cell.z == 0 && cell.y == 0), Is.Empty,
                "An empty neighbour far below the source platform is a cliff, not continuous ground.");
            var pedestal = World(); Stack(pedestal, Source, 10, 2); Stack(pedestal, East, 8, 2, 2);
            Assert.That(Moves(pedestal, Source, cell => cell.x == 0 && cell.z == 0 && cell.y == 10
                || cell.x == 1 && cell.z == 0 && cell.y == 8), Is.Empty,
                "Equal potential top height cannot hide incompatible foundation heights.");
        }

        [Test]
        public void DiagonalNeedsAtLeastOneCompleteOrthogonalFoundationAndRoute()
        {
            var world = World(); Stack(world, Source, 10, 2);
            bool Clear(Vector3Int cell) => cell.x == Diagonal.x && cell.z == Diagonal.y;
            Assert.That(Moves(world, Source, Platform(10, Source, Diagonal), clear: Clear), Is.Empty,
                "A diagonal island cannot be reached across two missing orthogonal platforms.");
            var segments = new List<(Vector3 From, Vector3 To)>();
            bool Route(Vector3 from, Vector3 to) { segments.Add((from, to)); return true; }
            var move = Moves(world, Source, Platform(10, Source, East, Diagonal), clear: Clear, route: Route).Single();
            Assert.That(move.To, Is.EqualTo(Cell(Diagonal, 10)));
            Vector3 start = BlockWorld.Center(move.From), corner = BlockWorld.Center(Cell(East, move.From.y));
            Vector3 high = BlockWorld.Center(Cell(Diagonal, move.From.y)), finish = BlockWorld.Center(move.To);
            Assert.That(segments, Does.Contain((start, corner))); Assert.That(segments, Does.Contain((corner, high)));
            Assert.That(segments, Does.Contain((high, finish)));
            Assert.That(Moves(world, Source, Platform(10, Source, East, Diagonal), clear: Clear,
                route: (from, to) => from != corner || to != high), Is.Empty,
                "The real orthogonal sweep must also be clear, not only the final diagonal destination.");
        }

        [Test]
        public void DiagonalChecksBothPlatformStepsAlongTheOrthogonalRoute()
        {
            var world = World(); Stack(world, Source, 10, 3);
            bool Clear(Vector3Int cell) => cell.x == Diagonal.x && cell.z == Diagonal.y;
            bool Support(Vector3Int cell) => cell == Cell(Source, 10) || cell == Cell(Diagonal, 11)
                || cell == Cell(East, 9) || cell == Cell(new Vector2Int(0, 1), 9);
            Assert.That(Moves(world, Source, Support, clear: Clear), Is.Empty,
                "Both possible middle-to-target segments exceed the one-cell platform step.");
            bool Connected(Vector3Int cell) => Support(cell) || cell == Cell(East, 10);
            Assert.That(Moves(world, Source, Connected, clear: Clear).Single().To, Is.EqualTo(Cell(Diagonal, 11)));
        }

        [Test]
        public void DestinationOverlapSweptObstaclesAndBlockedColumnsAllPreventMoves()
        {
            var world = World(); Stack(world, Source, 10, 2); var support = Platform(10, Source, East);
            Assert.That(Moves(world, Source, support, clear: _ => false), Is.Empty);
            Assert.That(Moves(world, Source, support, route: (_, _) => false), Is.Empty);
            Assert.That(Moves(world, Source, support, blocked: column => column == East), Is.Empty);
            int queries = 0;
            Assert.That(Moves(world, Source, cell => { queries++; return support(cell); },
                blocked: column => column == Source), Is.Empty);
            Assert.That(queries, Is.Zero, "A column with active falling geometry must not even begin a static search.");
        }

        [Test]
        public void AnArrivalCannotMoveAgainInTheSameRoundEvenWhenItsNewColumnIsTallEnough()
        {
            var world = World(); Stack(world, Source, 10, 3); Stack(world, East, 10, 1, 2);
            var first = Moves(world, Source, Platform(10, Source, East)).Single();
            world.Set(first.From, default); world.Set(first.To, first.Value);
            var eastExit = East + Vector2Int.right;
            var support = Platform(10, East, eastExit);
            Assert.That(Moves(world, East, support), Has.Length.EqualTo(1),
                "Without the arrival guard this destination is otherwise eligible to spread.");
            Assert.That(Moves(world, East, support, arrivals: new HashSet<Vector3Int> { first.To }), Is.Empty);
        }

        [Test]
        public void ReceivingColumnCannotMoveAnArrivalAgainAfterItSettlesAtAnotherHeightUntilTheNextRound()
        {
            var world = World(); Stack(world, Source, 10, 6); Stack(world, East, 10, 3);
            var payload = new BlockCell { Owner = 2, Durability = 37, Flags = 0xa4 };
            world.Set(Cell(Source, 15), payload);
            var first = Moves(world, Source, Platform(10, Source, East)).Single();
            Assert.That(first.To, Is.EqualTo(Cell(East, 13)));
            world.Set(first.From, default); world.Set(first.To, first.Value);
            var arrivals = new HashSet<Vector3Int> { first.To };
            var receivedColumns = new HashSet<Vector2Int> { East };

            // Shoot B10, then map the remaining B11..B13 payloads down one cell,
            // exactly as gravity's run-cell settlement preserves their ordering.
            var falling = Enumerable.Range(11, 3).Select(y =>
            {
                Assert.That(world.TryGet(Cell(East, y), out var value), Is.True);
                return value;
            }).ToArray();
            for (int y = 10; y <= 13; y++) world.Set(Cell(East, y), default);
            for (int i = 0; i < falling.Length; i++) world.Set(Cell(East, 10 + i), falling[i]);
            Assert.That(world.TryGet(Cell(East, 12), out var landed), Is.True);
            Assert.That(landed, Is.EqualTo(payload));
            Assert.That(arrivals.Contains(Cell(East, 12)), Is.False, "The old cell address cannot identify the settled arrival.");

            var exit = East + Vector2Int.right;
            var support = Platform(10, East, exit);
            Assert.That(Moves(world, East, support, arrivals: arrivals).Single().Value, Is.EqualTo(payload),
                "The stale cell-address guard alone would allow the same payload to move twice.");
            Assert.That(Moves(world, East, support, arrivals: arrivals, receivedColumns: receivedColumns), Is.Empty);

            arrivals.Clear(); receivedColumns.Clear();
            Assert.That(Moves(world, East, support, arrivals: arrivals, receivedColumns: receivedColumns).Single().Value,
                Is.EqualTo(payload), "A new round releases the receiving-column guard.");
        }

        [Test]
        public void AReceivingColumnRemainsEligibleAsATargetForOtherSources()
        {
            var world = World(); Stack(world, Source, 10, 4); Stack(world, East, 10, 1);
            var move = Moves(world, Source, Platform(10, Source, East),
                receivedColumns: new HashSet<Vector2Int> { East }).Single();
            Assert.That(move.To, Is.EqualTo(Cell(East, 11)));
        }
    }
}
