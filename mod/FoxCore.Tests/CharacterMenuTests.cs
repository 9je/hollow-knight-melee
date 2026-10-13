using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class CharacterMenuTests {
        [Fact]
        public void FoxAlwaysOwnedMarthByQuillOthersByTheirCharm() {
            Assert.Equal(new[] { "Fox" }, Roster.Owned(_ => false, false, false).Select(c => c.Name));
            Assert.Equal(new[] { "Fox", "Marth" }, Roster.Owned(_ => false, true, false).Select(c => c.Name));
            var owned = Roster.Owned(charm => charm == 32 || charm == 13, false, false).Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "Fox", "Jigglypuff", "Falco" }, owned);   // Quick Slash, Mark of Pride; roster order
            Assert.Equal(26, Roster.Owned(_ => false, false, true).Count);
        }

        [Fact]
        public void TheCursorStartsOnWhoPlaysAndWraps() {
            var owned = Roster.Owned(_ => false, false, true);
            var m = new CharacterMenu(owned, owned.Single(c => c.Name == "Marth"));
            Assert.Equal("Marth", m.Current.Name);
            m.Move(-1);
            Assert.Equal("Fox", m.Current.Name);
            m.Move(-1);
            Assert.Equal("Kirby", m.Current.Name);   // wraps to the end
            m.Move(1);
            Assert.Equal("Fox", m.Current.Name);
        }

        [Fact]
        public void APlayerWhoLostTheirCharacterStartsOnFox() {
            var owned = Roster.Owned(_ => false, false, false);
            Assert.Equal("Fox", new CharacterMenu(owned, Roster.Find("Peach", out _)).Current.Name);
        }
    }
}
