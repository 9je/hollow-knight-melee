namespace FoxMelee {
    public enum HeroOwner { Hollow, Fox }

    /// The Knight's state as FoxRunner reads it from HeroController each frame.
    public struct HeroFlags {
        public bool Transitioning, WaitingToTransition, HazardRespawning, HazardDeath, Dead;
        public bool AcceptingInput, ControlRelinquished;
        public bool Loading;                    // the game is not playing yet (a save loading, a cutscene)
        public bool Respawning;                 // HeroController.Respawn / HazardRespawn still running (they clear
                                                // dead / hazardDeath at their start, then wake the Knight up)
    }

    /// After a respawn Fox ignores the controller until it is let go once: a stick still held from before the hit
    /// would otherwise dash him straight back into the spikes (the Knight's respawn animation gives the player that
    /// moment; Melee would act on the first frame).
    public sealed class RespawnHold {
        public const int MaxFrames = 60;        // at most a second: a controller that never reads as let go can't lock Fox
        int left;
        public bool Armed => left > 0;
        public void Arm() => left = MaxFrames;
        public McInput Filter(McInput i) {
            if (left <= 0) return i;
            if (i.Buttons != 0 || Far(i.StickX) || Far(i.StickY) || Far(i.CStickX) || Far(i.CStickY) || i.TriggerL > 42 || i.TriggerR > 42) {
                left--;
                return new McInput();
            }
            left = 0;
            return i;
        }
        static bool Far(sbyte v) => v > 22 || v < -22;   // Melee's dead zone (0.2875 of 80)
    }

    /// Who moves the Knight, and what FoxRunner must do to him. Pure, so the hand-over rules are unit-tested;
    /// the Unity calls live in FoxRunner.
    public sealed class FoxSession {
        string roomScene;
        bool roomReady, took, respawn;
        int interact = -1;                      // frames since BeginInteract; -1 = not interacting
        bool interactStarted;                   // Hollow Knight took control (a tablet, an NPC, a bench)

        /// How long Hollow Knight gets to react to an interact press before Fox carries on.
        public const int InteractWaitFrames = 10;

        public bool Interacting => interact >= 0;

        /// The player pressed interact: the Knight goes back to Hollow Knight, which presses "up" for him.
        public void BeginInteract() { interact = 0; interactStarted = false; }

        /// Hollow Knight is moving the Knight itself (room transition, spikes, hazard respawn, death, loading a save:
        /// until the game plays, the Knight is parked far outside the room).
        public static bool HollowBusy(HeroFlags f) =>
            f.Respawning || f.Loading || f.Transitioning || !f.WaitingToTransition || f.HazardRespawning || f.HazardDeath || f.Dead;

        public bool NeedsRoom(string scene) => scene != roomScene;

        /// Records the conversion of a scene's terrain. Returns true when the Knight Fox had taken must be
        /// given back (the room failed): the player plays the Knight there until a room converts again.
        public bool RoomConverted(string scene, bool ok) {
            roomScene = scene;
            roomReady = ok;
            if (ok || !took) return false;
            took = false;
            return true;
        }

        public HeroOwner Owner(HeroFlags f) {
            if (f.HazardDeath || f.HazardRespawning || f.Dead || f.Respawning) respawn = true;
            if (interact >= 0) {
                if (f.ControlRelinquished || !f.AcceptingInput) interactStarted = true;
                bool done = interactStarted ? !f.ControlRelinquished && f.AcceptingInput : interact >= InteractWaitFrames;
                if (!done) { interact++; return HeroOwner.Hollow; }
                interact = -1;
            }
            return roomReady && !HollowBusy(f) ? HeroOwner.Fox : HeroOwner.Hollow;
        }

        bool placing;
        /// Fox was placed (mc_place/respawn_fighter): it lands at Melee's next frame, and until then Melee still has him
        /// where he was (after spikes: on them). The Knight must not be moved there.
        public void Placed() => placing = true;
        public void Stepped() => placing = false;
        public bool KnightFollowsFox => !placing;

        /// When Fox next takes the Knight: true after spikes or death (Fox comes back still, as the Knight does),
        /// false after a door or transition (Fox keeps his momentum). Consumed by the call.
        public bool TakeRespawns() {
            bool r = respawn;
            respawn = false;
            return r;
        }

        /// While Fox drives: block the Knight's input every frame (FinishedEnteringScene calls AcceptInput again
        /// but leaves controlReqlinquished set, so RelinquishControl alone would not), relinquish once.
        public (bool IgnoreInput, bool Relinquish) Take(HeroFlags f) {
            took = true;
            return (f.AcceptingInput, !f.ControlRelinquished);
        }

        /// Hollow Knight needs the Knight's normal body for transitions and respawns; on death it makes the
        /// body kinematic itself, so leave it alone.
        public bool ReturnBody(HeroFlags f) => !f.Dead;

        /// The Knight is visible only when there is no usable room (the player plays him).
        public bool KnightVisible(HeroFlags f) => !roomReady;

        /// Returns true when the Knight must be released (only if Fox took him).
        public bool Fail() {
            bool r = took;
            took = false;
            roomReady = false;
            return r;
        }
    }
}
