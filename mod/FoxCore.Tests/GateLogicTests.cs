using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class GateLogicTests {
        static readonly GateLogic Logic = GateLogic.Load();

        [Fact]
        public void DirtmouthFromKingsPassWithoutClaw() {
            var blocked = Logic.Blocked("Town", "right1", new KnightAbilities());
            Assert.Contains("left1", blocked);                      // the high ledge back up to King's Pass: Mantis Claw
            Assert.DoesNotContain("bot1", blocked);                 // the well down to the Crossroads
            Assert.DoesNotContain("door_mapper", blocked);          // Iselda's shop
            Assert.DoesNotContain("right1", blocked);               // where you came in
        }

        [Fact]
        public void TheClawOpensIt() =>
            Assert.DoesNotContain("left1", Logic.Blocked("Town", "right1", new KnightAbilities { Claw = true }));

        [Fact]
        public void TheEntranceIsNeverBlocked() {
            foreach (var t in Logic.Doors("Town")) Assert.DoesNotContain(t, Logic.Blocked("Town", t, new KnightAbilities()));
        }

        [Fact]
        public void NoDoorEntranceBlocksNothing() {
            Assert.Empty(Logic.Blocked("Town", "", new KnightAbilities()));          // a bench, a save load
            Assert.Empty(Logic.Blocked("Town", "nonsense", new KnightAbilities()));
            Assert.Empty(Logic.Blocked("Not_A_Room", "left1", new KnightAbilities()));
        }

        [Fact]
        public void EveryAbilityOpensEveryReachableRoomFully() {
            var all = new KnightAbilities { Claw = true, Wings = true, Dash = true, SuperDash = true, Acid = true, ShadeCloak = true,
                                            Fireball = true, Quake = true, Scream = true, DreamNail = true, Lantern = true };
            // with every ability, every Dirtmouth door is open: Sly's and Bretta's houses depend on rescues elsewhere (passable),
            // and top1 (logic NONE: no ability involved) is the game's own business, never walled
            Assert.Empty(Logic.Blocked("Town", "right1", all));
        }

        [Fact]
        public void ADropWithEveryExitShutLeavesThemOpen() {
            // synthetic room: entered by a one-way drop, its only exit needs the claw: nothing is blocked (never trapped)
            var g = GateLogic.Parse(
                "[{\"sceneName\":\"R\",\"gateName\":\"top1\",\"logic\":\"R[top1]\",\"oneWayType\":\"OneWayIn\",\"Name\":\"R[top1]\"}," +
                " {\"sceneName\":\"R\",\"gateName\":\"left1\",\"logic\":\"R[left1] | R + LEFTCLAW\",\"oneWayType\":\"TwoWay\",\"Name\":\"R[left1]\"}]",
                "[{\"name\":\"R\",\"logic\":\"R[top1] | R[left1]\"}]", "{}");
            Assert.Empty(g.Blocked("R", "top1", new KnightAbilities()));
            Assert.Empty(g.Blocked("R", "top1", new KnightAbilities { Claw = true }));
        }

        [Theory]
        [InlineData("A + B | C", true, false, true, true)]
        [InlineData("A + (B | C)", true, false, true, true)]
        [InlineData("A + (B | C)", true, false, false, false)]
        [InlineData("NONE", true, true, true, false)]
        [InlineData("A | FALSE", false, true, true, false)]
        public void Expressions(string expr, bool a, bool b, bool c, bool expected) =>
            Assert.Equal(expected, GateLogic.Evaluate(expr, t => t == "A" ? a : t == "B" ? b : t == "C" ? c : (bool?)null));

        static readonly KnightAbilities Everything = new KnightAbilities { Claw = true, Wings = true, Dash = true, SuperDash = true, Acid = true,
            ShadeCloak = true, Fireball = true, Quake = true, Scream = true, DreamNail = true, Lantern = true };

        [Fact]
        public void BackUpThroughCrystalPeaksDiveHoleTheWayOutIsOpen() {
            // Mines_01: dove in from the Crossroads (the floor is broken in the save), back up through bot1: left1 is the way out
            Assert.DoesNotContain("left1", Logic.Blocked("Mines_01", "bot1", Everything));
            // without Claw and Wings the climb up stays walled, as for the Knight: back down bot1 is the way out
        }

        [Fact]
        public void ARoomWithNoOpenExitOpensEverything() {
            // synthetic: entered through a two-way top door the Knight could only use with wings, every exit needing the claw
            var g = GateLogic.Parse(
                "[{\"sceneName\":\"R\",\"gateName\":\"top1\",\"logic\":\"R[top1] | R + WINGS\",\"oneWayType\":\"TwoWay\",\"Name\":\"R[top1]\"}," +
                " {\"sceneName\":\"R\",\"gateName\":\"left1\",\"logic\":\"R[left1] | R + LEFTCLAW\",\"oneWayType\":\"TwoWay\",\"Name\":\"R[left1]\"}]",
                "[{\"name\":\"R\",\"logic\":\"R[top1] | R[left1]\"}]", "{}");
            Assert.Empty(g.Blocked("R", "top1", new KnightAbilities()));
        }

        [Fact]
        public void AnExitTheKnightCantUseDoesntCountAsAWayOut() {
            // entered from the top; left1 needs the claw; right1 is reachable by nothing (a gate opened from the other side):
            // with no exit the Knight can use, nothing closes
            var g = GateLogic.Parse(
                "[{\"sceneName\":\"R\",\"gateName\":\"top1\",\"logic\":\"R[top1]\",\"oneWayType\":\"OneWayIn\",\"Name\":\"R[top1]\"}," +
                " {\"sceneName\":\"R\",\"gateName\":\"left1\",\"logic\":\"R[left1] | R + LEFTCLAW\",\"oneWayType\":\"TwoWay\",\"Name\":\"R[left1]\"}," +
                " {\"sceneName\":\"R\",\"gateName\":\"right1\",\"logic\":\"R[right1]\",\"oneWayType\":\"TwoWay\",\"Name\":\"R[right1]\"}]",
                "[{\"name\":\"R\",\"logic\":\"R[top1] | R[left1] | R[right1]\"}]", "{}");
            Assert.Empty(g.Blocked("R", "top1", new KnightAbilities()));
        }
    }
}
