using System.Collections.Generic;
using Xunit;

namespace FoxMelee.Tests {
    public class HitlagTimersTests {
        [Fact]
        public void AVictimStaysFrozenForTheHitlagFramesAfterTheHit() {
            var t = new HitlagTimers(); var enemy = new object();
            Assert.True(t.Freeze(enemy, 4));                  // first freeze: the caller freezes it
            for (int i = 0; i < 3; i++) Assert.Empty(t.Tick());
            Assert.Equal(new[] { enemy }, t.Tick());           // released after the 4th frame
            Assert.Empty(t.Tick());
            Assert.False(t.IsFrozen(enemy));
        }

        [Fact]
        public void AHitDuringHitlagRestartsIt() {
            var t = new HitlagTimers(); var enemy = new object();
            t.Freeze(enemy, 4);
            t.Tick(); t.Tick();
            Assert.False(t.Freeze(enemy, 5));                 // already frozen: only the count changes
            for (int i = 0; i < 4; i++) Assert.Empty(t.Tick());
            Assert.Single(t.Tick());
        }

        [Fact]
        public void NoHitlagFreezesNothing() {
            var t = new HitlagTimers(); var enemy = new object();
            Assert.False(t.Freeze(enemy, 0));
            Assert.False(t.IsFrozen(enemy));
        }

        [Fact]
        public void ReleaseAllEmptiesIt() {
            var t = new HitlagTimers(); object a = new object(), b = new object();
            t.Freeze(a, 3); t.Freeze(b, 9);
            Assert.Equal(2, new List<object>(t.ReleaseAll()).Count);
            Assert.Empty(t.Tick());
        }
    }
}
