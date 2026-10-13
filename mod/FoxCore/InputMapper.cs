using System;

namespace FoxMelee {
    public struct PadState {
        public float LeftX, LeftY, RightX, RightY;
        public float LeftTrigger, RightTrigger;
        public bool South, East, West, North, LeftBumper, RightBumper;
        public bool DPadUp, DPadDown, DPadLeft, DPadRight;
    }

    public static class GcButtons {
        public const ushort DLeft = 0x0001, DRight = 0x0002, DDown = 0x0004, DUp = 0x0008, Z = 0x0010, R = 0x0020, L = 0x0040,
                            A = 0x0100, B = 0x0200, X = 0x0400, Y = 0x0800, Start = 0x1000;
    }

    /// A modern controller (or the keyboard mapped onto one) as GameCube PADStatus values. D-pad up is Melee's
    /// taunt; the rest of the D-pad and Start stay with Hollow Knight (spec §3.2, HkControls).
    public static class InputMapper {
        public const int StickRange = 80;
        public const int TriggerRange = 140;
        const float Click = 0.9f;

        static sbyte Axis(float v) => (sbyte)Math.Max(-127, Math.Min(127, (int)Math.Round(v * StickRange, MidpointRounding.AwayFromZero)));
        static byte Trigger(float v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * TriggerRange, MidpointRounding.AwayFromZero)));

        public static McInput Map(PadState p) {
            ushort b = 0;
            if (p.South) b |= GcButtons.A;
            if (p.East) b |= GcButtons.B;
            if (p.West) b |= GcButtons.X;
            if (p.North) b |= GcButtons.Y;
            if (p.RightBumper) b |= GcButtons.Z;
            if (p.DPadUp) b |= GcButtons.DUp;
            float l = p.LeftTrigger, r = p.LeftBumper ? 1f : p.RightTrigger;
            if (l >= Click) b |= GcButtons.L;
            if (r >= Click) b |= GcButtons.R;
            return new McInput {
                Buttons = b,
                StickX = Axis(p.LeftX), StickY = Axis(p.LeftY), CStickX = Axis(p.RightX), CStickY = Axis(p.RightY),
                TriggerL = Trigger(l), TriggerR = Trigger(r),
            };
        }
    }

    /// Hollow Knight's own actions while Fox plays: D-pad left/right interact (inspect, talk, benches), D-pad down
    /// focuses. D-pad up stays Melee's taunt.
    public struct HkControls {
        public bool Interact, Focus, Inventory;   // Inventory: GameCube D-pad right (it has no Select button)
        const ushort Hollow = GcButtons.DLeft | GcButtons.DRight | GcButtons.DDown;

        public static HkControls FromGameCube(McInput gc) => new HkControls {
            Interact = (gc.Buttons & GcButtons.DLeft) != 0, Focus = (gc.Buttons & GcButtons.DDown) != 0,
            Inventory = (gc.Buttons & GcButtons.DRight) != 0,
        };

        public static HkControls FromPad(PadState p) => new HkControls { Interact = p.DPadLeft || p.DPadRight, Focus = p.DPadDown };

        /// The GameCube controller's input for Melee without the buttons Hollow Knight uses.
        public static McInput ForMelee(McInput gc) { gc.Buttons &= unchecked((ushort)~Hollow); return gc; }
    }

    public struct FocusStep { public bool Focusing, Heal; public int Drain; }

    /// Hollow Knight's Focus for Fox (spec §5.3): held while Fox can focus (grounded and still), it drains `cost`
    /// Soul over `frames` frames, then heals a mask, and goes on while held. Letting go early loses the Soul spent.
    public sealed class FocusRules {
        readonly int frames, cost;
        int frame;

        public FocusRules(int frames, int cost) { this.frames = frames; this.cost = cost; }

        /// Why a Focus press does nothing (null: it can start), for the log.
        public static string WhyNot(bool still, int soul, int cost, int health, int maxHealth) =>
            health >= maxHealth ? "masks are full" : soul < cost ? $"needs {cost} soul (has {soul})" : !still ? "Fox must stand still on the ground" : null;
        public int Cost => cost;

        public FocusStep Tick(bool held, bool canFocus, int soul, int health, int maxHealth) {
            if (!held || !canFocus || health >= maxHealth || (frame == 0 && soul < cost)) { frame = 0; return default; }
            int drain = cost * (frame + 1) / frames - cost * frame / frames;
            frame++;
            bool heal = frame >= frames;
            if (heal) frame = 0;
            return new FocusStep { Focusing = true, Drain = drain, Heal = heal };
        }
    }
}

namespace FoxMelee {
    /// The GameCube D-pad right, which stands in for the buttons the pad lacks: a tap opens the inventory (Select),
    /// a hold shows the quick map while held (LB); in the inventory it moves right.
    public sealed class DPadRight {
        public const float HoldSeconds = 0.3f;
        public const float MapButtonDelay = 0.1f;   // the Knight is handed over (QuickMap) before the map button is pressed
        /// QuickMap: held, the mod hands the Knight to Hollow Knight; MapButton: its quick map button, pressed a little later
        /// (Hollow Knight opens the map on the press, and only for a Knight it controls).
        public struct State { public bool Inventory, QuickMap, MapButton, Right; }
        float held = -1f;                       // seconds held; -1: up

        public State Update(bool down, float dt, bool inInventory) {
            if (inInventory) { held = -1f; return new State { Right = down }; }
            if (down) {
                held = held < 0 ? dt : held + dt;
                return new State { QuickMap = held >= HoldSeconds, MapButton = held >= HoldSeconds + MapButtonDelay };
            }
            bool tap = held >= 0 && held < HoldSeconds;
            held = -1f;
            return new State { Inventory = tap };
        }
    }
}
