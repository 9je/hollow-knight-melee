using Xunit;

namespace FoxMelee.Tests {
    public class BoostCharmsTests {
        [Fact]
        public void SprintmasterSpeedsTheRunAndDashSteadyBodyAddsWeight() {
            var plain = BoostCharms.Attributes(new FighterBase(2.0f, 2.2f, 75f), sprint: false, steady: false);
            Assert.Equal((2.0f, 2.2f, 75f), plain);
            var both = BoostCharms.Attributes(new FighterBase(2.0f, 2.2f, 75f), sprint: true, steady: true);
            Assert.Equal(2.2f, both.dash, 4); Assert.Equal(2.42f, both.run, 4); Assert.Equal(95f, both.weight, 4);
        }

        [Fact]
        public void StrengthHitsHarder() {
            Assert.Equal(5, HitRules.HkDamage(5, 5));
            Assert.Equal(8, HitRules.HkDamage(5, 5, BoostCharms.DamageScale(strength: true)));   // 7.5 rounds away from zero
            Assert.Equal(1.0, BoostCharms.DamageScale(strength: false));
        }

        [Fact]
        public void DeepFocusIsSlowerAndHealsTwo() {
            Assert.Equal((100, 1), BoostCharms.Focus(100, deep: false));
            Assert.Equal((165, 2), BoostCharms.Focus(100, deep: true));
        }
    }
}
