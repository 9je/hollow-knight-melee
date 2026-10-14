using System.Collections.Generic;
using Xunit;

namespace FoxMelee.Tests {
    public class CharmRowTests {
        [Fact]
        public void TheRowIsEnteredUnderTheGridsColumnAndLeft() {
            var row = new CharmRow(new List<int> { 2, 4, 37 });
            Assert.False(row.Active);
            Assert.True(row.Enter(column: 0, columns: 7));   // the grid's left end: the first charm
            Assert.Equal(0, row.Index);
            row.Enter(column: 6, columns: 7);                 // its right end: the last
            Assert.Equal(2, row.Index);
            row.Enter(column: 3, columns: 7);
            Assert.Equal(1, row.Index);
            Assert.Equal(4, row.Current);
            row.Leave();
            Assert.False(row.Active);
            Assert.Equal(0, row.Current);
        }

        [Fact]
        public void MovingStaysOnTheRow() {
            var row = new CharmRow(new List<int> { 2, 4 });
            row.Move(1);                                      // not on the row: nothing
            Assert.False(row.Active);
            row.Enter(0, 1);
            row.Move(-1); Assert.Equal(0, row.Index);
            row.Move(1); row.Move(1); Assert.Equal(1, row.Index);
        }

        [Fact]
        public void AnEmptyRowCannotBeEntered() => Assert.False(new CharmRow(new List<int>()).Enter(0, 7));

        [Theory]
        [InlineData(1, 3, 2, true)]     // one notch, one free
        [InlineData(2, 3, 2, false)]    // two notches, one free: no overcharming here
        [InlineData(0, 3, 3, true)]     // a free charm always fits
        public void ACharmEquipsWhileItsNotchesFit(int cost, int slots, int filled, bool can) => Assert.Equal(can, CharmRules.CanEquip(cost, slots, filled));

        [Fact]
        public void TheNormalCharmsAreTheOnesThatAreNotCharacters() {
            var owned = new HashSet<int> { 1, 2, 32, 37, 40 };   // 32 is Jigglypuff's
            var normal = CharmRules.Normal(id => owned.Contains(id));
            Assert.Equal(new List<int> { 1, 2, 37, 40 }, normal);
        }
    }
}
