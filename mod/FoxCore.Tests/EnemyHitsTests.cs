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
        public void AnEnemyFoxJustHitCannotHitHimForTheGrace() {
            var e = new EnemyHits(cooldownFrames: 30);
            object a = new object(), b = new object();
            e.Guard(a, frames: 20);                     // Fox's attack landed on a: its body can't hurt him for a moment
            Assert.True(e.Accept(b));                   // other enemies still can
            for (int i = 0; i < 19; i++) { e.Tick(); Assert.False(e.Accept(a)); }
            e.Tick();
            Assert.True(e.Accept(a));
            e.Guard(a, frames: 5);                      // a grace never shortens a running cooldown (30 frames)
            for (int i = 0; i < 29; i++) e.Tick();
            Assert.False(e.Accept(a));
            e.Tick();
            Assert.True(e.Accept(a));
        }

        [Fact]
        public void AfterALandedHitNoEnemyHitsForTheInvulnerability() {   // the Knight's moment of invincibility after a hit
            var e = new EnemyHits(cooldownFrames: 30);
            object a = new object(), b = new object();
            Assert.True(e.Accept(a));
            e.Invulnerable(frames: 45);
            Assert.False(e.Accept(b));                  // another enemy, right after: no
            for (int i = 0; i < 44; i++) { e.Tick(); Assert.False(e.Accept(b)); }
            e.Tick();
            Assert.True(e.Accept(b));
            Assert.True(e.Accept(a));                   // a's own cooldown (30) ran out meanwhile
            e.Invulnerable(frames: 10);
            e.Invulnerable(frames: 5);                  // never shortened
            for (int i = 0; i < 9; i++) { e.Tick(); Assert.False(e.Accept(b)); }
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
    
        // Fox's percent follows his masks (user's call): full health 10%, the last mask 100%, healing brings it back
        [Theory]
        [InlineData(5, 5, 10)]
        [InlineData(1, 5, 100)]
        [InlineData(3, 5, 55)]
        [InlineData(4, 5, 32.5)]
        [InlineData(9, 9, 10)]
        [InlineData(1, 9, 100)]
        [InlineData(1, 1, 10)]        // one mask in all: nothing to lose
        [InlineData(0, 5, 100)]       // dying: never past the top
        [InlineData(7, 5, 10)]        // lifeblood over the maximum: never under the bottom
        public void PercentFollowsMasks(int health, int maxHealth, double percent) =>
            Assert.Equal(percent, EnemyHits.PercentFor(new EnemyHitTuning(), health, maxHealth), 3);
    }
}
