#if UNITY_EDITOR
using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Splatoon.Painting;
using Splatoon.Prototype;
namespace Splatoon.Tests
{
    public sealed class InkCoverageTests
    {
        [SetUp] public void LoadAtlas() => InkShapeAtlas.Configure(UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(InkShapeAtlas.AssetPath));
        static readonly Matrix4x4 Identity=Matrix4x4.identity;
        static PaintStamp Stamp(byte team,float strength=1)=>new(){Team=team,Position=Vector3.zero,Normal=Vector3.up,Radius=1.5f,Hardness=.01f,Strength=strength};
        [Test] public void FaintPaintAccumulatesInsteadOfBeingDiscarded()
        {
            var grid=new SurfaceOwnershipGrid(new Vector2(4,4),.125f,null);
            grid.Apply(Stamp(1,.2f),Identity,.5f,.034424f,110);Assert.That(grid.PinkArea,Is.Zero);
            for(int i=0;i<12;i++)grid.Apply(Stamp(1,.2f),Identity,.5f,.034424f,110);
            Assert.That(grid.PinkArea,Is.GreaterThan(0));
            double area=grid.PinkArea;var before=(byte[])grid.State.Clone();
            for(int i=0;i<12;i++)grid.Apply(Stamp(1,.2f),Identity,.5f,.034424f,110);
            Assert.That(grid.PinkArea,Is.GreaterThanOrEqualTo(area));
            Assert.That(grid.State.Where((v,i)=>i%4==0).Sum(v=>(int)v),
                Is.GreaterThan(before.Where((v,i)=>i%4==0).Sum(v=>(int)v)), "Repeated faint paint must keep accumulating even when the occupied cells do not expand");
        }
        [Test] public void OpposingPaintErodesPreviousTeamAndTiesKeepPreviousOwner()
        {
            var value=new Color32(128,128,2,255);
            Assert.That(InkCoverage.Accumulate(value,1,0).b,Is.EqualTo(2));
            value=new Color32(255,0,1,255);
            value=InkCoverage.Accumulate(value,2,.2f);Assert.That(value.b,Is.EqualTo(1));
            for(int i=0;i<8;i++)value=InkCoverage.Accumulate(value,2,.2f);
            Assert.That(value.b,Is.EqualTo(2));Assert.That(value.g,Is.GreaterThan(value.r));
        }
        [Test] public void RestoringFaintInkThenContinuingMatchesUninterruptedPainting()
        {
            var a=new SurfaceOwnershipGrid(new Vector2(4,4),.125f,new[]{0});
            a.Apply(Stamp(1,.2f),Identity,.5f,.034424f,110);var bytes=a.Capture();
            var b=new SurfaceOwnershipGrid(new Vector2(4,4),.125f,new[]{0});b.Restore(bytes);
            for(int n=0;n<12;n++){var stamp=Stamp((byte)(n%3==0?2:1),.4f);a.Apply(stamp,Identity,.5f,.034424f,110);b.Apply(stamp,Identity,.5f,.034424f,110);}
            CollectionAssert.AreEqual(a.Capture(),b.Capture());Assert.That(b.Cells[0],Is.EqualTo(255));
            b.Clear();Assert.That(Array.TrueForAll(b.State,x=>x==0),Is.True);
        }
        [Test] public void DetailCoordinatesAreContinuousAcrossTranslatedFloorTiles()
        {
            Vector3 point=new(0,0,7);
            var left=Matrix4x4.Translate(new Vector3(-8,0,0));var right=Matrix4x4.Translate(new Vector3(8,0,0));
            Assert.That(InkCoverage.DetailUV(left.MultiplyPoint3x4(new Vector3(8,0,7)),Vector3.up,.034424f),Is.EqualTo(InkCoverage.DetailUV(right.MultiplyPoint3x4(new Vector3(-8,0,7)),Vector3.up,.034424f)));
            var slope=Quaternion.Euler(-26.565f,0,0);Vector3 n=slope*Vector3.up,t=slope*Vector3.forward;
            Assert.That((InkCoverage.DetailUV(point+t,n,1)-InkCoverage.DetailUV(point,n,1)).magnitude,Is.EqualTo(1).Within(.0001));
        }
        [Test] public void PaletteUsesOriginalReferencePinkAndMintBlue()
        {
            Assert.That(PrototypeArena.Pink,Is.EqualTo(new Color(.9433962f,.27945885f,.47586557f,1)));
            Assert.That((Color32)PrototypeArena.Blue,Is.EqualTo(new Color32(125,227,232,255)));
        }
    }
}
#endif
