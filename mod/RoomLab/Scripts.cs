using System.Collections.Generic;

namespace FoxMelee.Lab {
    public sealed class TracePoint { public int Frame; public double X, Y; public uint Action; public int Air; }

    /// Scripted controller inputs for runs through real rooms (raw GameCube values).
    public static class Scripts {
        const ushort X = 0x0400, R = 0x0020;
        public static McInput[] Get(string name, int frames) {
            var a = new McInput[frames];
            for (int i = 0; i < frames; i++) {
                switch (name) {
                    case "idle": case "fall": break;
                    case "run-right": a[i].StickX = 80; break;
                    case "run-left": a[i].StickX = -80; break;
                    case "drop-run-right": if (i >= 90) a[i].StickX = 80; break;   // fall in from a top gate, then run
                    case "hop-right": a[i].StickX = 80; if (i % 40 < 3) a[i].Buttons = X; break;
                    case "wavedash-right":   // jump, then air dodge down-right on the 4th airborne frame, every 60 frames
                        int k = i % 60;
                        if (k == 0) a[i].Buttons = X;
                        if (k == 4) { a[i].Buttons = R; a[i].TriggerR = 140; a[i].StickX = 56; a[i].StickY = -56; }
                        break;
                    default: throw new System.ArgumentException($"unknown script {name}");
                }
            }
            return a;
        }
    }
}
