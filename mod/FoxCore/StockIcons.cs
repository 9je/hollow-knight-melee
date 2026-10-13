using System;
using System.IO;

namespace FoxMelee {
    /// stocks.bin (ssbmhk-extract-stocks, from the player's ISO): Melee's stock icons, RGBA, top row first, in Melee's
    /// character id order.
    public sealed class StockIcons {
        public int Width, Height;
        public byte[][] Rgba;

        public static StockIcons Parse(byte[] b) {
            if (b.Length < 16 || b[0] != 'S' || b[1] != 'T' || b[2] != 'C' || b[3] != 'K') throw new InvalidDataException("not a stocks.bin");
            int w = BitConverter.ToInt32(b, 4), h = BitConverter.ToInt32(b, 8), n = BitConverter.ToInt32(b, 12);
            if (w <= 0 || h <= 0 || w > 256 || h > 256 || n <= 0 || n > 64 || b.Length != 16 + w * h * 4 * n) throw new InvalidDataException("stocks.bin has the wrong size");
            var s = new StockIcons { Width = w, Height = h, Rgba = new byte[n][] };
            for (int i = 0; i < n; i++) { s.Rgba[i] = new byte[w * h * 4]; Buffer.BlockCopy(b, 16 + i * w * h * 4, s.Rgba[i], 0, w * h * 4); }
            return s;
        }

        public byte[] For(Character c) => c.MeleeId >= 0 && c.MeleeId < Rgba.Length ? Rgba[c.MeleeId] : null;
    }
}
