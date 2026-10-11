using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class RoomGeometryTests {
        static ColliderOutline Box(double x0, double y0, double x1, double y1, bool oneWay = false) =>
            new ColliderOutline { Points = new[] { new V2(x0, y0), new V2(x1, y0), new V2(x1, y1), new V2(x0, y1) }, OneWay = oneWay };
        static readonly RoomTransform Unit = new RoomTransform { Scale = 1, Origin = new V2(0, 0) };

        [Fact]
        public void TouchingBoxesBecomeOneSolidWithFourCorners() {
            var r = RoomGeometry.Build(new[] { Box(0, 0, 10, 2), Box(10, 0, 20, 2) }, Unit);
            Assert.Single(r);
            Assert.Equal(ShapeKind.Solid, r[0].Kind);
            Assert.Equal(8, r[0].XY.Length);   // the shared edge and its collinear points are gone
        }

        [Fact]
        public void TerrainAroundARoomLeavesAHole() {
            // a ring of four walls: outer 0..100 x 0..50, inner empty space 10..90 x 10..40
            var walls = new[] { Box(0, 0, 100, 10), Box(0, 40, 100, 50), Box(0, 0, 10, 50), Box(90, 0, 100, 50) };
            var r = RoomGeometry.Build(walls, Unit);
            Assert.Equal(1, r.Count(s => s.Kind == ShapeKind.Solid));
            var hole = Assert.Single(r, s => s.Kind == ShapeKind.Hole);
            var ys = Enumerable.Range(0, hole.XY.Length / 2).Select(i => hole.XY[2 * i + 1]).ToArray();
            Assert.Equal(10f, ys.Min());
            Assert.Equal(40f, ys.Max());
        }

        [Fact]
        public void OpenEdgesBecomeThinSolids() {
            var edge = new ColliderOutline { Points = new[] { new V2(0, 5), new V2(10, 5) }, Closed = false };
            var s = Assert.Single(RoomGeometry.Build(new[] { edge }, Unit));
            Assert.Equal(ShapeKind.Solid, s.Kind);
            var ys = Enumerable.Range(0, s.XY.Length / 2).Select(i => s.XY[2 * i + 1]).ToArray();
            Assert.Equal(5 + RoomGeometry.EdgeThickness / 2, ys.Max(), 4);
            Assert.Equal(5 - RoomGeometry.EdgeThickness / 2, ys.Min(), 4);
        }

        [Fact]
        public void OneWayCollidersBecomePlatformsAlongTheirTop() {
            var r = RoomGeometry.Build(new[] { Box(4, 9, -2, 10, oneWay: true) }, Unit);
            var p = Assert.Single(r);
            Assert.Equal(ShapeKind.Platform, p.Kind);
            Assert.Equal(new float[] { -2, 10, 4, 10 }, p.XY);
        }

        [Fact]
        public void TransformScalesAroundTheOrigin() {
            var t = new RoomTransform { Scale = 6, Origin = new V2(100, 20) };
            var s = Assert.Single(RoomGeometry.Build(new[] { Box(100, 20, 101, 21) }, t));
            Assert.Equal(0f, s.XY.Where((v, i) => i % 2 == 0).Min());
            Assert.Equal(6f, s.XY.Where((v, i) => i % 2 == 0).Max());
            var back = t.ToHk(t.ToMelee(new V2(3.5, -7)));
            Assert.Equal(3.5, back.X, 9);
            Assert.Equal(-7, back.Y, 9);
        }

        [Fact]
        public void BoundsCoverEveryCollider() {
            var (min, max) = RoomGeometry.Bounds(new[] { Box(-5, 1, 2, 3), Box(0, -4, 9, 0) });
            Assert.Equal(-5, min.X); Assert.Equal(-4, min.Y); Assert.Equal(9, max.X); Assert.Equal(3, max.Y);
        }

        [Fact]
        public void NoCollidersMeansNoShapes() => Assert.Empty(RoomGeometry.Build(new ColliderOutline[0], Unit));
    }
}
