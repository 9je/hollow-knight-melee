namespace FoxMelee {
    public enum HeroOwner { Hollow, Fox }

    /// The Knight's state as FoxRunner reads it from HeroController each frame.
    public struct HeroFlags {
        public bool Transitioning, WaitingToTransition, HazardRespawning, HazardDeath, Dead;
        public bool AcceptingInput, ControlRelinquished;
        public bool Loading;                    // the game is not playing yet (a save loading, a cutscene)
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
            f.Loading || f.Transitioning || !f.WaitingToTransition || f.HazardRespawning || f.HazardDeath || f.Dead;

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
            if (f.HazardDeath || f.HazardRespawning || f.Dead) respawn = true;
            if (interact >= 0) {
                if (f.ControlRelinquished || !f.AcceptingInput) interactStarted = true;
                bool done = interactStarted ? !f.ControlRelinquished && f.AcceptingInput : interact >= InteractWaitFrames;
                if (!done) { interact++; return HeroOwner.Hollow; }
                interact = -1;
            }
            return roomReady && !HollowBusy(f) ? HeroOwner.Fox : HeroOwner.Hollow;
        }

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
