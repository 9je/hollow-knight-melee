using System;
using System.Collections.Generic;
using Xunit;

namespace FoxMelee.Tests {
    public class MeleeCoreTests {
        [Fact]
        public void LoadingAMissingLibraryNamesThePath() {
            var e = Assert.Throws<MeleeCoreException>(() => MeleeCore.Load("/nonexistent/libmelee_core.so"));
            Assert.Contains("/nonexistent/libmelee_core.so", e.Message);
        }

        [Fact]
        public void FoxStandsInABuiltRoom() {
            if (!DevConfig.Available) return;   // needs make -C core shared and the ISO from dev.toml
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.False(core.Init("/nonexistent/mem1.bin", DevConfig.Iso));
            Assert.Contains("cannot open", core.Error);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            Assert.True(core.ReadFighter(0, out var f));
            Assert.Equal(14u, f.Action);
            var shapes = new List<RoomShape> {
                new RoomShape { Kind = ShapeKind.Solid, XY = new float[] { -100, 0, 60, 0, 60, 100, 80, 100, 80, -50, -100, -50 } },
                new RoomShape { Kind = ShapeKind.Platform, XY = new float[] { -40, 30, 20, 30 } },
            };
            Assert.True(core.BuildCollision(shapes, out var col), core.Error);
            Assert.True(core.LoadCollision(col), core.Error);
            Assert.True(core.SetBounds(new McBox { Left = -300, Right = 300, Top = 300, Bottom = -200 },
                                       new McBox { Left = -250, Right = 250, Top = 250, Bottom = -150 }), core.Error);
            Assert.True(core.PlaceFighter(0, -10, 30), core.Error);
            for (int i = 0; i < 10; i++) Assert.True(core.Step(), core.Error);
            Assert.True(core.ReadFighter(0, out f));
            Assert.Equal(0u, f.Airborne);
            Assert.InRange(f.Y, 29.99f, 30.01f);   // on the platform
            // a breakable wall goes: the room is rebuilt around Fox, who keeps standing where he is
            var x0 = f.X;
            var broken = new List<RoomShape> {
                new RoomShape { Kind = ShapeKind.Solid, XY = new float[] { -100, 0, 60, 0, 60, -50, -100, -50 } },
                new RoomShape { Kind = ShapeKind.Platform, XY = new float[] { -40, 30, 20, 30 } },
            };
            Assert.True(core.BuildCollision(broken, out var col2), core.Error);
            Assert.True(core.LoadCollision(col2), core.Error);
            for (int i = 0; i < 30; i++) Assert.True(core.Step(), core.Error);
            Assert.True(core.ReadFighter(0, out f));
            Assert.Equal(0u, f.Airborne);
            Assert.InRange(f.Y, 29.99f, 30.01f);
            Assert.InRange(f.X, x0 - 0.01f, x0 + 0.01f);
            var bad = new List<RoomShape> { new RoomShape { Kind = ShapeKind.Solid, XY = new float[] { 0, 0, 1, 1 } } };
            Assert.False(core.BuildCollision(bad, out _));
            Assert.Contains("too few", core.Error);
        }
    
        [Fact]
        public void RoomGeometryOutputIsAcceptedByTheCoreBuilder() {
            if (!DevConfig.Available) return;
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            ColliderOutline Box(double x0, double y0, double x1, double y1) =>
                new ColliderOutline { Points = new[] { new V2(x0, y0), new V2(x1, y0), new V2(x1, y1), new V2(x0, y1) } };
            // a walled-in room (outer solid + hole), a touching block, an edge collider and a one-way platform
            var colliders = new[] {
                Box(0, 0, 100, 10), Box(0, 40, 100, 50), Box(0, 0, 10, 50), Box(90, 0, 100, 50), Box(100, 0, 120, 10),
                new ColliderOutline { Points = new[] { new V2(30, 20), new V2(50, 20) }, Closed = false },
                new ColliderOutline { Points = new[] { new V2(60, 25), new V2(80, 25), new V2(80, 26), new V2(60, 26) }, OneWay = true },
            };
            var t = new RoomTransform { Scale = 6, Origin = new V2(60, 25) };
            var shapes = RoomGeometry.Build(colliders, t);
            Assert.Contains(shapes, s => s.Kind == ShapeKind.Hole);
            Assert.True(core.BuildCollision(shapes, out _), core.Error);
        }

        [Fact]
        public void FoxsJabHasAHitboxAndAReportedHitIsRemembered() {
            if (!DevConfig.Available) return;
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            var boxes = new McHitbox[4]; var hurt = new McHurtbox[15];
            Assert.Equal(0, core.ReadHitboxes(boxes));
            Assert.InRange(core.ReadHurtboxes(hurt), 5, 15);
            Assert.True(hurt[0].Radius > 0);
            core.SetInput(0, new McInput { Buttons = 0x0100 });   // A: jab
            Assert.True(core.Step(), core.Error);
            core.SetInput(0, new McInput());
            int n = 0;
            for (int i = 0; i < 10 && (n = core.ReadHitboxes(boxes)) == 0; i++) Assert.True(core.Step(), core.Error);
            Assert.True(n > 0);
            Assert.Equal(4f, boxes[0].Damage);
            Assert.Equal(1u, boxes[0].Fresh);
            Assert.True(core.ReadFighter(0, out var f));
            Assert.InRange(boxes[0].X - f.X, 0.1f, 20f);
            Assert.False(core.ReportHit(boxes[0].Group + 77, 4));
            Assert.True(core.ReportHit(boxes[0].Group, boxes[0].Damage), core.Error);
            core.ReadHitboxes(boxes);
            Assert.Equal(0u, boxes[0].Fresh);
        }

        [Fact]
        public void AHitReportedAtTheHitPauseGivesFoxHitlagThatFrame() {
            if (!DevConfig.Available) return;
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            core.SetInput(0, new McInput { Buttons = 0x0100 });
            Assert.True(core.Step(), core.Error);
            core.SetInput(0, new McInput());
            var boxes = new McHitbox[4];
            int n = 0;
            for (int i = 0; i < 10; i++) {
                Assert.Equal(0, core.StepToHits());
                if ((n = core.ReadHitboxes(boxes)) > 0) break;
                Assert.True(core.Step(), core.Error);
            }
            Assert.True(n > 0);
            Assert.True(core.ReportHit(boxes[0].Group, boxes[0].Damage), core.Error);
            Assert.True(core.Step(), core.Error);
            Assert.True(core.ReadFighter(0, out var f));
            Assert.Equal(4f, f.Hitlag);
        }

        [Fact]
        public void ShieldAndLasersAreReadFromMelee() {
            if (!DevConfig.Available) return;
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            Assert.True(core.ReadShield(out var sh));
            Assert.Equal(0u, sh.Active);
            core.SetInput(0, new McInput { Buttons = 0x0020, TriggerR = 255 });
            for (int i = 0; i < 5; i++) Assert.True(core.Step(), core.Error);
            Assert.True(core.ReadShield(out sh));
            Assert.Equal(1u, sh.Active);
            Assert.InRange(sh.Radius, 6f, 20f);
            core.SetInput(0, new McInput());
            for (int i = 0; i < 30; i++) Assert.True(core.Step(), core.Error);
            core.SetInput(0, new McInput { Buttons = 0x0200 });
            Assert.True(core.Step(), core.Error);
            core.SetInput(0, new McInput());
            var items = new McItem[16];
            bool laser = false;
            for (int i = 0; i < 40 && !laser; i++) {
                Assert.True(core.Step(), core.Error);
                int n = core.ReadItems(items);
                for (int k = 0; k < n; k++) laser |= items[k].Kind == MeleeCore.FoxLaser && Math.Abs(items[k].Vx) > 1;
            }
            Assert.True(laser);
        }

        [Fact]
        public void MeleesOwnFrameDecodesToFoxsTriangles() {
            if (!DevConfig.Available) return;
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            Assert.True(core.GxCapture(true), core.Error);
            for (int i = 0; i < 3; i++) Assert.True(core.Step(), core.Error);
            var frame = new GxFrame();
            Assert.True(core.GxDecode(GxFrame.FoxAndEffects, frame), core.Error);
            Assert.True(core.ReadFighter(0, out var f));
            Assert.True(frame.VertexCount > 9000);
            Assert.Equal(frame.VertexCount, frame.Runs[frame.Runs.Count - 1].First + frame.Runs[frame.Runs.Count - 1].Count);
            for (int i = 0; i < frame.VertexCount; i++) {
                Assert.InRange(frame.Pos[3 * i], f.X - 15, f.X + 15);
                Assert.InRange(frame.Pos[3 * i + 1], f.Y - 2, f.Y + 20);
            }
            var atlas = frame.Runs.Find(r => r.Tex == GxFrame.Atlas);
            Assert.True(atlas.Count > 0);
            Assert.True(core.GxTexture(GxFrame.Atlas, out var t));
            Assert.Equal(4096, t.W);
            Assert.NotEqual(IntPtr.Zero, t.Rgba);
            Assert.True(t.Gen > 0);
            Assert.False(core.GxTexture(123456, out _));
        }
    }
}
