namespace FoxMelee {
    public enum HeroOwner { Hollow, Fox }

    /// The Knight's state as FoxRunner reads it from HeroController each frame.
    public struct HeroFlags {
        public bool Transitioning, WaitingToTransition, HazardRespawning, HazardDeath, Dead;
        public bool AcceptingInput, ControlRelinquished;
    }

    /// Who moves the Knight, and what FoxRunner must do to him. Pure, so the hand-over rules are unit-tested;
    /// the Unity calls live in FoxRunner.
    public sealed class FoxSession {
        string roomScene;
        bool roomReady, took;

        /// Hollow Knight is moving the Knight itself (room transition, spikes, hazard respawn, death).
        public static bool HollowBusy(HeroFlags f) =>
            f.Transitioning || !f.WaitingToTransition || f.HazardRespawning || f.HazardDeath || f.Dead;

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

        public HeroOwner Owner(HeroFlags f) => roomReady && !HollowBusy(f) ? HeroOwner.Fox : HeroOwner.Hollow;

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
