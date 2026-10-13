using System.IO;
using Xunit;

namespace FoxMelee.Tests {
    public class StockIconsTests {
        static byte[] File(int w, int h, int n) {
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            bw.Write(new[] { (byte)'S', (byte)'T', (byte)'C', (byte)'K' }); bw.Write(w); bw.Write(h); bw.Write(n);
            for (int i = 0; i < n; i++) for (int p = 0; p < w * h; p++) bw.Write(new[] { (byte)i, (byte)p, (byte)0, (byte)255 });
            return ms.ToArray();
        }

        [Fact]
        public void IconsByMeleeId() {
            var s = StockIcons.Parse(File(2, 2, 26));
            Assert.Equal(9, s.For(Roster.Find("Marth", out _))[0]);
            Assert.Equal(16, s.For(Roster.Fox).Length);
        }

        [Fact]
        public void RejectsWhatIsntOne() {
            Assert.Throws<InvalidDataException>(() => StockIcons.Parse(new byte[] { 1, 2, 3 }));
            var b = File(2, 2, 26); System.Array.Resize(ref b, b.Length - 1);
            Assert.Throws<InvalidDataException>(() => StockIcons.Parse(b));
        }
    }
}
