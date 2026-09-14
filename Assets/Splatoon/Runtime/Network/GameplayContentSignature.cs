using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Splatoon.Painting;
using Splatoon.Combat;
using Splatoon.Prototype;

namespace Splatoon.Networking
{
    public static class GameplayContentSignature
    {
        public const int PaintProtocolVersion = 7;
        public static byte[] Compute(byte[] tables, string topology, PrototypePlayer player, IEnumerable<HeroContent> heroes = null)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(tables.Length); w.Write(tables); w.Write(topology); w.Write(PlayerSnapshot.ProtocolVersion); w.Write(PaintProtocolVersion); w.Write(InkShapeAtlas.ContentHash);
            var p = player.Presentation; var c = player.GetComponent<CharacterController>();
            Write(w, p.AimPivot); Write(w, p.MuzzlePosition); Write(w, player.SimulationAimPivot); Write(w, player.SimulationMuzzle.localPosition);
            Write(w, p.CameraPivot); Write(w, p.CameraOffset); w.Write(p.CameraCollisionRadius); w.Write(p.CameraCollisionPadding);
            Write(w, c.center); w.Write(c.radius); w.Write(c.height); w.Write(c.skinWidth); w.Write(c.stepOffset); w.Write(c.slopeLimit); Write(w, player.transform.lossyScale);
            w.Write(c.minMoveDistance); w.Write(c.detectCollisions); w.Write(c.enableOverlapRecovery); w.Write(player.gameObject.layer);
            for (int layer=0;layer<32;layer++) w.Write(Physics.GetIgnoreLayerCollision(player.gameObject.layer,layer));
            w.Write(p.StationarySpeed); w.Write(p.TurnThreshold); w.Write(p.MovingTurnSpeed); w.Write(p.TurnLeftDuration); w.Write(p.TurnRightDuration);
            Write(w, p.TurnLeftProgress); Write(w, p.TurnRightProgress);
            if (heroes != null)
            {
                var entries = heroes.OrderBy(h => h.Config.Id).ToArray(); w.Write(entries.Length);
                foreach (var hero in entries)
                {
                    w.Write(hero.Config.Id); w.Write(hero.Config.CharacterPrefabAddress); w.Write(hero.Config.WeaponPrefabAddress);
                    var profile = hero.Profile;
                    Write(w, profile.AimPivot); Write(w, profile.MuzzlePosition);
                    Write(w, profile.LeftMuzzlePosition); w.Write(profile.DualWield); w.Write(profile.SingleShot); w.Write(profile.ShotPlaybackSeconds);
                    w.Write(profile.BlendSeconds); w.Write(profile.AnimationReferenceSpeed);
                    w.Write(profile.WalkPlayback.x); w.Write(profile.WalkPlayback.y); w.Write(profile.WalkPlayback.z); w.Write(profile.WalkPlayback.w);
                    w.Write(profile.ShootDuration); w.Write(profile.DieForwardDuration); w.Write(profile.DieBackwardDuration);
                    w.Write(profile.SpineAimWeight); w.Write(profile.RecoilRecovery); w.Write(profile.CameraShake);
                    Write(w, profile.CameraPivot); Write(w, profile.CameraOffset);
                    w.Write(profile.CameraCollisionRadius); w.Write(profile.CameraCollisionPadding);
                    w.Write(profile.StationarySpeed); w.Write(profile.TurnThreshold); w.Write(profile.MovingTurnSpeed);
                    w.Write(profile.TurnLeftDuration); w.Write(profile.TurnRightDuration);
                    Write(w, profile.TurnLeftProgress); Write(w, profile.TurnRightProgress);
                    var binding = hero.WeaponPrefab.GetComponent<HeroWeaponBindings>();
                    Write(w, hero.WeaponPrefab.transform.localPosition); Write(w, hero.WeaponPrefab.transform.localEulerAngles);
                    Write(w, hero.WeaponPrefab.transform.localScale);
                    Write(w, hero.WeaponPrefab.transform.InverseTransformPoint(binding.Nozzle.position));
                    w.Write(binding.SupportLeftHand);
                    if (binding.SupportLeftHand) Write(w, hero.WeaponPrefab.transform.InverseTransformPoint(binding.LeftGrip.position));
                    w.Write(binding.LeftPart != null);
                    if (binding.LeftPart != null)
                    { Write(w, binding.LeftPart.localPosition); Write(w, binding.LeftPart.localEulerAngles); Write(w, binding.LeftPart.localScale); Write(w, binding.LeftPart.InverseTransformPoint(binding.LeftNozzle.position)); }
                }
            }
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
