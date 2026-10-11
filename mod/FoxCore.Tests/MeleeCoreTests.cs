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
    }
}
