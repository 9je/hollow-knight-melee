using Xunit;

namespace FoxMelee.Tests {
    public class KnightBoxTests {
        // the Knight's own box: 0.6 wide, 1.2 tall, centred 0.6 above his transform (its bottom at his feet)
        const float W = 0.6f, H = 1.2f, Oy = 0.0f, Scale = 6f;

        static McHurtbox Capsule(float ax, float ay, float bx, float by, float r, uint state = 0) =>
            new McHurtbox { Ax = ax, Ay = ay, Bx = bx, By = by, Radius = r, State = state };

        [Fact]
        public void TheBoxSpansTheHurtboxesWithItsBottomAtTheFeet() {
            // a tall body: a capsule from the hips (y 20) to the head (y 50) with radius 10, at x 100 (Melee units)
            var boxes = new[] { Capsule(100, 20, 100, 50, 10), Capsule(96, 5, 104, 5, 5) };
            var fit = KnightBox.For(boxes, 2, 100, 0, Scale, W, H, Oy);
            Assert.Equal(20 / Scale, fit.Width, 3);           // radius 10 each side
            Assert.Equal(60 / Scale, fit.Height, 3);          // the head's top at 60
            Assert.Equal(Oy - H / 2 + fit.Height / 2, fit.OffsetY, 3);   // bottom where the Knight's was
        }

        [Fact]
        public void NeverSmallerThanTheKnightsOwn() {
            var boxes = new[] { Capsule(0, 1, 0, 2, 0.5f) };   // a tiny body
            var fit = KnightBox.For(boxes, 1, 0, 0, Scale, W, H, Oy);
            Assert.Equal(W, fit.Width, 3);
            Assert.Equal(H, fit.Height, 3);
            Assert.Equal(Oy, fit.OffsetY, 3);
        }

        [Fact]
        public void AHurtboxThatIsOffDoesNotCount() {
            var boxes = new[] { Capsule(0, 0, 0, 100, 30, state: 1), Capsule(0, 0, 0, 10, 1) };
            var fit = KnightBox.For(boxes, 2, 0, 0, Scale, W, H, Oy);
            Assert.Equal(11 / Scale, fit.Height, 3);
            Assert.Equal(W, fit.Width, 3);                     // 2 wide in Melee units is narrower than the Knight's own
        }

        [Fact]
        public void TheWiderSideSetsTheWidthAroundTheFighter() {
            // a sword held out to the right: the box stays centred on the fighter, wide enough for the reach both ways
            var boxes = new[] { Capsule(0, 10, 40, 10, 5) };
            var fit = KnightBox.For(boxes, 1, 0, 0, Scale, W, H, Oy);
            Assert.Equal(90 / Scale, fit.Width, 3);
        }
    }
}
