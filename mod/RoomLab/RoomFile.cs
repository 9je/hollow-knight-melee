using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FoxMelee.Lab {
    public sealed class Gate { public string Name; public double X, Y, W, H; }

    /// A room extracted by ssbmhk-hk-rooms: world-space colliders and transition gates.
    public sealed class RoomFile {
        public string Scene;
        public List<ColliderOutline> Colliders = new List<ColliderOutline>();
        public List<string> Names = new List<string>();   // each collider's GameObject
        public List<Gate> Gates = new List<Gate>();

        public static RoomFile Load(string path) {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var room = new RoomFile { Scene = r.GetProperty("scene").GetString() };
            foreach (var c in r.GetProperty("colliders").EnumerateArray()) {
                room.Names.Add(c.TryGetProperty("name", out var n) ? n.GetString() : "");
                room.Colliders.Add(new ColliderOutline {
                    Closed = c.GetProperty("closed").GetBoolean(),
                    OneWay = c.GetProperty("one_way").GetBoolean(),
                    Points = c.GetProperty("points").EnumerateArray().Select(p => new V2(p[0].GetDouble(), p[1].GetDouble())).ToArray(),
                });
            }
            foreach (var g in r.GetProperty("gates").EnumerateArray())
                room.Gates.Add(new Gate { Name = g.GetProperty("name").GetString(), X = g.GetProperty("x").GetDouble(), Y = g.GetProperty("y").GetDouble(),
                                          W = g.GetProperty("w").GetDouble(), H = g.GetProperty("h").GetDouble() });
            return room;
        }
    }

    public sealed class Conversion {
        public string Scene; public int Colliders, Shapes, Lines; public bool Ok; public string Error;
        public RoomTransform Transform; public System.IntPtr Collision;
        public V2 Min, Max;   // terrain bounds, Hollow Knight units
    }

    public static class Rooms {
        /// Exactly the mod's conversion (FoxRunner.EnterRoom): ForRoom, RoomGeometry.Build, mc_build_collision.
        public static Conversion Convert(MeleeCore core, RoomFile room, double scale) {
            var c = new Conversion { Scene = room.Scene, Colliders = room.Colliders.Count };
            if (room.Colliders.Count == 0) { c.Error = "no terrain"; return c; }
            c.Transform = RoomTransform.ForRoom(room.Colliders, scale);
            (c.Min, c.Max) = RoomGeometry.Bounds(room.Colliders);
            var shapes = RoomGeometry.Build(room.Colliders, c.Transform);
            c.Shapes = shapes.Count;
            if (!core.BuildCollision(shapes, out c.Collision)) { c.Error = core.Error; return c; }
            c.Lines = core.ReadCollision(c.Collision).V0.Length;
            c.Ok = true;
            return c;
        }
            public static List<TracePoint> Run(MeleeCore core, Conversion room, double startX, double startY, McInput[] inputs, bool still = false) {
            if (!core.LoadCollision(room.Collision)) throw new System.InvalidOperationException(core.Error);
            var (bmin, bmax) = (room.Transform.ToMelee(room.Min), room.Transform.ToMelee(room.Max));
            var cam = new McBox { Left = (float)bmin.X - 40, Right = (float)bmax.X + 40, Top = (float)bmax.Y + 40, Bottom = (float)bmin.Y - 40 };
            core.SetBounds(new McBox { Left = cam.Left - 100000, Right = cam.Right + 100000, Top = cam.Top + 100000, Bottom = cam.Bottom - 100000 }, cam);
            var lo = room.Transform.ToMelee(new V2(startX, startY));
            if (!(still ? core.RespawnFighter(0, (float)lo.X, (float)lo.Y) : core.PlaceFighter(0, (float)lo.X, (float)lo.Y))) throw new System.InvalidOperationException(core.Error);
            var trace = new List<TracePoint>();
            for (int f = 0; f < inputs.Length; f++) {
                core.SetInput(0, inputs[f]);
                if (!core.Step()) throw new System.InvalidOperationException(core.Error);
                core.ReadFighter(0, out var fx);
                trace.Add(new TracePoint { Frame = f, X = fx.X, Y = fx.Y, Action = fx.Action, Air = (int)fx.Airborne });
            }
            return trace;
        }

        /// More frames from where the fighter is now (after Run).
        public static List<TracePoint> Continue(MeleeCore core, McInput[] inputs) {
            var trace = new List<TracePoint>();
            for (int f = 0; f < inputs.Length; f++) {
                core.SetInput(0, inputs[f]);
                if (!core.Step()) throw new System.InvalidOperationException(core.Error);
                core.ReadFighter(0, out var fx);
                trace.Add(new TracePoint { Frame = f, X = fx.X, Y = fx.Y, Action = fx.Action, Air = (int)fx.Airborne });
            }
            return trace;
        }
}
}
