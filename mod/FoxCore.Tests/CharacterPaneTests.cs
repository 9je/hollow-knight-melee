using Xunit;

namespace FoxMelee.Tests {
    public class CharacterPaneTests {
        static Character C(string n) => Roster.Find(n, out _);

        [Fact]
        public void TheCursorLeavesTheGridAtItsSidesForThePaneArrows() {
            var g = new CharacterGrid(C("Falco"));                       // row 1, column 0
            Assert.Equal(-1, g.Move(-1, 0));                             // past the left end: the left arrow
            Assert.Equal("Falco", g.Current.Name);
            g = new CharacterGrid(C("Young Link"));                      // row 1's last
            Assert.Equal(1, g.Move(1, 0));
            g = new CharacterGrid(C("Roy"));                             // the short row's last
            Assert.Equal(1, g.Move(1, 0));
            Assert.Equal(0, g.Move(-1, 0));
            Assert.Equal("Marth", g.Current.Name);
            Assert.Equal(0, g.Move(0, 1));                               // the bottom: stays (no edge)
        }

        [Fact]
        public void UpFromTheTopRowReachesTheColors() {
            var g = new CharacterGrid(C("Mario"));
            Assert.Equal(CharacterGrid.Top, g.Move(0, -1));
            Assert.Equal("Mario", g.Current.Name);
        }

        [Fact]
        public void TheTextSaysWhatChoosingDoes() {
            var marth = C("Marth"); var fox = C("Fox"); var puff = C("Jigglypuff");
            Assert.Equal(("Marth", "Rest at a bench to play as Marth."), CharacterPane.Describe(marth, owned: true, playing: false, atBench: false));
            Assert.Equal(("Marth", "Play as Marth."), CharacterPane.Describe(marth, true, false, true));
            Assert.Equal(("Fox", "Playing as Fox."), CharacterPane.Describe(fox, true, true, true));
            Assert.Equal(("???", "Sold by Iselda in Dirtmouth."), CharacterPane.Describe(marth, false, false, true));
            Assert.Equal(("???", "Found with one of Hallownest's charms."), CharacterPane.Describe(puff, false, false, true));
        }

        [Fact]
        public void ChoosingNeedsTheBenchAndSomethingNew() {
            var marth = C("Marth");
            Assert.True(CharacterPane.CanChoose(owned: true, atBench: true, playing: false, costumeChanged: false));
            Assert.True(CharacterPane.CanChoose(true, true, playing: true, costumeChanged: true));    // the same character in another color
            Assert.False(CharacterPane.CanChoose(true, true, true, false));
            Assert.False(CharacterPane.CanChoose(true, atBench: false, false, false));
            Assert.False(CharacterPane.CanChoose(owned: false, true, false, false));
        }

        [Fact]
        public void TheTilesFillHollowKnightsCharmGridCentredRowByRow() {
            // the charm grid's slots: columns 1.5 apart from x = -8 to 5.5 (10), rows 1.45 apart from y = 0 down
            var t = CharacterPane.Layout(new[] { 9, 10, 7 }, left: -8, right: 5.5, top: 0, rowStep: 1.45);
            Assert.Equal(3, t.Length);
            Assert.Equal(10, t[1].Length);
            Assert.Equal(-8, t[1][0].X, 6); Assert.Equal(5.5, t[1][9].X, 6);
            Assert.Equal(-1.45, t[1][0].Y, 6);
            Assert.Equal(-7.25, t[0][0].X, 6);            // 9 tiles: half a step in
            Assert.Equal(-5.75, t[2][0].X, 6);            // 7: one and a half
            Assert.Equal(1.5, CharacterPane.Step(new[] { 9, 10, 7 }, -8, 5.5), 6);
        }
    }
}
