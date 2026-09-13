using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using Splatoon.Combat;
using Splatoon.Prototype;

namespace Splatoon.Networking
{
    public static class GameplayContentSignature
    {
        public const int PaintProtocolVersion = 5;
        public static byte[] Compute(byte[] tables, string topology, PrototypePlayer player)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(tables.Length); w.Write(tables); w.Write(topology); w.Write(PlayerSnapshot.ProtocolVersion); w.Write(PaintProtocolVersion);
            var p = player.Presentation; var c = player.GetComponent<CharacterController>();
            Write(w, p.AimPivot); Write(w, p.MuzzlePosition); Write(w, player.SimulationAimPivot); Write(w, player.SimulationMuzzle.localPosition);
            Write(w, p.CameraPivot); Write(w, p.CameraOffset); w.Write(p.CameraCollisionRadius); w.Write(p.CameraCollisionPadding);
            Write(w, c.center); w.Write(c.radius); w.Write(c.height); w.Write(c.skinWidth); w.Write(c.stepOffset); w.Write(c.slopeLimit); Write(w, player.transform.lossyScale);
            w.Write(c.minMoveDistance); w.Write(c.detectCollisions); w.Write(c.enableOverlapRecovery); w.Write(player.gameObject.layer);
            for (int layer=0;layer<32;layer++) w.Write(Physics.GetIgnoreLayerCollision(player.gameObject.layer,layer));
            w.Write(p.StationarySpeed); w.Write(p.TurnThreshold); w.Write(p.MovingTurnSpeed); w.Write(p.TurnLeftDuration); w.Write(p.TurnRightDuration);
            Write(w, p.TurnLeftProgress); Write(w, p.TurnRightProgress);
            using var sha = SHA256.Create(); return sha.ComputeHash(stream.ToArray());
        }
        static void Write(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void Write(BinaryWriter w, AnimationCurve curve)
        {
            w.Write((int)curve.preWrapMode); w.Write((int)curve.postWrapMode); w.Write(curve.length);
            foreach (var k in curve.keys) { w.Write(k.time); w.Write(k.value); w.Write(k.inTangent); w.Write(k.outTangent); w.Write(k.inWeight); w.Write(k.outWeight); w.Write((int)k.weightedMode); }
        }
    }
}
