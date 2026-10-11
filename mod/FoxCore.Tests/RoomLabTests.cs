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
    }
}
