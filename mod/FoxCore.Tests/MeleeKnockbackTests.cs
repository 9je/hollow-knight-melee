using Xunit;

namespace FoxMelee.Tests {
    public class MeleeKnockbackTests {
        [Fact]
        public void FoxJabOnAFreshWeight100Victim() =>   // ((4/10 + 4*4/20) * 200/200 * 1.4 + 18) * 100/100 + 0
            Assert.Equal(19.68, MeleeKnockback.Knockback(4, growth: 100, setKb: 0, baseKb: 0, percentAfter: 4, weight: 100), 3);

        [Fact]
        public void SetKnockbackIgnoresPercent() =>      // ((1 + 10/2) * 1.4 + 18) * 100/100 + 0
            Assert.Equal(26.4, MeleeKnockback.Knockback(4, 100, setKb: 10, baseKb: 0, percentAfter: 90, weight: 100), 3);

        [Fact]
        public void HeavierPercentLaunchesFurther() =>
            Assert.True(MeleeKnockback.Knockback(14, 100, 0, 30, 80, 100) > MeleeKnockback.Knockback(14, 100, 0, 30, 10, 100));

        [Fact]
        public void CappedAt2500() => Assert.Equal(2500, MeleeKnockback.Knockback(99, 999, 0, 999, 999, 100), 3);

        [Theory]
        [InlineData(361, 20.0, true, 0.0)]    // Sakurai angle, grounded, weak: along the ground
        [InlineData(361, 60.0, true, 44.0)]
        [InlineData(361, 20.0, false, 45.0)]  // airborne
        [InlineData(80, 20.0, true, 80.0)]
        public void SakuraiAngle(int angle, double kb, bool grounded, double expect) =>
            Assert.Equal(expect, MeleeKnockback.LaunchAngle(angle, kb, grounded), 3);

        [Fact]
        public void SpeedAndHitstun() {
            Assert.Equal(3.0, MeleeKnockback.Speed(100), 6);    // 0.03 per point
            Assert.Equal(40, MeleeKnockback.Hitstun(100.9));    // floor(0.4 x kb)
        }

        [Fact]
        public void BossesKeepHollowKnightsRecoil() {
            var t = new KnockbackTuning();
            Assert.True(MeleeKnockback.Launches(t, maxHp: 20, width: 1, height: 1));
            Assert.False(MeleeKnockback.Launches(t, maxHp: 700, width: 1, height: 1));
            Assert.False(MeleeKnockback.Launches(t, maxHp: 20, width: 9, height: 1));
            Assert.False(MeleeKnockback.Launches(new KnockbackTuning { Enabled = false }, 20, 1, 1));
        }
    }
}

namespace FoxMelee.Tests {
    public class KnockbackFlightTests {
        [Fact]
        public void DecaysAlongItsDirectionThenEnds() {
            var f = new KnockbackFlight(speed: 1.0, angleDeg: 90, hitstun: 3);
            Assert.Equal(1.0, f.Vy, 6);
            Assert.True(f.Step());                 // frame 1 of hitstun
            Assert.Equal(1.0 - MeleeKnockback.Decay, f.Vy, 6);
            Assert.Equal(0, f.Vx, 6);
            Assert.True(f.Step()); Assert.True(f.Step());
            Assert.False(f.Step());                // hitstun over: the enemy acts again
        }

        [Fact]
        public void ATumbleEndsInAKnockdownOnceItLands() {
            var f = new KnockbackFlight(1.0, 45, hitstun: 2, tumble: true);
            Assert.True(f.Step(grounded: false)); Assert.True(f.Step(grounded: false));
            Assert.True(f.Step(grounded: false));             // hitstun over, still in the air: tumbling
            Assert.False(f.Down);
            for (int i = 0; i < KnockbackFlight.KnockdownFrames - 1; i++) { Assert.True(f.Step(grounded: true)); Assert.True(f.Down); }
            Assert.False(f.Step(grounded: true));             // gets up
        }

        [Fact]
        public void ATumbleThatNeverLandsEnds() {
            var f = new KnockbackFlight(1.0, 45, hitstun: 0, tumble: true);
            int n = 0;
            while (f.Step(grounded: false)) n++;
            Assert.Equal(KnockbackFlight.MaxTumbleFrames, n);
        }

        [Fact]
        public void NoTumbleEndsWithTheHitstunInTheAir() {
            var f = new KnockbackFlight(1.0, 45, hitstun: 1);
            Assert.True(f.Step(grounded: false));
            Assert.False(f.Step(grounded: false));
        }

        [Theory]
        [InlineData(80, 45, false, true)]     // Melee's tumble: knockback 80 and up
        [InlineData(79, 45, false, false)]
        [InlineData(40, 270, true, true)]     // driven down into the ground (a down throw): knocked down
        [InlineData(40, 270, false, false)]   // the same in the air: a weak spike, no tumble
        [InlineData(40, 0, true, false)]
        public void Tumbles(double kb, double angle, bool grounded, bool tumbles) =>
            Assert.Equal(tumbles, MeleeKnockback.Tumbles(kb, angle, grounded));

        [Fact]
        public void NeverReversesWhileDecaying() {
            var f = new KnockbackFlight(0.06, 0, 10);
            f.Step(); f.Step();
            Assert.Equal(0, f.Vx, 6);
        }
    }
}
