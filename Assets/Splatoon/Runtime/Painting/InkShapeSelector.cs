using System.Collections.Generic;

namespace Splatoon.Painting
{
    /// <summary>Authority-only appearance stream. Never consumes ballistic or radius randomness.</summary>
    public sealed class InkShapeSelector
    {
        private sealed class Bag
        {
            public readonly int[] Shapes = new int[InkShapeAtlas.Count];
            private readonly int[] _recent = { -1, -1, -1, -1 };
            private uint _random;
            private int _cursor = InkShapeAtlas.Count, _history;
            public readonly uint Round;
            public Bag(uint seed, uint round) { _random = seed; Round = round; }
            private uint Next() => InkShapeAtlas.Hash(_random += 0x9e3779b9u);
            private bool Recent(int shape) => System.Array.IndexOf(_recent, shape) >= 0;
            public int Take()
            {
                if (_cursor == Shapes.Length)
                {
                    for (int i = 0; i < Shapes.Length; i++) Shapes[i] = i;
                    for (int i = Shapes.Length - 1; i > 0; i--)
                    {
                        int j = (int)(Next() % (uint)(i + 1));
                        (Shapes[i], Shapes[j]) = (Shapes[j], Shapes[i]);
                    }
                    // The first four in a new bag cannot repeat the last four of the old bag.
                    for (int i = 0; i < _recent.Length; i++)
                        if (Recent(Shapes[i]))
                            for (int j = _recent.Length; j < Shapes.Length; j++)
                                if (!Recent(Shapes[j])) { (Shapes[i], Shapes[j]) = (Shapes[j], Shapes[i]); break; }
                    _cursor = 0;
                }
                int selected = Shapes[_cursor++];
                _recent[_history] = selected; _history = (_history + 1) % _recent.Length;
                return selected;
            }
        }
        private readonly Dictionary<ulong, Bag> _shooters = new();
        public uint Select(ulong shooter, uint round, uint shotSeed, uint shotId, byte pellet, uint ordinal, bool impact)
        {
            uint entropy = InkShapeAtlas.Hash(shotSeed ^ InkShapeAtlas.Hash(shotId) ^ InkShapeAtlas.Hash(ordinal)
                ^ ((uint)pellet << 24) ^ (impact ? 0xb5297a4du : 0x68e31da4u));
            if (!_shooters.TryGetValue(shooter, out var bag) || bag.Round != round)
                _shooters[shooter] = bag = new Bag(InkShapeAtlas.Hash(entropy ^ (uint)shooter ^ (uint)(shooter >> 32) ^ round), round);
            return InkShapeAtlas.Pack(bag.Take(), entropy);
        }
        public void Clear() => _shooters.Clear();
    }
}
