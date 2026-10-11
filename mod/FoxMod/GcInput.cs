using InControl;
using UnityEngine;

namespace FoxMelee {
    /// The GameCube controller through a Wii U / Mayflash adapter, read natively over USB as Slippi Dolphin does.
    /// Fox gets its values unmapped (Melee sees what a GameCube would give it); Hollow Knight sees the same controller
    /// as an InControl device for its menus, pausing (Start) and any room played as the Knight.
    sealed class GcInput {
        readonly GcAdapter adapter = new GcAdapter();
        readonly GcPads pads = new GcPads();
        readonly int port;
        string lastStatus;

        public GcInput(int port) {
            this.port = port;
            if (!adapter.Start()) { FoxMod.Instance.LogWarn("GameCube adapter off: " + adapter.Status); return; }
            InputManager.AttachDevice(new GcDevice(this));
        }

        /// Reads the newest adapter report (call before each Melee frame and each InControl update).
        public void Poll() {
            pads.Update(adapter.Latest());
            if (adapter.Status != lastStatus) { lastStatus = adapter.Status; FoxMod.Instance.Log("GameCube adapter: " + lastStatus); }
        }

        public McInput? Current() => pads.Current(port);

        /// While playing, D-pad left/right are Fox's interact: Hollow Knight must not read them as walking (an interact
        /// press hands it the Knight for a few frames). Menus and the pause screen keep them.
        public static bool HideDPadSides;

        /// Fox's input for the next Melee frame, or null when no GameCube controller is plugged in.
        public McInput? Melee() { Poll(); return pads.Melee(port); }

        public void Dispose() => adapter.Dispose();

        internal sealed class GcDevice : InputDevice {
            readonly GcInput gc;

            public GcDevice(GcInput gc) : base("GameCube Controller (adapter)") {
                this.gc = gc;
                // a style Hollow Knight knows (it re-maps and logs an error for unknown ones); A confirms, B goes back
                DeviceStyle = InputDeviceStyle.XboxOne;
                foreach (var t in new[] {
                    InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp, InputControlType.LeftStickDown,
                    InputControlType.RightStickLeft, InputControlType.RightStickRight, InputControlType.RightStickUp, InputControlType.RightStickDown,
                    InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
                    InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
                    InputControlType.LeftTrigger, InputControlType.RightTrigger, InputControlType.RightBumper,
                    InputControlType.Start, InputControlType.Command,
                }) AddControl(t, t.ToString());
            }

            public override void Update(ulong tick, float dt) {
                gc.Poll();
                var v = gc.Current() ?? default(McInput);
                bool B(ushort b) => (v.Buttons & b) != 0;
                UpdateLeftStickWithValue(new Vector2(v.StickX / 127f, v.StickY / 127f), tick, dt);
                UpdateRightStickWithValue(new Vector2(v.CStickX / 127f, v.CStickY / 127f), tick, dt);
                UpdateWithState(InputControlType.DPadUp, B(GcButtons.DUp), tick, dt);
                UpdateWithState(InputControlType.DPadDown, B(GcButtons.DDown), tick, dt);
                UpdateWithState(InputControlType.DPadLeft, B(GcButtons.DLeft) && !HideDPadSides, tick, dt);
                UpdateWithState(InputControlType.DPadRight, B(GcButtons.DRight) && !HideDPadSides, tick, dt);
                UpdateWithState(InputControlType.Action1, B(GcButtons.A), tick, dt);     // A: confirm / jump
                UpdateWithState(InputControlType.Action2, B(GcButtons.B), tick, dt);     // B: back
                UpdateWithState(InputControlType.Action3, B(GcButtons.X), tick, dt);
                UpdateWithState(InputControlType.Action4, B(GcButtons.Y), tick, dt);
                UpdateWithValue(InputControlType.LeftTrigger, B(GcButtons.L) ? 1f : v.TriggerL / 255f, tick, dt);
                UpdateWithValue(InputControlType.RightTrigger, B(GcButtons.R) ? 1f : v.TriggerR / 255f, tick, dt);
                UpdateWithState(InputControlType.RightBumper, B(GcButtons.Z), tick, dt);
                UpdateWithState(InputControlType.Start, B(GcButtons.Start), tick, dt);
                UpdateWithState(InputControlType.Command, B(GcButtons.Start), tick, dt);
            }
        }
    }
}
