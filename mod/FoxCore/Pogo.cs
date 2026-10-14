namespace FoxMelee {
    /// Spec §5.4 (the one deliberate exception to Melee): a down-air on spikes bounces the character like the Knight's nail
    /// does him, to the same height whoever it is, the swing playing on, the double jump back, and that touch doesn't hurt.
    public sealed class PogoTuning {
        public bool Enabled = true;
        public double BounceHeight = 2.5;  // how high the bounce peaks, Hollow Knight units, the same for every character (the Knight's nail bounce: about half his jump)
        public int GraceFrames = 8;       // Melee frames after a bounce in which spikes don't hurt Fox
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
