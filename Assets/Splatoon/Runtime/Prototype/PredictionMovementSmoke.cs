#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Splatoon.Combat;
using Splatoon.Networking;
using Splatoon.Painting;
using UnityEngine;

namespace Splatoon.Prototype
{
    /// <summary>Opt-in two-process ground traversal fixture; never enabled by normal play.</summary>
    public static class PredictionMovementSmoke
    {
        static bool _placed, _friendly, _enemy, _killed;
        static double _start = -1, _nextPaint;
        static readonly Dictionary<ulong, Vector3> Lanes = new();
        public static void ModifyInput(PrototypePlayer player, ref PlayerInputFrame input)
        {
            input.Fire = false; input.Swim = false; input.Move = Vector2.zero;
            input.JumpSequence = player.PresentedState.ConsumedJump;
            input.Look = new Vector2(0, 20);
            if (_start < 0) return;
            double age = player.NetworkManager.ServerTime.Time - _start;
            input.Swim = age >= 4;
            input.Move = new Vector2(Mathf.Sin((float)age * Mathf.PI) * .2f, 0);
        }
        public static void Tick(PrototypeMatch match)
        {
            var local = PrototypePlayer.Local;
            if (local == null) return;
            if (match.IsServer && !_placed && match.Players.Count >= 2)
            {
                foreach (var p in match.Players)
                {
                    Vector3 origin = PrototypeArena.Spawn(p.Snapshot.Value.Team, p.Snapshot.Value.Slot) +
                        new Vector3(0, 2, p.Snapshot.Value.Team == 1 ? 3 : -3);
                    if (!Physics.Raycast(origin, Vector3.down, out var hit, 5, PlayerMotorSimulation.WorldMask))
                        throw new System.InvalidOperationException("Prediction fixture has no ground at spawn lane.");
                    Lanes[p.PlayerId] = hit.point;
                    p.DiagnosticPlace(hit.point + Vector3.up * .04f, 0);
                }
                _placed = true;
            }
            if (_start < 0 && local.Snapshot.Value.Revision >= 2)
            { _start = local.Snapshot.Value.SimulatedAt; Debug.Log("[PREDICTION-SMOKE] Ground fixture started"); }
            if (!match.IsServer || _start < 0) return;
            double now = match.NetworkManager.ServerTime.Time, age = now - _start;
            if (age >= 12 && !_friendly)
            { foreach (var p in match.Players) PaintLane(match, Lanes[p.PlayerId], p.Snapshot.Value.Team); _friendly = true; }
            if (age >= 24 && !_enemy)
            { foreach (var p in match.Players) PaintLane(match, Lanes[p.PlayerId], (byte)(3 - p.Snapshot.Value.Team)); _enemy = true; }
            if (age >= 6 && age < 31 && now >= _nextPaint)
            { _nextPaint = now + .2; Paint(match, Vector3.zero, (byte)(1 + ((int)(age * 5) & 1)), .4f); }
            if (age >= 32 && !_killed)
            { foreach (var p in match.Players) p.ReceiveDamage((byte)(3 - p.Snapshot.Value.Team), 200); _killed = true; }
        }
        static void Paint(PrototypeMatch match, Vector3 point, byte team, float radius)
        {
            if (!Physics.Raycast(point + Vector3.up * 2, Vector3.down, out var hit, 4, PlayerMotorSimulation.WorldMask) ||
                !hit.collider.TryGetComponent<PaintSurface>(out var surface))
                throw new System.InvalidOperationException("Prediction fixture paint surface missing.");
            match.Paint(surface, hit.point, hit.normal, radius, team, 1, 1);
        }
        static void PaintLane(PrototypeMatch match, Vector3 lane, byte team)
        {
            // Irregular splats can leave their centre unpainted. Verify actual gameplay coverage.
            for (int x = -2; x <= 2; x++)
            {
                var point = lane + Vector3.right * (x * .5f);
                for (int attempt = 0; attempt < 24 && match.Arena.FloorOwner(point + Vector3.up * .04f) != team; attempt++)
                    Paint(match, point + new Vector3((attempt % 3 - 1) * .35f, 0, (attempt / 3 % 3 - 1) * .35f), team, 3);
                if (match.Arena.FloorOwner(point + Vector3.up * .04f) != team)
                    throw new System.InvalidOperationException("Prediction fixture failed to cover the traversal lane.");
            }
        }
        public static void Reset() { _placed = _friendly = _enemy = _killed = false; _start = -1; _nextPaint = 0; Lanes.Clear(); }
    }
}
#endif
