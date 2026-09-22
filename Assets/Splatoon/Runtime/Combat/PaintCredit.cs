using Splatoon.Config;
using Splatoon.Prototype;
namespace Splatoon.Combat
{
    public enum PaintAttackKind:byte { Main, Sub, Special }
    public readonly struct PaintCredit
    {
        public readonly ulong Owner;
        public readonly uint Round, Equipment;
        public readonly byte Team;
        public readonly PaintAttackKind Kind;
        public PaintCredit(ulong owner,byte team,uint round,uint equipment,PaintAttackKind kind)
        {Owner=owner;Team=team;Round=round;Equipment=equipment;Kind=kind;}
        // Working calibration: legacy 300 square units / point, with the 10:1 reference
        // distance normalization gives 3. This still requires a captured 11.3.0 area fixture;
        // see Docs/SpecialWeapons.md. Never confuse this with the confirmed 200-point cost.
        public const double RawSquareUnitsPerPoint=3;
        public static double Points(double area)=>area/(RawSquareUnitsPerPoint*SubWeaponDefaults.Scale*SubWeaponDefaults.Scale);
        public void Apply(double area,double now)
        {
            var match=PrototypeMatch.Current;
            if(match==null||!match.IsServer||match.State.Value.Round!=Round||!PrototypePlayer.ByOwner.TryGetValue(Owner,out var player))return;
            player.ReceivePaintCredit(this,area,now);
        }
        public void ApplyTo(ref PlayerSnapshot s,double area,double now)
        {
            if(s.HeroRevision!=Equipment||s.Team!=Team)return;
            SpecialWeaponSimulation.AddPoints(ref s,Points(area),now,PlayerLoadout.From(s).RequiredPoints);
        }
    }
}
