using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class CharacterGridTests {
        [Fact]
        public void MeleesLayoutWithSheikBesideZelda() {
            var rows = CharacterGrid.Rows.Select(r => r.Select(c => c.Name).ToArray()).ToArray();
            Assert.Equal(new[] { 9, 10, 7 }, rows.Select(r => r.Length));
            Assert.Equal("Dr. Mario", rows[0][0]); Assert.Equal("Ganondorf", rows[0][8]);
            Assert.Equal("Falco", rows[1][0]); Assert.Equal(new[] { "Zelda", "Sheik" }, rows[1].Skip(6).Take(2));
            Assert.Equal("Roy", rows[2][6]);
            Assert.Equal(26, rows.SelectMany(r => r).Distinct().Count());
        }

        [Fact]
        public void TheCursorStartsOnWhoPlaysAndMovesAroundTheGrid() {
            var g = new CharacterGrid(Roster.Find("Fox", out _));
            Assert.Equal("Fox", g.Current.Name);
            g.Move(1, 0); Assert.Equal("Ness", g.Current.Name);
            g.Move(0, -1); Assert.Equal("Luigi", g.Current.Name);     // up a row, same column
            g.Move(0, 1); g.Move(0, 1); Assert.Equal("Puff", Short(g.Current));
            g.Move(-10, 0); Assert.Equal("Pichu", g.Current.Name);    // stops at the row's ends
            g.Move(0, 1); Assert.Equal("Pichu", g.Current.Name);      // and at the bottom
        }

        [Fact]
        public void MovingDownFromAWiderRowLandsOnTheLastSlot() {
            var g = new CharacterGrid(Roster.Find("Young Link", out _));   // row 2, column 9
            g.Move(0, 1);
            Assert.Equal("Roy", g.Current.Name);
        }

        static string Short(Character c) => c.Name == "Jigglypuff" ? "Puff" : c.Name;
    }
}
