using System.Collections.Generic;

namespace FoxMelee {
    /// Plan 7 (spec §5 Grab): Fox's grab and Melee's push against small enemies, through a stand-in fighter.
    public sealed class GrabTuning {
        public bool Enabled = true;
        public double WakeRangeX = 3.0, WakeRangeY = 2.0;   // HK units around Fox where an enemy wakes the stand-in
        public double MaxWidth = 3.0, MaxHeight = 3.0;      // HK units: bigger enemies can't be grabbed
        public int MaxHp = 200;                             // tougher enemies (bosses) can't be grabbed
        public double PercentAtFullHp = 0, PercentAtNoHp = 100;   // the stand-in's percent: throws launch weak enemies further
        public double LaunchScale = 1.0;                    // thrown enemy's velocity = Melee knockback velocity x this
        // how long a grabbed enemy stays held (Melee frames) before it struggles out, by its health: a weak one is carried
        // for a minute (and can't do a thing about it), a strong one is out soon; between, the stronger the sooner
        public double HoldFramesWeak = 3600, HoldFramesStrong = 45;
        public int HoldWeakHp = 8, HoldStrongHp = 70;
    }

    public struct GrabCandidate { public int Id; public double X, Y, Width, Height; public int Hp, MaxHp; }   // X, Y: feet, HK units

    public static class GrabTargets {
        /// The enemy the stand-in follows: within the wake range of Fox, small and weak enough; the current one while it
        /// still qualifies, else the nearest. -1: none.
        public static int Pick(IReadOnlyList<GrabCandidate> c, double foxX, double foxY, int currentId, GrabTuning t) {
            int best = -1; double bestD = double.MaxValue;
            foreach (var e in c) {
                if (!Eligible(e, foxX, foxY, t)) continue;
                if (e.Id == currentId) return e.Id;
                double d = System.Math.Abs(e.X - foxX);
                if (d < bestD) { bestD = d; best = e.Id; }
            }
            return best;
        }

        static bool Eligible(GrabCandidate e, double fx, double fy, GrabTuning t) =>
            e.Width <= t.MaxWidth && e.Height <= t.MaxHeight && e.Hp > 0 && e.MaxHp <= t.MaxHp
            && System.Math.Abs(e.X - fx) <= t.WakeRangeX && System.Math.Abs(e.Y - fy) <= t.WakeRangeY;

        /// The stand-in's percent: an enemy low on health flies further when thrown.
        /// Melee frames a grab holds this enemy (its health now: a hurt enemy struggles less), HoldFramesWeak up to
        /// HoldWeakHp, HoldFramesStrong from HoldStrongHp, a straight line between.
        public static double HoldFrames(int hp, GrabTuning t) {
            if (t.HoldStrongHp <= t.HoldWeakHp) return hp <= t.HoldWeakHp ? t.HoldFramesWeak : t.HoldFramesStrong;
            double f = System.Math.Max(0, System.Math.Min(1, (double)(hp - t.HoldWeakHp) / (t.HoldStrongHp - t.HoldWeakHp)));
            return t.HoldFramesWeak + (t.HoldFramesStrong - t.HoldFramesWeak) * f;
        }

        public static double Percent(GrabCandidate c, GrabTuning t) {
            double f = c.MaxHp > 0 ? System.Math.Max(0, System.Math.Min(1, (double)c.Hp / c.MaxHp)) : 1;
            return t.PercentAtNoHp + (t.PercentAtFullHp - t.PercentAtNoHp) * f;
        }
    }

    public enum GrabCommand { None, Wake, Place, Sleep }
    public enum GrabEvent { None, Grabbed, Pummel, Thrown, Escaped }
    public struct GrabSeen { public bool Held, TargetGone; public uint FoxAction; public float Percent, KbX, KbY; }

    /// The stand-in's life: asleep; shadowing an enemy (placed at it every frame); held in Fox's grab.
    public sealed class GrabMachine {
        public enum Phase { Asleep, Shadowing, Held }
        const uint ThrowFirst = 0xDB, ThrowLast = 0xDE;   // ThrowF, ThrowB, ThrowHi, ThrowLw
        public Phase State { get; private set; }
        public int TargetId { get; private set; } = -1;
        bool sleepNext;
        float mark;   // the stand-in's percent at the last damage reported

        /// Before a Melee frame: what to do with the stand-in. targetId -1: no enemy to follow.
        public GrabCommand Before(int targetId) {
            if (sleepNext) { sleepNext = false; State = Phase.Asleep; TargetId = -1; return GrabCommand.Sleep; }
            switch (State) {
                case Phase.Asleep:
                    if (targetId < 0) return GrabCommand.None;
                    State = Phase.Shadowing; TargetId = targetId; return GrabCommand.Wake;
                case Phase.Shadowing:
                    if (targetId < 0) { State = Phase.Asleep; TargetId = -1; return GrabCommand.Sleep; }
                    TargetId = targetId; return GrabCommand.Place;
                default: return GrabCommand.None;   // held: Melee moves it
            }
        }

        /// After the frame: what Melee did to it. damage: percent it took (pummel, throw).
        public GrabEvent After(GrabSeen seen, out float damage) {
            damage = 0;
            if (State == Phase.Shadowing && seen.Held) { State = Phase.Held; mark = seen.Percent; return GrabEvent.Grabbed; }
            if (State != Phase.Held) return GrabEvent.None;
            if (seen.TargetGone) { sleepNext = true; return GrabEvent.Escaped; }
            if (seen.Held) {
                if (seen.Percent <= mark + 1e-3f) return GrabEvent.None;
                damage = seen.Percent - mark; mark = seen.Percent; return GrabEvent.Pummel;
            }
            if (seen.FoxAction >= ThrowFirst && seen.FoxAction <= ThrowLast) {
                damage = System.Math.Max(0, seen.Percent - mark); sleepNext = true; return GrabEvent.Thrown;
            }
            State = Phase.Shadowing; return GrabEvent.Escaped;
        }

        /// Hollow Knight took the Knight, a new room: the stand-in sleeps at the next Before.
        public void Reset() { if (State != Phase.Asleep) sleepNext = true; else TargetId = -1; }
    }
}
