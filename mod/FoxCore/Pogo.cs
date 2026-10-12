namespace FoxMelee {
    /// Spec §5.4 (the one deliberate exception to Melee): Fox's down-air on spikes bounces him like a bumper, with his
    /// double jump back, and that touch doesn't hurt him.
    public sealed class PogoTuning {
        public bool Enabled = true;
        public double BounceSpeed = 3.5;  // Melee units per frame upward (about a full hop; Fox's full jump is 3.68)
        public int GraceFrames = 8;       // Melee frames after a bounce in which spikes don't hurt Fox
    }

    public sealed class Pogo {
        const uint AttackAirLw = 69;
        readonly PogoTuning tuning;
        int grace;

        public Pogo(PogoTuning tuning) { this.tuning = tuning; }

        /// The bounce's grace: spikes touching the Knight's body now are the ones Fox just bounced off.
        public bool SpikesIgnored => grace > 0;

        /// Each Melee frame: Fox's action, vertical speed, and whether a down-air hitbox touches spikes. True: bounce now.
        public bool Frame(uint action, float velY, bool touchingSpikes) {
            if (grace > 0) grace--;
            if (!tuning.Enabled || action != AttackAirLw || !touchingSpikes || velY > 0) return false;
            grace = tuning.GraceFrames;
            return true;
        }
    }
}
