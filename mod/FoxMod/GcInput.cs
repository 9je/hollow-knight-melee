using InControl;
using UnityEngine;

namespace FoxMelee {
    /// The GameCube controller through a Wii U / Mayflash adapter, read natively over USB as Slippi Dolphin does.
    /// Fox gets its values unmapped (Melee sees what a GameCube would give it); Hollow Knight sees the same controller
    /// as an InControl device for its menus, pausing (Start) and any room played as the Knight.
    sealed class GcInput {
        readonly GcAdapter adapter = new GcAdapter(System.Environment.OSVersion.Platform == System.PlatformID.Win32NT
            ? System.IO.Path.Combine(FoxMod.ModDir, "libusb-1.0.dll") : "libusb-1.0.so.0");
        readonly GcPads pads = new GcPads();
        readonly int port;
        string lastStatus;

        public GcInput(int port) {
            this.port = port;
            if (!adapter.Start()) { FoxMod.Instance.LogWarn("GameCube adapter off: " + adapter.Status); return; }
            InputManager.AttachDevice(Device = new GcDevice(this));
        }

        /// The controller as Hollow Knight's input sees it (an InControl device).
        public InputDevice Device { get; private set; }

        /// While a controller is in the port, Hollow Knight's actions read only it (and the keyboard): the same pad seen a
        /// second time as another device (Steam Input's virtual pad on Windows) would hand Hollow Knight the D-pad the mod
        /// keeps for itself, and at a bench D-pad right got the Knight up instead of opening the inventory.
        bool pinned;
        public void PinHollowInput(InControl.PlayerActionSet actions) {
            if (actions == null || Device == null) return;
            bool want = Current() != null;
            if (want == pinned && (!want || actions.Device == Device)) return;
            actions.Device = want ? Device : null;
            pinned = want;
            var others = string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(InputManager.Devices, d => d != Device), d => d.Name));
            FoxMod.Instance.Log(want ? $"Hollow Knight reads the GameCube controller only (other controllers: {(others.Length > 0 ? others : "none")})" : "Hollow Knight reads any controller");
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
        public static bool InventoryOpen;       // FoxRunner: Hollow Knight's inventory is open (D-pad right moves right)
        public static bool QuickMapHeld;        // D-pad right held: the quick map (Fox stands still)
        public static bool InventoryTapped;     // D-pad right tapped: FoxRunner opens the inventory (and clears it)
        public static bool DreamNailButton;     // D-pad up held: Hollow Knight's Dream Nail button (Action4) is pressed

        /// Fox's input for the next Melee frame, or null when no GameCube controller is plugged in.
        public McInput? Melee() { Poll(); return pads.Melee(port); }

        public void Dispose() => adapter.Dispose();

        internal sealed class GcDevice : InputDevice {
            readonly GcInput gc;
            readonly DPadRight dpadRight = new DPadRight();

            public GcDevice(GcInput gc) : base("GameCube Controller (adapter)") {
                this.gc = gc;
                // a style Hollow Knight knows (it re-maps and logs an error for unknown ones); A confirms, B goes back
                DeviceStyle = InputDeviceStyle.XboxOne;
                foreach (var t in new[] {
                    InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp, InputControlType.LeftStickDown,
                    InputControlType.RightStickLeft, InputControlType.RightStickRight, InputControlType.RightStickUp, InputControlType.RightStickDown,
                    InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
                    InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
                    InputControlType.LeftTrigger, InputControlType.RightTrigger, InputControlType.RightBumper, InputControlType.LeftBumper,
                    InputControlType.Start, InputControlType.Command, InputControlType.View, InputControlType.Back,
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
                var right = dpadRight.Update(B(GcButtons.DRight), dt, InventoryOpen);
                QuickMapHeld = right.QuickMap;
                UpdateWithState(InputControlType.DPadLeft, B(GcButtons.DLeft) && !HideDPadSides, tick, dt);
                UpdateWithState(InputControlType.DPadRight, right.Right || (B(GcButtons.DRight) && !HideDPadSides && !InventoryOpen), tick, dt);
                UpdateWithState(InputControlType.Action1, B(GcButtons.A), tick, dt);     // A: confirm / jump
                UpdateWithState(InputControlType.Action2, B(GcButtons.B), tick, dt);     // B: back
                UpdateWithState(InputControlType.Action3, B(GcButtons.X), tick, dt);
                UpdateWithState(InputControlType.Action4, B(GcButtons.Y) || DreamNailButton, tick, dt);
                // only a full press: a trigger at rest reads a little above 0, and Hollow Knight turns the inventory's
                // pages (paneLeft / paneRight) on any trigger value
                UpdateWithState(InputControlType.LeftTrigger, B(GcButtons.L), tick, dt);
                UpdateWithState(InputControlType.RightTrigger, B(GcButtons.R), tick, dt);
                UpdateWithState(InputControlType.RightBumper, B(GcButtons.Z), tick, dt);
                UpdateWithState(InputControlType.Start, B(GcButtons.Start), tick, dt);
                UpdateWithState(InputControlType.Command, B(GcButtons.Start), tick, dt);
                // no Select or LB on a GameCube pad: D-pad right tapped opens the inventory (FoxRunner opens it itself:
                // a player's own button bindings may have View / Back on another action, which got the Knight off a bench),
                // held shows the quick map
                if (right.Inventory) InventoryTapped = true;
                UpdateWithState(InputControlType.LeftBumper, right.MapButton, tick, dt);
            }
        }
    }
}
