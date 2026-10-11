using System.Collections.Generic;
using Xunit;

namespace FoxMelee.Tests {
    public class StuckGuardTests {
        static RoomShape Box(ShapeKind k, float x0, float y0, float x1, float y1) =>
            new RoomShape { Kind = k, XY = new[] { x0, y0, x1, y0, x1, y1, x0, y1 } };

        // a solid block 0..100 x 0..100 with a cave 20..80 x 20..80, and a pillar 40..60 inside the cave
        static readonly List<RoomShape> Room = new List<RoomShape> {
            Box(ShapeKind.Solid, 0, 0, 100, 100), Box(ShapeKind.Hole, 20, 20, 80, 80), Box(ShapeKind.Solid, 40, 20, 60, 50),
            new RoomShape { Kind = ShapeKind.Platform, XY = new float[] { 0, 150, 100, 150 } },
        };

        [Theory]
        [InlineData(10, 10, true)]     // in the rock
        [InlineData(30, 30, false)]    // in the cave
        [InlineData(50, 30, true)]     // in the pillar
        [InlineData(50, 120, false)]   // above everything
        [InlineData(50, 150, false)]   // platforms are not solid
        public void InsideSolidFollowsTheShapes(double x, double y, bool inside) =>
            Assert.Equal(inside, StuckGuard.InsideSolid(Room, x, y));

        [Fact]
        public void RescuesToTheLastSafeGroundAfterBeingInsideLongEnough() {
            var g = new StuckGuard();
            Assert.Null(g.Frame(inside: false, grounded: true, 30, 21));
            Assert.Null(g.Frame(inside: false, grounded: false, 35, 40));   // airborne: not a safe spot
            for (int i = 1; i < StuckGuard.Frames; i++) Assert.Null(g.Frame(inside: true, grounded: false, 10, 10));
            var to = g.Frame(inside: true, grounded: false, 10, 10);
            Assert.NotNull(to);
            Assert.Equal(30, to.Value.X); Assert.Equal(21, to.Value.Y);
            Assert.Null(g.Frame(inside: true, grounded: false, 10, 10));   // counts again from zero
        }

        [Fact]
        public void PassingThroughIsNotStuck() {
            var g = new StuckGuard();
            g.Frame(false, true, 30, 21);
            for (int i = 0; i < 3 * StuckGuard.Frames; i++)
                Assert.Null(g.Frame(inside: i % StuckGuard.Frames != 0, grounded: false, 10, 10));
        }

        [Fact]
        public void NoSafeSpotYetMeansNoRescue() {
            var g = new StuckGuard();
            for (int i = 0; i < 2 * StuckGuard.Frames; i++) Assert.Null(g.Frame(true, false, 10, 10));
        }

        [Fact]
        public void ANewRoomForgetsTheSafeSpot() {
            var g = new StuckGuard();
            g.Frame(false, true, 30, 21);
            g.Reset();
            for (int i = 0; i < 2 * StuckGuard.Frames; i++) Assert.Null(g.Frame(true, false, 10, 10));
        }
    }
}
