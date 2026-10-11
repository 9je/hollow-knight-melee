using Xunit;

namespace FoxMelee.Tests {
    public class TerrainSetTests {
        [Fact]
        public void OnlyADifferentSetOfCollidersIsAChange() {
            var t = new TerrainSet();
            Assert.True(t.Changed(new[] { 3, 1, 2 }));     // first sight
            Assert.False(t.Changed(new[] { 1, 2, 3 }));    // same colliders, any order
            Assert.True(t.Changed(new[] { 1, 3 }));        // a wall broke
            Assert.True(t.Changed(new[] { 1, 3, 9 }));     // a gate closed
            Assert.False(t.Changed(new[] { 9, 3, 1 }));
            t.Reset();
            Assert.True(t.Changed(new[] { 9, 3, 1 }));     // a new room always loads
        }
    }
}
