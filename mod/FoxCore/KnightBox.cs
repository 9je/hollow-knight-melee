using System;

namespace FoxMelee {
    /// The hidden Knight's box (his BoxCollider2D) while a character drives him. Hollow Knight tests that box, not the body it
    /// sees: spikes and enemies hurt through it, benches, doors, respawn markers and prompts trigger on it. The Knight's own
    /// is small (about 0.6 by 1.2); a Melee character's body is wider and often much taller, so a spike between Sheik's legs
    /// missed her and the bench's prompt needed a wiggle. The box becomes as wide and tall as the character's hurtboxes,
    /// never smaller than the Knight's, with its bottom at his feet as before, so everything of his that measures from the
    /// feet (HeroFeetOffset, the ground raycasts) stays right.
    public static class KnightBox {
        public struct Fit { public float Width, Height, OffsetY; }

        /// From the character's hurtboxes (Melee units, world; capsules a-b with a radius; state 1 is off) at the fighter's
        /// position: the box in Hollow Knight units, given the Knight's own box and the room's scale (Melee units per unit).
        public static Fit For(McHurtbox[] boxes, int n, float fighterX, float fighterY, float scale,
                              float knightWidth, float knightHeight, float knightOffsetY) {
            float left = 0, right = 0, top = 0;   // around the fighter: half-widths each side and the height above the feet
            for (int i = 0; i < n; i++) {
                var b = boxes[i];
                if (b.State == 1) continue;
                left = Math.Max(left, fighterX - Math.Min(b.Ax, b.Bx) + b.Radius);
                right = Math.Max(right, Math.Max(b.Ax, b.Bx) + b.Radius - fighterX);
                top = Math.Max(top, Math.Max(b.Ay, b.By) + b.Radius - fighterY);
            }
            float w = Math.Max(knightWidth, 2 * Math.Max(left, right) / scale);   // centred on the fighter: the Knight's x
            float h = Math.Max(knightHeight, top / scale);
            return new Fit { Width = w, Height = h, OffsetY = knightOffsetY - knightHeight / 2 + h / 2 };
        }

        /// The box held steady: Hollow Knight's camera zones (CameraLockArea) switch as the box enters and leaves them, so a
        /// box that breathes with an idle animation across a zone's edge shook the camera. It grows at once; it shrinks only
        /// once the body has been smaller for `frames` frames running, to the largest of those.
        public sealed class Held {
            readonly int frames;
            Fit held, smaller;
            int count = -1;
            public Held(int frames) { this.frames = frames; }

            public Fit Next(Fit f) {
                if (count < 0 || f.Width > held.Width || f.Height > held.Height) {
                    if (count < 0) held = f;
                    else held = Larger(held, f);
                    count = 0;
                    return held;
                }
                smaller = count == 0 ? f : Larger(smaller, f);
                if (++count >= frames) { held = smaller; count = 0; }
                return held;
            }

            static Fit Larger(Fit a, Fit b) {
                var tall = b.Height > a.Height ? b : a;
                return new Fit { Width = Math.Max(a.Width, b.Width), Height = tall.Height, OffsetY = tall.OffsetY };
            }
        }
    }
}
