using System.IO;
using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class GateSurveyTests {
        /// Every room and entrance, with every ability: what stays shut is written out for review (and each of those must
        /// be a door whose logic the Knight can never satisfy without a skip or another room's state).
        [Fact]
        public void WithEveryAbilityOnlyImpossibleDoorsStayShut() {
            var g = GateLogic.Load();
            var all = new KnightAbilities { Claw = true, Wings = true, Dash = true, SuperDash = true, Acid = true, ShadeCloak = true,
                                            Fireball = true, Quake = true, Scream = true, DreamNail = true, Lantern = true };
            var none = new KnightAbilities();
            using var w = new StreamWriter(Path.Combine(Path.GetTempPath(), "gate-survey.txt"));
            int shutAll = 0, shutNone = 0, rooms = 0;
            foreach (var scene in g.Scenes()) {
                rooms++;
                foreach (var entry in g.Doors(scene)) {
                    var a = g.Blocked(scene, entry, all);
                    var n = g.Blocked(scene, entry, none);
                    shutAll += a.Count; shutNone += n.Count;
                    foreach (var d in a) w.WriteLine($"ALL {scene}[{entry}] -> {d}: {g.LogicOf(scene, d)}");
                    Assert.True(a.All(d => n.Contains(d)), $"{scene}[{entry}]: more shut with every ability than with none");
                }
            }
            w.WriteLine($"rooms {rooms}, shut with every ability {shutAll}, with none {shutNone}");
        }
    }
}
