using System;

namespace FoxMelee {
    public struct PadState {
        public float LeftX, LeftY, RightX, RightY;
        public float LeftTrigger, RightTrigger;
        public bool South, East, West, North, LeftBumper, RightBumper;
    }

    public static class GcButtons {
        public const ushort DLeft = 0x0001, DRight = 0x0002, DDown = 0x0004, DUp = 0x0008, Z = 0x0010, R = 0x0020, L = 0x0040,
                            A = 0x0100, B = 0x0200, X = 0x0400, Y = 0x0800, Start = 0x1000;
    }

    /// A modern controller (or the keyboard mapped onto one) as GameCube PADStatus values. The D-pad and
    /// Start stay with Hollow Knight (spec §3.2).
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
}
