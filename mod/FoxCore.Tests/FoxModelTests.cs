using System;
using System.IO;
using Xunit;

namespace FoxMelee.Tests {
    public class FoxModelTests {
        static string ModelPath => Path.Combine(DevConfig.Repo, "build/foxmodel/fox.model");

        [Fact]
        public void RejectsFilesThatAreNotFoxModels() {
            var p = Path.GetTempFileName();
            File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
            var e = Assert.Throws<InvalidDataException>(() => FoxModel.Load(p));
            Assert.Contains("FOXMODEL v2", e.Message);
        }

        [Fact]
        public void PoseFollowsTheCoreSkeleton() {
            if (!DevConfig.Available || !File.Exists(ModelPath)) return;   // needs ssbmhk-extract-fox --out build/foxmodel
            var m = FoxModel.Load(ModelPath);
            Assert.Equal(73, m.BoneCount);
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            var bones = new float[128 * 12];
            Assert.Equal(0, core.ReadBones(0, bones));
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) core.Step();
            int n = core.ReadBones(0, bones);
            Assert.Equal(73, n);
            core.ReadFighter(0, out var f);
            var pos = new float[m.Pos.Length];
            m.Pose(bones, n, pos);
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < pos.Length; i += 3) { minX = Math.Min(minX, pos[i]); maxX = Math.Max(maxX, pos[i]); minY = Math.Min(minY, pos[i + 1]); maxY = Math.Max(maxY, pos[i + 1]); }
            Assert.InRange(minY, f.Y - 1, f.Y + 1);                 // feet on the floor
            Assert.InRange(maxY, f.Y + 12, f.Y + 18);               // about Fox's height
            Assert.InRange((minX + maxX) / 2, f.X - 6, f.X + 6);    // around Fox
            // facing left mirrors through the matrices: still a well-formed pose around Fox
            core.SetInput(0, new McInput { StickX = -127 });
            for (int i = 0; i < 20; i++) core.Step();
            core.ReadFighter(0, out f);
            Assert.True(f.Facing < 0);
            m.Pose(bones, core.ReadBones(0, bones), pos);
            Assert.Throws<ArgumentException>(() => m.Pose(bones, 10, pos));
            // parts Melee hides this frame are not drawn; the rest are drawn back (Melee -z) to front
            var hidden = new byte[256]; var joint = new byte[256];
            int nd = core.ReadDobjs(0, hidden, joint);
            Assert.Equal(86, nd);
            var partHidden = m.MapHidden(hidden, joint, nd);
            Assert.Contains(true, partHidden);
            var order = new int[m.Tris.Length];
            int count = m.DrawOrder(pos, partHidden, order);
            Assert.InRange(count, 3, m.Tris.Length - 3);           // some triangles skipped
            float prev = float.MinValue;
            for (int t = 0; t < count; t += 3) {
                float z = (pos[3 * order[t] + 2] + pos[3 * order[t + 1] + 2] + pos[3 * order[t + 2] + 2]) / 3;
                Assert.True(z >= prev - 1e-4f, $"triangle {t / 3} out of order");
                prev = z;
            }
        }
    }
}
