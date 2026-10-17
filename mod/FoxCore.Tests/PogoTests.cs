using Xunit;

namespace FoxMelee.Tests {
    public class PogoTests {
        const uint Dair = 69, Nair = 65;

        [Fact]
        public void DownAirOnSpikesWhileFallingBounces() {
            var p = new Pogo(new PogoTuning());
            Assert.True(p.Frame(Dair, -1.5f, true));
            Assert.True(p.SpikesIgnored);
        }

        [Theory]
        [InlineData(Nair, -1.5f, true)]    // only the down-air
        [InlineData(Dair, 1.0f, true)]     // rising (just bounced): no second bounce
        [InlineData(Dair, -1.5f, false)]   // nothing under it
        public void NoBounce(uint action, float vy, bool spikes) => Assert.False(new Pogo(new PogoTuning()).Frame(action, vy, spikes));

        [Fact]
        public void SpikesHurtAgainAfterTheGrace() {
            var t = new PogoTuning { GraceFrames = 3 };
            var p = new Pogo(t);
            p.Frame(Dair, -1f, true);
            for (int i = 0; i < 3; i++) { Assert.True(p.SpikesIgnored); p.Frame(Dair, 1f, false); }
            Assert.False(p.SpikesIgnored);
        }

        [Fact]
        public void OneSwingBouncesOnce() {   // Marth's long down-air fell back onto the spikes and bounced twice
            var p = new Pogo(new PogoTuning { GraceFrames = 0 });
            Assert.True(p.Frame(Dair, -1.5f, true));
            for (int i = 0; i < 10; i++) p.Frame(Dair, 1f, false);   // up
            Assert.False(p.Frame(Dair, -1.5f, true));                 // down again, the same swing still playing
            p.Frame(Nair, -1.5f, false);                              // the swing ended (any other action)
            Assert.True(p.Frame(Dair, -1.5f, true));                  // a new down-air bounces again
        }

        [Fact]
        public void OffNeverBounces() => Assert.False(new Pogo(new PogoTuning { Enabled = false }).Frame(Dair, -1f, true));

        [Fact]
        public void AMushroomBouncesAFallOntoItAndADownAirHigh() {
            var t = new PogoTuning();
            Assert.Equal(t.BounceHeight, ShroomBounce.Height(t, true, -2f, 0, feetOnShroom: true, downAirOnShroom: false));
            Assert.Equal(t.ShroomBounceHeight, ShroomBounce.Height(t, true, -2f, ShroomBounce.AttackAirLw, false, true));
            Assert.Equal(t.ShroomBounceHeight, ShroomBounce.Height(t, true, -2f, ShroomBounce.AttackAirLw, true, false));
            Assert.Equal(0, ShroomBounce.Height(t, true, 1f, 0, true, false));     // rising (just bounced): once per fall
            Assert.Equal(0, ShroomBounce.Height(t, false, 0f, 0, true, false));    // standing beside one
            Assert.Equal(0, ShroomBounce.Height(t, true, -2f, 0, false, true));    // only a down-air uses the nail's flag
            t.Shrooms = false;
            Assert.Equal(0, ShroomBounce.Height(t, true, -2f, ShroomBounce.AttackAirLw, true, true));
        }
    }
}
