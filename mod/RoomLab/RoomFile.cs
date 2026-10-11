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
        public List<Gate> Gates = new List<Gate>();

        public static RoomFile Load(string path) {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var room = new RoomFile { Scene = r.GetProperty("scene").GetString() };
            foreach (var c in r.GetProperty("colliders").EnumerateArray())
                room.Colliders.Add(new ColliderOutline {
                    Closed = c.GetProperty("closed").GetBoolean(),
                    OneWay = c.GetProperty("one_way").GetBoolean(),
                    Points = c.GetProperty("points").EnumerateArray().Select(p => new V2(p[0].GetDouble(), p[1].GetDouble())).ToArray(),
                });
            foreach (var g in r.GetProperty("gates").EnumerateArray())
                room.Gates.Add(new Gate { Name = g.GetProperty("name").GetString(), X = g.GetProperty("x").GetDouble(), Y = g.GetProperty("y").GetDouble(),
                                          W = g.GetProperty("w").GetDouble(), H = g.GetProperty("h").GetDouble() });
            return room;
        }
    }

    public sealed class Conversion {
        public string Scene; public int Colliders, Shapes, Lines; public bool Ok; public string Error;
        public RoomTransform Transform; public System.IntPtr Collision;
    }

    public static class Rooms {
        /// Exactly the mod's conversion (FoxRunner.EnterRoom): ForRoom, RoomGeometry.Build, mc_build_collision.
        public static Conversion Convert(MeleeCore core, RoomFile room, double scale) {
            var c = new Conversion { Scene = room.Scene, Colliders = room.Colliders.Count };
            if (room.Colliders.Count == 0) { c.Error = "no terrain"; return c; }
            c.Transform = RoomTransform.ForRoom(room.Colliders, scale);
            var shapes = RoomGeometry.Build(room.Colliders, c.Transform);
            c.Shapes = shapes.Count;
            if (!core.BuildCollision(shapes, out c.Collision)) { c.Error = core.Error; return c; }
            c.Lines = core.ReadCollision(c.Collision).V0.Length;
            c.Ok = true;
            return c;
        }
    }
}
