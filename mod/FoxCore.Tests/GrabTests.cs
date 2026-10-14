using System.Collections.Generic;
using Xunit;

namespace FoxMelee.Tests {
    public class GrabTests {
        static readonly GrabTuning T = new GrabTuning();
        static GrabCandidate C(int id, double x, double w = 1, double h = 1, int hp = 20) =>
            new GrabCandidate { Id = id, X = x, Y = 0, Width = w, Height = h, Hp = hp, MaxHp = 20 };

        [Fact]
        public void PicksNearestSmallEnemyInRange() {
            var c = new List<GrabCandidate> { C(1, 2.5), C(2, -1.0), C(3, 0.5, w: 5), C(5, 9) };
            Assert.Equal(2, GrabTargets.Pick(c, 0, 0, -1, T));
        }

        [Fact]
        public void ToughEnemiesAreNotGrabbed() {
            var boss = new GrabCandidate { Id = 4, X = 0.2, Width = 1, Height = 1, Hp = 900, MaxHp = 900 };
            Assert.Equal(-1, GrabTargets.Pick(new List<GrabCandidate> { boss }, 0, 0, -1, T));
        }

        [Fact]
        public void KeepsCurrentTargetWhileEligible() {
            var c = new List<GrabCandidate> { C(1, 2.5), C(2, -1.0) };
            Assert.Equal(1, GrabTargets.Pick(c, 0, 0, 1, T));
            Assert.Equal(2, GrabTargets.Pick(new List<GrabCandidate> { C(2, -1.0) }, 0, 0, 1, T));
        }

        [Fact]
        public void NoneInRange() => Assert.Equal(-1, GrabTargets.Pick(new List<GrabCandidate> { C(1, 6) }, 0, 0, -1, T));

        [Theory]
        [InlineData(20, 20, 0)]
        [InlineData(10, 20, 50)]
        [InlineData(0, 20, 100)]
        public void PercentFollowsHp(int hp, int max, double percent) =>
            Assert.Equal(percent, GrabTargets.Percent(new GrabCandidate { Hp = hp, MaxHp = max }, T), 3);

        [Fact]
        public void WakesShadowsAndSleeps() {
            var g = new GrabMachine();
            Assert.Equal(GrabCommand.None, g.Before(-1));
            Assert.Equal(GrabCommand.Wake, g.Before(7));
            Assert.Equal(GrabMachine.Phase.Shadowing, g.State);
            Assert.Equal(GrabEvent.None, g.After(new GrabSeen(), out _));
            Assert.Equal(GrabCommand.Place, g.Before(7));
            Assert.Equal(GrabCommand.Place, g.Before(8));
            Assert.Equal(8, g.TargetId);
            Assert.Equal(GrabCommand.Sleep, g.Before(-1));
            Assert.Equal(GrabMachine.Phase.Asleep, g.State);
        }

        [Fact]
        public void GrabPummelThrow() {
            var g = new GrabMachine();
            g.Before(7);
            Assert.Equal(GrabEvent.Grabbed, g.After(new GrabSeen { Held = true, Percent = 10 }, out _));
            Assert.Equal(GrabCommand.None, g.Before(7));   // Melee moves it now
            Assert.Equal(GrabEvent.None, g.After(new GrabSeen { Held = true, Percent = 10 }, out _));
            Assert.Equal(GrabEvent.Pummel, g.After(new GrabSeen { Held = true, Percent = 13 }, out var d));
            Assert.Equal(3f, d);
            Assert.Equal(GrabEvent.Thrown, g.After(new GrabSeen { Held = false, FoxAction = 0xDB, Percent = 20, KbX = 2, KbY = 1 }, out d));
            Assert.Equal(7f, d);
            Assert.Equal(GrabCommand.Sleep, g.Before(7));   // the enemy flies on its own; a fresh wake next frame
            Assert.Equal(GrabCommand.Wake, g.Before(7));
        }

        [Fact]
        public void LinkLostWithoutThrowIsEscape() {
            var g = new GrabMachine();
            g.Before(7);
            g.After(new GrabSeen { Held = true }, out _);
            Assert.Equal(GrabEvent.Escaped, g.After(new GrabSeen { Held = false, FoxAction = 0xDA }, out _));
            Assert.Equal(GrabMachine.Phase.Shadowing, g.State);
            Assert.Equal(GrabCommand.Place, g.Before(7));
        }

        [Fact]
        public void TargetGoneWhileHeldReleases() {
            var g = new GrabMachine();
            g.Before(7);
            g.After(new GrabSeen { Held = true }, out _);
            Assert.Equal(GrabEvent.Escaped, g.After(new GrabSeen { Held = true, TargetGone = true }, out _));
            Assert.Equal(GrabCommand.Sleep, g.Before(-1));
        }

        [Fact]
        public void ResetSleeps() {
            var g = new GrabMachine();
            g.Before(7);
            g.After(new GrabSeen { Held = true }, out _);
            g.Reset();
            Assert.Equal(GrabCommand.Sleep, g.Before(7));
            Assert.Equal(GrabMachine.Phase.Asleep, g.State);
        }
    }
}

namespace FoxMelee.Tests {
    public class GrabHoldTests {
        // DK's cargo (any grab): a weak enemy is carried for a minute and can't do a thing about it; a strong one struggles
        // out soon; a hurt enemy of either kind holds longer than it did fresh
        [Theory]
        [InlineData(1, 3600)]
        [InlineData(8, 3600)]
        [InlineData(70, 45)]
        [InlineData(500, 45)]
        public void HeldByHealth(int hp, double frames) => Assert.Equal(frames, GrabTargets.HoldFrames(hp, new GrabTuning()), 3);

        [Fact]
        public void BetweenWeakAndStrongTheHoldShortensWithHealth() {
            var t = new GrabTuning();
            double a = GrabTargets.HoldFrames(20, t), b = GrabTargets.HoldFrames(40, t);
            Assert.True(t.HoldFramesWeak > a && a > b && b > t.HoldFramesStrong);
        }
    }
}
