namespace FoxMelee {
    /// Spec §5.4 (the one deliberate exception to Melee): a down-air on spikes bounces the character like the Knight's nail
    /// does him, to the same height whoever it is, the swing playing on, the double jump back, and that touch doesn't hurt.
    public sealed class PogoTuning {
        public bool Enabled = true;
        public double BounceHeight = 2.5;  // how high the bounce peaks, Hollow Knight units, the same for every character (the Knight's nail bounce: about half his jump)
        public int GraceFrames = 8;       // Melee frames after a bounce in which spikes don't hurt Fox
        public bool Shrooms = true;       // Fungal Wastes' bounce mushrooms: falling onto one bounces (BounceHeight), a down-air on one bounces high
        public double ShroomBounceHeight = 6.5;   // the Knight's down-slash on a mushroom (SHROOM_BOUNCE_VELOCITY), Hollow Knight units
    }

    /// The bounce mushrooms (BounceShroom): the Knight landing on an active one bounces as his nail's pogo does, his
    /// down-slash on any (the component is also just a flag) sends him much higher. A character falling onto one (its feet about to touch) bounces the same; a down-air touching
    /// one bounces high. Once per fall: the bounce sends it up, and only falling again counts.
    public static class ShroomBounce {
        public const uint AttackAirLw = 69;
        /// The height to bounce to now (Hollow Knight units), or 0.
        public static double Height(PogoTuning t, bool airborne, float velY, uint action, bool feetOnShroom, bool downAirOnShroom) {
            if (!t.Enabled || !t.Shrooms || !airborne || velY > 0) return 0;
            if (action == AttackAirLw && (downAirOnShroom || feetOnShroom)) return t.ShroomBounceHeight;
            return feetOnShroom ? t.BounceHeight : 0;
        }
    }

    public sealed class Pogo {
        const uint AttackAirLw = 69;
        readonly PogoTuning tuning;
        int grace;
        bool bounced;   // this down-air bounced already: one bounce per swing, as the Knight's nail (Marth's long one fell back on the spikes)

        public Pogo(PogoTuning tuning) { this.tuning = tuning; }

        /// The bounce's grace: spikes touching the Knight's body now are the ones Fox just bounced off.
        public bool SpikesIgnored => grace > 0;

        /// Each Melee frame: Fox's action, vertical speed, and whether a down-air hitbox touches spikes. True: bounce now.
        public bool Frame(uint action, float velY, bool touchingSpikes) {
            if (grace > 0) grace--;
            if (action != AttackAirLw) bounced = false;
            if (!tuning.Enabled || action != AttackAirLw || bounced || !touchingSpikes || velY > 0) return false;
            grace = tuning.GraceFrames;
            bounced = true;
            return true;
        }
    }
}
