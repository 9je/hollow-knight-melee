using System.Linq;
using System.Collections.Generic;
using System.IO;
using FoxMelee.Lab;
using Xunit;

namespace FoxMelee.Tests {
    public class RoomLabTests {
        static ColliderOutline Box(double x0, double y0, double x1, double y1) =>
            new ColliderOutline { Points = new[] { new V2(x0, y0), new V2(x1, y0), new V2(x1, y1), new V2(x0, y1) } };

        [Fact]
        public void ForRoomCentresTheTerrain() {
            var t = RoomTransform.ForRoom(new[] { Box(10, 0, 30, 4) }, 6);
            Assert.Equal(20, t.Origin.X); Assert.Equal(2, t.Origin.Y); Assert.Equal(6, t.Scale);
        }

        [Fact]
        public void ReadCollisionReturnsTheBuiltLines() {
            if (DevConfig.CoreSo == null) return;
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.BuildCollision(new List<RoomShape> { new RoomShape { Kind = ShapeKind.Solid, XY = new float[] { 0, 0, 10, 0, 10, 5, 0, 5 } } }, out var col), core.Error);
            var m = core.ReadCollision(col);
            Assert.Equal(4, m.V0.Length);
            Assert.Contains((ushort)1, m.HiFlags);   // a floor
            Assert.Equal(8, m.XY.Length);
        }

        [Fact]
        public void RoomFilesLoadAndConvert() {
            if (DevConfig.CoreSo == null) return;
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "{\"scene\":\"Test\",\"colliders\":[{\"name\":\"a\",\"kind\":\"box\",\"closed\":true,\"one_way\":false,\"points\":[[0,0],[10,0],[10,2],[0,2]]}," +
                                    "{\"name\":\"e\",\"kind\":\"edge\",\"closed\":false,\"one_way\":false,\"points\":[[20,0],[30,0]]}],\"gates\":[{\"name\":\"right1\",\"x\":31,\"y\":2,\"w\":1,\"h\":4}]}");
            var room = RoomFile.Load(path);
            Assert.Equal("Test", room.Scene);
            Assert.Equal(2, room.Colliders.Count);
            Assert.False(room.Colliders[1].Closed);
            Assert.Equal("right1", room.Gates[0].Name);
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            var c = Rooms.Convert(core, room, 6);
            Assert.True(c.Ok, c.Error);
            Assert.Equal(2, c.Shapes);
            Assert.Equal(8, c.Lines);
            var empty = Rooms.Convert(core, new RoomFile { Scene = "Menu", Colliders = new List<ColliderOutline>(), Gates = new List<Gate>() }, 6);
            Assert.False(empty.Ok);
            Assert.Equal("no terrain", empty.Error);
        }
            [Fact]
        public void FoxRunsThroughKingsPassWithoutFallingThroughTheWorld() {
            var file = Path.Combine(DevConfig.Repo, "build/roomlab/rooms/Tutorial_01.json");
            if (!DevConfig.Available || !File.Exists(file)) return;   // needs ssbmhk-hk-rooms output
            var room = RoomFile.Load(file);
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            var c = Rooms.Convert(core, room, 6);
            Assert.True(c.Ok, c.Error);
            // the game's own entrance: the Knight drops in through top1 (the right exit has a scripted door in front of it)
            var gate = room.Gates.Find(g => g.Name == "top1");
            var trace = Rooms.Run(core, c, gate.X, gate.Y - gate.H / 2 + 1, Scripts.Get("drop-run-right", 400));
            var (min, _) = RoomGeometry.Bounds(room.Colliders);
            double floorOfWorld = c.Transform.ToMelee(min).Y;
            Assert.All(trace, t => Assert.True(t.Y > floorOfWorld - 1, $"frame {t.Frame}: y {t.Y} below the room"));
            var landed = trace.FindIndex(t => t.Frame > 10 && t.Air == 0);
            Assert.True(landed > 0, "Fox lands on real terrain");
            Assert.True(trace[^1].X > trace[landed].X + 20, "Fox runs right after landing");
            Assert.Equal(0, trace[^1].Air);
        }
    
        // King's Pass's collapsing floor ("Collapser Tute 01", its "Solid" boxes): Fox stands on it, it goes (the mod
        // rebuilds Melee's collision without it), and he falls down the shaft below, never inside the rock
        [Fact]
        public void FoxFallsWhenKingsPassFloorCollapses() {
            var file = Path.Combine(DevConfig.Repo, "build/roomlab/rooms/Tutorial_01.json");
            if (!DevConfig.Available || !File.Exists(file)) return;
            var room = RoomFile.Load(file);
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            var c = Rooms.Convert(core, room, 6);
            Assert.True(c.Ok, c.Error);
            var stand = Rooms.Run(core, c, 124.0, 62.0, Scripts.Get("idle", 60));
            System.IO.File.WriteAllLines(Path.Combine(DevConfig.Repo, "build/roomlab/collapse/stand.txt"),
                stand.Select(t => { var h = c.Transform.ToHk(new V2(t.X, t.Y)); return $"{t.Frame}: hk ({h.X:F2}, {h.Y:F2}) action {t.Action} air {t.Air}"; }));
            Assert.Equal(0, stand[^1].Air);
            var collapsed = room.Colliders.Where((o, i) => !(room.Names[i] == "Solid" && o.Points.All(q => q.X > 116 && q.X < 132 && q.Y > 58 && q.Y < 63))).ToList();
            Assert.Equal(room.Colliders.Count - 2, collapsed.Count);
            var shapes = RoomGeometry.Build(collapsed, c.Transform);
            Assert.True(core.BuildCollision(shapes, out var col), core.Error);
            Assert.True(core.LoadCollision(col), core.Error);
            var log = new List<string>();
            int inside = 0, worst = 0;
            for (int f = 0; f < 240; f++) {
                core.SetInput(0, new McInput());
                Assert.True(core.Step(), core.Error);
                core.ReadFighter(0, out var fx);
                var hk = c.Transform.ToHk(new V2(fx.X, fx.Y));
                log.Add($"{f}: hk ({hk.X:F2}, {hk.Y:F2}) action {fx.Action} air {fx.Airborne}");
                inside = StuckGuard.InsideSolid(shapes, fx.X, fx.Y + 6) ? inside + 1 : 0;
                worst = System.Math.Max(worst, inside);
            }
            System.IO.File.WriteAllLines(Path.Combine(DevConfig.Repo, "build/roomlab/collapse/fall.txt"), log);
            Assert.True(worst < StuckGuard.Frames, $"inside the rock for {worst} frames: " + string.Join(" | ", log.GetRange(0, 40)));
            core.ReadFighter(0, out var end);
            var endHk = c.Transform.ToHk(new V2(end.X, end.Y));
            Assert.True(endHk.Y < 58, $"Fox fell down the shaft (ends at {endHk.X:F2}, {endHk.Y:F2})");
            Assert.Equal(0u, (uint)end.Airborne);
        }
    
        // Crossroads_25: Firefox straight up at x = 33 hits the ceiling and must stay there (a near-zero ceiling line
        // threw Fox 14 units sideways onto the stalactite hazard: "it keeps resetting me back")
        [Fact]
        public void FirefoxIntoCrossroads25CeilingStaysPut() {
            var file = Path.Combine(DevConfig.Repo, "build/roomlab/rooms/Crossroads_25.json");
            if (!DevConfig.Available || !File.Exists(file)) return;
            var room = RoomFile.Load(file);
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            var c = Rooms.Convert(core, room, 6);
            Assert.True(c.Ok, c.Error);
            var trace = Rooms.Run(core, c, 33, 6.1, Scripts.Get("firefox-up", 130));
            for (int i = 1; i < trace.Count; i++) {
                double step = System.Math.Abs(trace[i].X - trace[i - 1].X) + System.Math.Abs(trace[i].Y - trace[i - 1].Y);
                Assert.True(step < 12, $"frame {i}: Fox moved {step:F1} Melee units in one frame");
            }
        }
    }
}
