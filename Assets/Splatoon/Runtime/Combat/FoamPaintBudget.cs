using System;
using System.Collections.Generic;
using UnityEngine;
using Splatoon.Config;
using Splatoon.Painting;
using Splatoon.Prototype;

namespace Splatoon.Combat
{
    public sealed class FoamPaintBudget
    {
        public float Total {get;private set;}
        public float PrimaryEach {get;private set;}
        public float AuxiliaryEach {get;private set;}
        float _primaryRemaining,_auxiliaryRemaining;
        readonly HashSet<(uint ordinal,bool primary)> _seen=new();
        public float Spent { get; private set; }
        public FoamPaintBudget(InkShot shot)=>Reset(shot);
        internal void Reset(InkShot shot)
        {
            _seen.Clear();Spent=0;
            var w=shot.Configuration;int volley=WeaponSimulation.IsBubble(w)?Mathf.Max(1,w.BurstCount):1;
            Total=WeaponSimulation.InkCost(w,shot.Charge)*w.FoamVolumePerInk/(Mathf.Max(1,w.PelletCount)*volley);
            _primaryRemaining=Total*w.FoamPrimaryShare;_auxiliaryRemaining=Total-_primaryRemaining;
            int primary=WeaponSimulation.IsBubble(w)?w.BubbleMaxBounces+1:w.Ammo.HasExplosion?2:1;
            PrimaryEach=_primaryRemaining/Mathf.Max(1,primary);
            int trails=WeaponSimulation.IsSplatling(w)?w.SplatlingTrailCount:WeaponSimulation.IsBlaster(w)?w.BlasterTrailCount:
                DualiesNormalSimulation.Enabled(w)?w.DualiesTrailCount:Mathf.CeilToInt(WeaponSimulation.Range(w,shot.Charge)/Mathf.Max(.01f,w.TrailSpacing));
            AuxiliaryEach=_auxiliaryRemaining/Mathf.Max(1,1+trails);
        }
        public float Take(uint ordinal,bool primary)
        {
            if(!_seen.Add((ordinal,primary)))return 0;
            float amount;
            if(primary){amount=Mathf.Min(PrimaryEach,_primaryRemaining);_primaryRemaining-=amount;}
            else{amount=Mathf.Min(AuxiliaryEach,_auxiliaryRemaining);_auxiliaryRemaining-=amount;}
            Spent+=amount;return amount;
        }
    }

    public sealed partial class InkProjectileService
    {
        readonly Dictionary<(uint round,ulong shooter,uint id),FoamPaintBudget> _foamBudgets=new();
        readonly Stack<FoamPaintBudget> _foamBudgetPool=new();
        readonly List<(uint round,ulong shooter,uint id)> _foamExpired=new();
        readonly Dictionary<(uint round,ulong shooter,uint id),double> _foamBudgetExpiry=new();
        readonly List<(PaintSurface surface,PaintStamp stamp)> _foamGroup=new(48);
        bool _inFoamGroup;
        FoamPaintBudget Budget(InkShot shot)
        {
            var key=(shot.Round,shot.Shooter,shot.Id);
            if(!_foamBudgets.TryGetValue(key,out var budget))
            {
                if(_foamBudgetPool.Count>0){budget=_foamBudgetPool.Pop();budget.Reset(shot);}else budget=new FoamPaintBudget(shot);
                _foamBudgets.Add(key,budget);
                _foamBudgetExpiry[key]=shot.Born+shot.Configuration.Lifetime+shot.Configuration.PaintDropLifetime+shot.Configuration.WallDropSeconds+2;
            }
            return budget;
        }
        void BeginFoamGroup(){_inFoamGroup=true;_foamGroup.Clear();}
        void EndFoamGroup(InkShot shot)
        {
            _inFoamGroup=false;
            float volume=Budget(shot).Take(0xfffffff0,true);
            float each=_foamGroup.Count>0?volume/_foamGroup.Count:0;
            foreach(var entry in _foamGroup)PrototypeMatch.Current?.PaintWithFoam(entry.surface,entry.stamp,each);
            _foamGroup.Clear();
        }
        void PaintFoam(PaintSurface surface,InkShot shot,PaintStamp stamp,uint ordinal,bool primary)
        {
            if(_inFoamGroup){_foamGroup.Add((surface,stamp));return;}
            PrototypeMatch.Current?.PaintWithFoam(surface,stamp,Budget(shot).Take(ordinal,primary));
        }
        void PruneFoamBudgets(double now)
        {
            _foamExpired.Clear();foreach(var pair in _foamBudgetExpiry)if(pair.Value<now)_foamExpired.Add(pair.Key);
            foreach(var key in _foamExpired){_foamBudgetPool.Push(_foamBudgets[key]);_foamBudgets.Remove(key);_foamBudgetExpiry.Remove(key);}
        }
        void ClearFoamBudgets(){foreach(var b in _foamBudgets.Values)_foamBudgetPool.Push(b);_foamBudgets.Clear();_foamBudgetExpiry.Clear();_foamGroup.Clear();_inFoamGroup=false;}
    }
}
