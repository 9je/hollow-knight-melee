using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class RosterOwnedTests {
        [Fact]
        public void FoxAlwaysOwnedMarthBoughtOthersByTheirCharm() {
            Assert.Equal(new[] { "Fox" }, Roster.Owned(_ => false, false, false).Select(c => c.Name));
            Assert.Equal(new[] { "Fox", "Marth" }, Roster.Owned(_ => false, true, false).Select(c => c.Name));
            var owned = Roster.Owned(charm => charm == 32 || charm == 13, false, false).Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "Fox", "Jigglypuff", "Falco" }, owned);   // Quick Slash, Mark of Pride; roster order
            Assert.Equal(26, Roster.Owned(_ => false, false, true).Count);
        }
    }
}
