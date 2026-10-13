using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class RosterTests {
        [Fact]
        public void EveryCharacterOnceWithAMeleeIdAndABank() {
            Assert.Equal(26, Roster.All.Count);
            Assert.Equal(Enumerable.Range(0, 26), Roster.All.Select(c => c.MeleeId).OrderBy(i => i));
            Assert.Equal(26, Roster.All.Select(c => c.Name).Distinct().Count());
            Assert.All(Roster.All, c => Assert.NotEmpty(c.Banks));
        }

        [Fact]
        public void UnlocksAreUniqueAndFoxIsFree() {
            Assert.Equal(0, Roster.Fox.Charm);
            Assert.Equal(2, Roster.Fox.MeleeId);
            var marth = Roster.All.Single(c => c.Name == "Marth");
            Assert.True(marth.IselaShop);
            var charms = Roster.All.Where(c => c.Charm != 0).Select(c => c.Charm).ToList();
            Assert.Equal(24, charms.Count);
            Assert.Equal(charms.Count, charms.Distinct().Count());
            Assert.DoesNotContain(2, charms);   // Wayward Compass stays a compass
        }

        [Fact]
        public void ZeldaAndSheikCannotTransform() =>
            Assert.Equal(new[] { "Sheik", "Zelda" }, Roster.All.Where(c => c.LockDownB).Select(c => c.Name).OrderBy(n => n));

        [Fact]
        public void KirbyHasHisCopyBank() => Assert.Equal(new[] { 14, 33, 11 }, Roster.All.Single(c => c.Name == "Kirby").Banks);

        [Theory]
        [InlineData("Marth", "Marth", true)]
        [InlineData("marth", "Marth", true)]
        [InlineData(" Captain Falcon ", "Captain Falcon", true)]
        [InlineData("Wario", "Fox", false)]
        [InlineData("", "Fox", false)]
        [InlineData(null, "Fox", false)]
        public void FindIsCaseInsensitiveAndFallsBackToFox(string name, string expected, bool known) {
            Assert.Equal(expected, Roster.Find(name, out bool k).Name);
            Assert.Equal(known, k);
        }

        [Theory]
        [InlineData(6, "Roy")]
        [InlineData(32, "Jigglypuff")]
        [InlineData(2, null)]     // Wayward Compass: a charm
        [InlineData(36, null)]    // Kingsoul: a charm
        [InlineData(0, null)]
        public void CharacterForCharm(int charm, string name) => Assert.Equal(name, Roster.ForCharm(charm)?.Name);
    }
}
