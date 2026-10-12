using Xunit;

namespace FoxMelee.Tests {
    public class OutOfRoomTests {
        // a 100 x 50 scene (Hollow Knight units, origin at its bottom left)
        static bool Run(OutOfRoom g, double x, double y, int frames) {
            bool fired = false;
            for (int i = 0; i < frames; i++) fired |= g.Frame(x, y, 100, 50);
            return fired;
        }

        [Theory]
        [InlineData(50, 25, false)]                          // inside
        [InlineData(50, -OutOfRoom.Margin + 1, false)]       // just below the bottom: an exit down still takes him
        [InlineData(50, -OutOfRoom.Margin - 1, true)]        // fell out of the bottom
        [InlineData(-OutOfRoom.Margin - 1, 25, true)]        // out the left
        [InlineData(100 + OutOfRoom.Margin + 1, 25, true)]   // out the right
        [InlineData(50, 500, false)]                         // high above: he comes back down
        public void FiresOnlyOutsideTheRoom(double x, double y, bool fires) =>
            Assert.Equal(fires, Run(new OutOfRoom(), x, y, OutOfRoom.Frames));

        [Fact]
        public void WaitsItsFramesAndCountsAgainAfterComingBack() {
            var g = new OutOfRoom();
            Assert.False(Run(g, 50, -100, OutOfRoom.Frames - 1));
            Assert.False(g.Frame(50, 25, 100, 50));          // back in: the count restarts
            Assert.False(Run(g, 50, -100, OutOfRoom.Frames - 1));
            Assert.True(g.Frame(50, -100, 100, 50));
        }
    }
}
