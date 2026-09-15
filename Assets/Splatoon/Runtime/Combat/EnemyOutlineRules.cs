using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public static class EnemyOutlineRules
    {
        public static bool IsEnemy(PlayerSnapshot target, byte viewerTeam) =>
            viewerTeam is 1 or 2 && target.Team is 1 or 2 && target.Team != viewerTeam && target.Health > 0;
    }
}
