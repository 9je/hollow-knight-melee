using System;

namespace FoxMelee {
    /// Enemies launched by Fox's hits with Melee's knockback (replacing Hollow Knight's small recoil), so they can be comboed.
    public sealed class KnockbackTuning {
        public bool Enabled = true;
        public double Weight = 100;                          // Melee weight of every enemy (Fox is 75, Mario 100)
        public double PercentAtFullHp = 0, PercentAtNoHp = 100;   // the enemy's "percent", from its health (as grabs)
        public int MaxHp = 200;                              // tougher enemies (bosses) keep Hollow Knight's recoil
        public double MaxWidth = 4.0, MaxHeight = 4.0;       // HK units: so do bigger ones
        public double Scale = 1.0;                           // launch speed x this
    }

    /// Melee's knockback (ftColl_80079EA8 with PlCo's constants: 1.4, 18, 200/(w+100), p/10 + p*d/20, cap 2500), its
    /// launch speed (0.03 per point, decaying 0.051 per frame), hitstun (0.4 per point) and the Sakurai angle (361).
    public static class MeleeKnockback {
        public const int Sakurai = 361;
        public const double Decay = 0.051;   // Melee units per frame, each frame

        public static double Knockback(double damage, int growth, int setKb, int baseKb, double percentAfter, double weight) {
            double inner = setKb != 0 ? 10 * 0.1 + 0.05 * (10 * setKb) : 0.1 * percentAfter + 0.05 * (damage * percentAfter);
            double kb = 0.01 * growth * (1.4 * ((2.0 - 2.0 * (weight * 0.01) / (1.0 + weight * 0.01)) * inner) + 18) + baseKb;
            return Math.Min(kb, 2500);
        }

        /// The launch angle in degrees (Melee's, before mirroring for a victim on Fox's left). 361: along the ground for a
        /// weak hit on a grounded victim (44 degrees from knockback 32.1 up), 45 in the air.
        public static double LaunchAngle(int angle, double kb, bool grounded) {
            if (angle != Sakurai) return angle;
            if (!grounded) return 45;
            if (kb < 32.0) return 0;
            return kb >= 32.1 ? 44 : (kb - 32.0) / 0.1 * 44;
        }

        /// Melee's tumble (knockback 80 and up), which ends in a knockdown on landing; a grounded victim driven down into the
        /// ground (a down throw) is knocked down too.
        public static bool Tumbles(double kb, double angleDeg, bool grounded) {
            if (kb >= 80) return true;
            double a = ((angleDeg % 360) + 360) % 360;
            return grounded && a > 180 && a < 360;
        }

        public static double Speed(double kb) => kb * 0.03;
        public static int Hitstun(double kb) => (int)Math.Floor(kb * 0.4);

        public static bool Launches(KnockbackTuning t, int maxHp, double width, double height) =>
            t.Enabled && maxHp <= t.MaxHp && width <= t.MaxWidth && height <= t.MaxHeight;

        public static double Percent(KnockbackTuning t, int hp, int maxHp) {
            double f = maxHp > 0 ? Math.Max(0, Math.Min(1, (double)hp / maxHp)) : 1;
            return t.PercentAtNoHp + (t.PercentAtFullHp - t.PercentAtNoHp) * f;
        }
    }
}

namespace FoxMelee {
    /// One launched enemy's knockback velocity (Melee units per frame), decaying 0.051 per frame along its direction as
    /// Melee's does, and its hitstun. A tumble goes on until the enemy lands, then it lies down for KnockdownFrames (a
    /// missed tech: Melee's DownBound and getting up) before acting.
    public sealed class KnockbackFlight {
        public const int KnockdownFrames = 45;
        public const int MaxTumbleFrames = 180;   // one that never lands (a flyer, a pit) acts again after this
        double speed;
        readonly double cos, sin;
        readonly bool tumble;
        int hitstun, tumbling, down = -1;
        public double Vx => speed * cos;
        public double Vy => speed * sin;
        public bool Down => down >= 0;

        public KnockbackFlight(double speed, double angleDeg, int hitstun, bool tumble = false) {
            this.speed = speed; this.hitstun = hitstun; this.tumble = tumble;
            cos = System.Math.Cos(angleDeg * System.Math.PI / 180); sin = System.Math.Sin(angleDeg * System.Math.PI / 180);
            if (System.Math.Abs(cos) < 1e-9) cos = 0;
            if (System.Math.Abs(sin) < 1e-9) sin = 0;
        }

        /// One Melee frame: the knockback decays; false once the enemy can act again (hitstun over; a tumble landed and got up).
        public bool Step(bool grounded = true) {
            speed = System.Math.Max(0, speed - MeleeKnockback.Decay);
            if (hitstun-- > 0) return true;
            if (!tumble) return false;
            if (down < 0) {
                if (!grounded) return tumbling++ < MaxTumbleFrames;
                down = KnockdownFrames;
                speed = 0;
            }
            return --down > 0;
        }
    }
}
