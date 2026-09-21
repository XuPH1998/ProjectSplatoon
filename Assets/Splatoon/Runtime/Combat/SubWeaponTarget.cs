using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;

namespace Splatoon.Combat
{
    public sealed class SubWeaponTarget : MonoBehaviour
    {
        public const int Layer = 9;
        public uint Id { get; private set; }
        public byte Team { get; private set; }
        public ulong Owner { get; private set; }
        public SubWeaponType Type { get; private set; }
        public Collider HitCollider { get; private set; }
        SubWeaponService _service;
        public void Initialize(SubWeaponService service, SubEntityState state, SubWeaponRuntimeConfig config)
        {
            _service = service; Id = state.Id; Team = state.Team; Owner = state.Owner; Type = state.Type;
            gameObject.layer = Layer;
            HitCollider = Type == SubWeaponType.SplashWall ? gameObject.AddComponent<BoxCollider>() : gameObject.AddComponent<SphereCollider>();
            HitCollider.isTrigger = Type != SubWeaponType.SplashWall;
            UpdateState(state, config);
        }
        public void UpdateState(SubEntityState state, SubWeaponRuntimeConfig config, double at = double.NaN)
        {
            transform.SetPositionAndRotation(state.Position, Quaternion.Euler(0, state.Yaw, 0));
            if (HitCollider is BoxCollider box)
            {
                box.size = config.Wall.size; box.center = Vector3.up * config.Wall.size.y * .5f;
                box.enabled = state.Phase == SubEntityPhase.Active && (double.IsNaN(at) ? TimeNow() : at) + 1e-8 >= state.Changed + config.Wall.expand;
                foreach (var player in PrototypePlayer.ByOwner.Values)
                    if (player != null) Physics.IgnoreCollision(box, player.GetComponent<CharacterController>(), player.PresentedState.Team == Team);
            }
            else if (HitCollider is SphereCollider sphere) sphere.radius = Type == SubWeaponType.Torpedo ? .3f : .35f;
        }
        static double TimeNow() => PrototypeMatch.Current != null ? PrototypeMatch.Current.NetworkManager.ServerTime.Time : Time.timeAsDouble;
        public bool CanHit(ulong shooter, byte? team)
        {
            if (team.HasValue) return team.Value != Team;
            return !PrototypePlayer.ByOwner.TryGetValue(shooter, out var p) || p.Snapshot.Value.Team != Team;
        }
        public void Hit(InkShot shot, float damage) => _service?.DamageObject(Id, shot.Team, damage * SubWeaponObjectDamage.Multiplier(shot.Configuration, Type));
    }
}
