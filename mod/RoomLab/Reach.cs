using System;
using System.Collections.Generic;

namespace FoxMelee.Lab {
    /// How high and how far a character gets on its own (Hollow Knight units, at the room scale), on a long flat floor:
    /// a single full jump; jump, double jump at its peak, then up-B at that peak (the most height); and running, the same
    /// with up-B angled forward or side-B (the widest gap, landing back at the floor's height). The world was scaled so Fox's
    /// single jump is the Knight's: a character's best below that can't follow the Knight everywhere. Scripted, not frame-perfect:
    /// lower bounds (2026-10-13: every character's best height >= 10.3, twice the Knight's jump; widest gap >= 18.5).
    public static class Reach {
        const ushort X = 0x0400, B = 0x0200;

        public sealed class Result { public double Jump, High, Gap; }

        public static Result Measure(MeleeCore core, double scale) {
            var floor = new List<ColliderOutline> { new ColliderOutline { Points = new[] { new V2(0, -20), new V2(600, -20), new V2(600, 0), new V2(0, 0) } } };
            var t = RoomTransform.ForRoom(floor, scale);
            if (!core.BuildCollision(RoomGeometry.Build(floor, t), out var col)) throw new InvalidOperationException(core.Error);
            if (!core.LoadCollision(col)) throw new InvalidOperationException(core.Error);
            var lo = t.ToMelee(new V2(-1000, -1000)); var hi = t.ToMelee(new V2(1600, 1000));
            var cam = new McBox { Left = (float)lo.X, Right = (float)hi.X, Top = (float)hi.Y, Bottom = (float)lo.Y };
            core.SetBounds(new McBox { Left = cam.Left - 100000, Right = cam.Right + 100000, Top = cam.Top + 100000, Bottom = cam.Bottom - 100000 }, cam);
            double ToHkY(float y) => t.ToHk(new V2(0, y)).Y;
            double ToHkX(float x) => t.ToHk(new V2(x, 0)).X;
            var r = new Result {
                Jump = Run(core, t, false, 0, 0, ToHkY, ToHkX).height,
                High = Run(core, t, true, 0, 127, ToHkY, ToHkX).height,
            };
            r.Gap = Math.Max(Run(core, t, true, 90, 90, ToHkY, ToHkX).gap, Run(core, t, true, 127, 0, ToHkY, ToHkX).gap);
            return r;
        }

        // from standing at x=40 (a run of 40 frames first when moving), jump; with `more`, double jump at the peak and the
        // special (stick sx, sy: up-B up, up-B forward, side-B) at the next peak, holding the stick that way after
        static (double height, double gap) Run(MeleeCore core, RoomTransform t, bool more, sbyte sx, sbyte sy, Func<float, double> hkY, Func<float, double> hkX) {
            var at = t.ToMelee(new V2(40, 0));
            core.RespawnFighter(0, (float)at.X, (float)at.Y);
            var idle = new McInput();
            for (int i = 0; i < 60; i++) { core.SetInput(0, idle); core.Step(); }
            bool moving = sx != 0;
            int run = moving ? 40 : 0;
            for (int i = 0; i < run; i++) { core.SetInput(0, new McInput { StickX = 80 }); core.Step(); }
            core.ReadFighter(0, out var f0);
            double x0 = hkX(f0.X), y0 = hkY(f0.Y), top = y0, landX = x0;
            // phases: 0 first jump rising; 1 air jumps at each peak while they still lift; 2 the special
            int phase = 0, wait = 0, jumps = 0; bool wasAir = false; float pressY = 0, riseY = 0;
            float lastY = f0.Y;
            for (int i = 0; i < 600; i++) {
                var inp = new McInput { StickX = (sbyte)(moving ? 127 : 0) };
                if (i < 12) inp.Buttons = X;                               // held through any jumpsquat: a full jump
                core.ReadFighter(0, out var f);
                bool falling = f.Y < lastY;
                lastY = f.Y;
                if (more && f.Airborne != 0 && i >= 12) {
                    if (wait > 0) {
                        wait--;
                        if (phase == 1 && wait > 14) inp.Buttons = X;
                        if (phase == 2 && wait > 4) inp.Buttons = B;
                        if (phase == 1) riseY = Math.Max(riseY, f.Y);
                        if (phase == 1 && wait == 0 && riseY <= pressY + 0.01f) { phase = 2; wait = 10; inp.Buttons = B; inp.StickX = sx; inp.StickY = sy; }   // no jump left
                    }
                    else if (falling && phase <= 1 && jumps < 6) { phase = 1; jumps++; wait = 20; pressY = riseY = f.Y; inp.Buttons = X; }   // an air jump (if any is left)
                    else if (falling) { phase = 2; wait = 10; inp.Buttons = B; inp.StickX = sx; inp.StickY = sy; }
                    if (phase == 2) { inp.StickX = sx; inp.StickY = sy; }
                }
                core.SetInput(0, inp);
                core.Step();
                core.ReadFighter(0, out f);
                top = Math.Max(top, hkY(f.Y));
                if (f.Airborne != 0) wasAir = true;
                else if (wasAir) { landX = hkX(f.X); break; }
            }
            return (top - y0, Math.Abs(landX - x0));
        }
    }
}
