using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace FoxMelee.Tests {
    public class CssAssetsTests {
        static byte[] Section(int w, int h, params (int c, int k)[] entries) {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            bw.Write(w); bw.Write(h); bw.Write(entries.Length);
            foreach (var (c, k) in entries) {
                bw.Write((byte)c); bw.Write((byte)k); bw.Write((ushort)0);
                var px = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)c; px[i + 1] = (byte)k; px[i + 3] = 255; }
                bw.Write(px);
            }
            return ms.ToArray();
        }
        static byte[] File(params byte[][] parts) {
            var all = new List<byte> { (byte)'C', (byte)'S', (byte)'S', (byte)'1' };
            foreach (var p in parts) all.AddRange(p);
            return all.ToArray();
        }

        [Fact]
        public void TilesPortraitsAndStocksAreKeyedByCharacterAndCostume() {
            var a = CssAssets.Parse(File(Section(2, 2, (8, 0), (2, 0)), Section(3, 4, (2, 0), (2, 1), (2, 2), (0, 0)), Section(1, 1, (2, 1))));
            Assert.Equal(2, a.Tiles.Count);
            Assert.Equal(2, a.Tiles[8].Width);
            Assert.Equal(4, a.Portraits.Count);
            Assert.Equal((byte)1, a.Portraits[(2, 1)].Rgba[1]);   // the second costume's pixels
            Assert.Equal(3, a.CostumesOf(2));
            Assert.Equal(1, a.CostumesOf(0));
            Assert.Equal(0, a.CostumesOf(19));
            Assert.Single(a.Stocks);
        }

        [Fact]
        public void ABrokenFileIsRefused() {
            Assert.Throws<InvalidDataException>(() => CssAssets.Parse(new byte[] { 1, 2, 3, 4, 5 }));
            var cut = File(Section(2, 2, (8, 0)));
            Array.Resize(ref cut, cut.Length - 3);
            Assert.Throws<InvalidDataException>(() => CssAssets.Parse(cut));
        }
    }
}
