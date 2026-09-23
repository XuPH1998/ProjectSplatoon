using System;
using NUnit.Framework;
using Splatoon.Painting;
using UnityEngine.Experimental.Rendering;

namespace Splatoon.Tests
{
    public sealed class InkSoftEdgeTests
    {
        [Test] public void BoundaryFallbackRetainsSubmillimetrePrecision()
        {
            Assert.That(PaintSurface.SelectBoundaryFormat(_=>true),Is.EqualTo(GraphicsFormat.R16G16_SFloat));
            Assert.That(PaintSurface.SelectBoundaryFormat(f=>f==GraphicsFormat.R16G16_UNorm),Is.EqualTo(GraphicsFormat.R16G16_UNorm));
            Assert.Throws<NotSupportedException>(()=>PaintSurface.SelectBoundaryFormat(_=>false));
            for(int i=-1000;i<=1000;i++)
            {
                float distance=i*PaintSurface.BoundaryRange/1000;
                float packed=(float)Math.Round((distance/(2*PaintSurface.BoundaryRange)+.5)*65535)/65535;
                float decoded=(packed-.5f)*2*PaintSurface.BoundaryRange;
                Assert.That(Math.Abs(decoded-distance),Is.LessThan(.00001f));
            }
        }
    }
}
