using Xunit;

namespace FoxMelee.Tests {
    public class InventoryPanesTests {
        // Hollow Knight's own switches, as its Inventory Control state machine holds them
        static readonly int[] TurnCompare = { -1, 4, 0, 1, 2, 3 };
        static readonly string[] TurnEvents = { "UNDER", "OVER", "INV", "CHARMS", "JOURNAL", "MAP" };

        [Fact]
        public void APaneTurnedToComesAfterTheMapAndOverMovesPastIt() {
            Assert.True(InventoryPanes.Extend(TurnCompare, TurnEvents, "CHARACTERS", out var compare, out var events, out int index, out var why), why);
            Assert.Equal(4, index);
            Assert.Equal(new[] { -1, 5, 0, 1, 2, 3, 4 }, compare);
            Assert.Equal(new[] { "UNDER", "OVER", "INV", "CHARMS", "JOURNAL", "MAP", "CHARACTERS" }, events);
        }

        [Fact]
        public void TheOpeningSwitchHasNoTurningEntries() {
            Assert.True(InventoryPanes.Extend(new[] { 0, 1, 2, 3 }, new[] { "INV", "CHARMS", "JOURNAL", "MAP" }, "CHARACTERS",
                out var compare, out var events, out int index, out var why), why);
            Assert.Equal(4, index);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, compare);
            Assert.Equal("CHARACTERS", events[4]);
        }

        [Fact]
        public void AnythingUnexpectedLeavesTheSwitchAlone() {
            // already there (patched twice)
            Assert.False(InventoryPanes.Extend(new[] { 0, 1, 2, 3, 4 }, new[] { "INV", "CHARMS", "JOURNAL", "MAP", "CHARACTERS" }, "CHARACTERS", out _, out _, out _, out _));
            // OVER isn't the pane count (another mod added a pane its own way)
            Assert.False(InventoryPanes.Extend(new[] { -1, 6, 0, 1, 2, 3 }, TurnEvents, "CHARACTERS", out _, out _, out _, out var why));
            Assert.Contains("OVER", why);
            // the arrays disagree
            Assert.False(InventoryPanes.Extend(new[] { 0, 1 }, new[] { "INV" }, "CHARACTERS", out _, out _, out _, out _));
            // the panes aren't 0..n-1
            Assert.False(InventoryPanes.Extend(new[] { 0, 2 }, new[] { "INV", "MAP" }, "CHARACTERS", out _, out _, out _, out _));
        }
    }
}
