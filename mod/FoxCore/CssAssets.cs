using System;
using System.Collections.Generic;
using System.IO;

namespace FoxMelee {
    /// css.bin (ssbmhk-extract-css, from the player's ISO): Melee's character select. Its 25 tiles (64x56: a face and a
    /// name plate; none for Sheik, as in Melee), a portrait (136x188) and a stock icon (24x24) for every character's
    /// every costume. Images are RGBA, top row first, keyed by Melee's external id and the costume number.
    public sealed class CssAssets {
        public sealed class Image { public int Width, Height; public byte[] Rgba; }

        public readonly Dictionary<int, Image> Tiles = new Dictionary<int, Image>();
        public readonly Dictionary<(int, int), Image> Portraits = new Dictionary<(int, int), Image>();
        public readonly Dictionary<(int, int), Image> Stocks = new Dictionary<(int, int), Image>();

        public static CssAssets Parse(byte[] b) {
            if (b.Length < 4 || b[0] != 'C' || b[1] != 'S' || b[2] != 'S' || b[3] != '1') throw new InvalidDataException("not a css.bin");
            var a = new CssAssets();
            int o = 4;
            foreach (var section in new[] { "tiles", "portraits", "stocks" }) {
                if (o + 12 > b.Length) throw new InvalidDataException("css.bin is cut short");
                int w = BitConverter.ToInt32(b, o), h = BitConverter.ToInt32(b, o + 4), n = BitConverter.ToInt32(b, o + 8);
                o += 12;
                if (w <= 0 || h <= 0 || w > 1024 || h > 1024 || n < 0 || n > 1024) throw new InvalidDataException("css.bin has an impossible section");
                for (int i = 0; i < n; i++) {
                    int size = w * h * 4;
                    if (o + 4 + size > b.Length) throw new InvalidDataException("css.bin is cut short");
                    int character = b[o], costume = b[o + 1];
                    var img = new Image { Width = w, Height = h, Rgba = new byte[size] };
                    Buffer.BlockCopy(b, o + 4, img.Rgba, 0, size);
                    o += 4 + size;
                    if (section == "tiles") a.Tiles[character] = img;
                    else if (section == "portraits") a.Portraits[(character, costume)] = img;
                    else a.Stocks[(character, costume)] = img;
                }
            }
            if (o != b.Length) throw new InvalidDataException("css.bin has trailing bytes");
            return a;
        }

        /// How many costumes css.bin holds for a character (its portraits): 0 when it has none.
        public int CostumesOf(int character) {
            int n = 0;
            while (Portraits.ContainsKey((character, n))) n++;
            return n;
        }
    }
}
