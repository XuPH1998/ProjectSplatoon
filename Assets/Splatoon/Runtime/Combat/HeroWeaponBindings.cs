using UnityEngine;

namespace Splatoon.Combat
{
    // Authored once on a weapon prefab; runtime never guesses bone or muzzle names.
    public sealed class HeroWeaponBindings : MonoBehaviour
    {
        public Transform Nozzle;
        public Transform LeftGrip;
        public Transform LeftPart, LeftNozzle;
        public bool SupportLeftHand = true;
    }
}
