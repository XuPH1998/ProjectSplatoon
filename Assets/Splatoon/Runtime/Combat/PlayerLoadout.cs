using System;
using Splatoon.Config;
using Splatoon.Prototype;
using UnityEngine;
namespace Splatoon.Combat
{
    public readonly struct PlayerLoadout
    {
        public readonly int HeroId, SubWeaponId, SpecialWeaponId;
        public readonly uint Revision;
        public PlayerLoadout(int hero,int sub,int special,uint revision=0){HeroId=hero;SubWeaponId=sub;SpecialWeaponId=special;Revision=revision;}
        public static SubWeaponRuntimeConfig SubWeapon(PlayerSnapshot s)=>SubWeaponConfigService.Current.GetById(SubWeaponConfigService.Current.Resolve(s.HeroId,s.SubWeaponId));
        public static PlayerLoadout From(PlayerSnapshot s)=>new(s.HeroId,SubWeaponConfigService.Current.Resolve(s.HeroId,s.SubWeaponId),s.SpecialWeaponId>0?s.SpecialWeaponId:1,s.HeroRevision);
        public bool Matches(PlayerLoadout b)=>HeroId==b.HeroId&&SubWeaponId==b.SubWeaponId&&SpecialWeaponId==b.SpecialWeaponId;
        public static PlayerLoadout Remembered(int hero)
        {
            int sub=PlayerPrefs.GetInt("Loadout.Sub."+hero,SubWeaponConfigService.Current.DefaultId(hero));
            int special=PlayerPrefs.GetInt("Loadout.Special."+hero,1);
            if(!SubWeaponConfigService.Current.Contains(sub))sub=SubWeaponConfigService.Current.DefaultId(hero);
            if(!SpecialWeaponConfigService.Current.Contains(special))special=1;
            return new PlayerLoadout(hero,sub,special);
        }
        public void Save(){PlayerPrefs.SetInt("Loadout.Sub."+HeroId,SubWeaponId);PlayerPrefs.SetInt("Loadout.Special."+HeroId,SpecialWeaponId);PlayerPrefs.Save();}
        public static string Validate(PlayerLoadout v)=>!SubWeaponConfigService.Current.Contains(v.SubWeaponId)?"副武器不存在":!SpecialWeaponConfigService.Current.Contains(v.SpecialWeaponId)?"大招不存在":null;
        public float RequiredPoints
        {
            get
            {
                float points=LubanConfigService.Current.Tables.TbSpecialWeapon.Get(SpecialWeaponId).RequiredPoints;int rank=0;bool conflict=false;
                foreach(var r in LubanConfigService.Current.Tables.TbLoadoutCost.DataList)
                {
                    if((r.HeroId!=0&&r.HeroId!=HeroId)||(r.SubWeaponId!=0&&r.SubWeaponId!=SubWeaponId)||(r.SpecialWeaponId!=0&&r.SpecialWeaponId!=SpecialWeaponId))continue;
                    int n=(r.HeroId!=0?1:0)+(r.SubWeaponId!=0?1:0)+(r.SpecialWeaponId!=0?1:0);
                    if(n==0){if(points<=0)points=r.RequiredPoints;continue;}
                    if(n==rank)conflict=true;
                    if(n>rank){rank=n;points=r.RequiredPoints;conflict=false;}
                }
                if(conflict)throw new InvalidOperationException("充能点数规则存在同优先级重叠");
                if(!float.IsFinite(points)||points<=0)throw new InvalidOperationException("大招所需点数必须为有限正数");
                return points;
            }
        }
    }
}
