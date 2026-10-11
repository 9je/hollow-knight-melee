using Xunit;

namespace FoxMelee.Tests {
    public class EnemyHitsTests {
        [Fact]
        public void AnEnemyCanHitFoxAgainOnlyAfterItsCooldown() {
            var e = new EnemyHits(cooldownFrames: 30);
            object a = new object(), b = new object();
            Assert.True(e.Accept(a));
            Assert.False(e.Accept(a));                  // the same enemy, same moment: one hit
            Assert.True(e.Accept(b));                   // another enemy is not blocked (no Knight-style invulnerability)
            for (int i = 0; i < 29; i++) e.Tick();
            Assert.False(e.Accept(a));
            e.Tick();
            Assert.True(e.Accept(a));
        }

        [Fact]
        public void AHollowKnightHitBecomesAMeleeHitScaledByMasks() {
            var t = new EnemyHitTuning { PercentPerMask = 10, Angle = 361, BaseKnockback = 30, KnockbackGrowth = 90, ShieldDamagePerMask = 8 };
            var h = EnemyHits.ToMelee(t, masks: 2, fromX: -12.5f, fromY: 4);
            Assert.Equal(20f, h.Damage);
            Assert.Equal(361, h.Angle);
            Assert.Equal(30, h.BaseKb);
            Assert.Equal(90, h.Growth);
            Assert.Equal(16f, h.ShieldDamage);
            Assert.Equal(-12.5f, h.FromX);
            Assert.Equal(0u, h.Unblockable);
            Assert.Equal(1u, EnemyHits.ToMelee(t, 3, 0, 0, unblockableFromMasks: 3).Unblockable);   // the biggest attacks
        }
    }
}
