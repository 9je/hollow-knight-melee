using InControl;

namespace FoxMelee {
    /// Presses Hollow Knight's "up" (its interact button: tablets, NPCs, benches) for a few frames, whatever controller
    /// the player uses: an InControl device whose D-pad up Hollow Knight's own bindings already read.
    sealed class HkPulse : InputDevice {
        const int PressFrames = 3;
        static HkPulse instance;
        int left;

        HkPulse() : base("FoxMelee interact") { AddControl(InputControlType.DPadUp, "Interact"); }

        public static void Attach() {
            if (instance != null) return;
            instance = new HkPulse();
            InputManager.AttachDevice(instance);
        }

        public static void PressUp() { if (instance != null) instance.left = PressFrames; }

        public override void Update(ulong tick, float dt) {
            UpdateWithState(InputControlType.DPadUp, left > 0, tick, dt);
            if (left > 0) left--;
        }
    }
}
