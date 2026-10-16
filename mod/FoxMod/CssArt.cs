using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FoxMelee {
    /// Melee's select art from the player's ISO (css.bin: each character's tile with its name plate, every costume's
    /// portrait and stock icon; stocks.bin's icons when css.bin is missing), as Unity textures and sprites for the
    /// inventory's Characters page; and the card shown while a character change loads.
    sealed class CssArt {
        readonly Texture2D[] icons = new Texture2D[26];
        readonly bool haveIcons;
        readonly CssAssets css;
        readonly Dictionary<(string, int, bool), Texture2D> textures = new Dictionary<(string, int, bool), Texture2D>();
        Texture2D white;

        public CssArt(Action<string> log) {
            try {
                var s = StockIcons.Parse(File.ReadAllBytes(Path.Combine(FoxMod.ModDir, "stocks.bin")));
                for (int i = 0; i < icons.Length && i < s.Rgba.Length; i++) icons[i] = Texture(s.Rgba[i], s.Width, s.Height);
                haveIcons = true;
            } catch (Exception e) { log($"stocks.bin not used ({e.Message})"); }
            try { css = CssAssets.Parse(File.ReadAllBytes(Path.Combine(FoxMod.ModDir, "css.bin"))); }
            catch (Exception e) { log($"css.bin not used ({e.Message}): the Characters page shows stock icons"); }
        }

        /// A Unity texture of an RGBA image stored top row first; dark: a locked character's (a quarter of the light).
        public static Texture2D Texture(byte[] rgba, int w, int h, bool smooth = false, bool dark = false) {
            var flipped = new byte[rgba.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
            if (dark) for (int i = 0; i < flipped.Length; i += 4) { flipped[i] /= 4; flipped[i + 1] /= 4; flipped[i + 2] /= 4; }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = smooth ? FilterMode.Bilinear : FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.LoadRawTextureData(flipped);
            t.Apply(false, true);
            return t;
        }

        Texture2D Cached(string kind, int key, bool dark, CssAssets.Image img, bool smooth) {
            if (img == null) return null;
            if (!textures.TryGetValue((kind, key, dark), out var t)) textures[(kind, key, dark)] = t = Texture(img.Rgba, img.Width, img.Height, smooth, dark);
            return t;
        }

        Texture2D IconOf(Character c, bool dark) {
            if (!haveIcons || c.MeleeId < 0 || c.MeleeId >= icons.Length || icons[c.MeleeId] == null) return null;
            if (!dark) return icons[c.MeleeId];
            if (!textures.TryGetValue(("icon", c.MeleeId, true), out var t)) {
                var src = icons[c.MeleeId];
                var px = src.GetPixels32();
                for (int i = 0; i < px.Length; i++) { px[i].r /= 4; px[i].g /= 4; px[i].b /= 4; }
                t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                t.SetPixels32(px); t.Apply(false, true);
                textures[("icon", c.MeleeId, true)] = t;
            }
            return t;
        }

        /// The character's tile (Melee's select: the face and its name plate); its stock icon without css.bin.
        public Texture2D Tile(Character c, bool dark) =>
            css != null && css.Tiles.TryGetValue(c.MeleeId, out var img) ? Cached("tile", c.MeleeId, dark, img, true) : IconOf(c, dark);

        public Texture2D Portrait(Character c, int costume, bool dark) {
            if (css == null) return IconOf(c, dark);
            int id = c.MeleeId;
            if (c.Name == "Sheik" && !css.Portraits.ContainsKey((19, 0))) id = 18;   // css.bin made with no renders of her: Zelda's, as Melee's select
            if (!css.Portraits.TryGetValue((id, costume), out var img) && !css.Portraits.TryGetValue((id, 0), out img)) return IconOf(c, dark);
            return Cached("portrait", id * 16 + costume, dark, img, true);
        }

        public Texture2D Stock(Character c, int costume, bool dark) =>
            css != null && css.Stocks.TryGetValue((c.MeleeId, costume), out var img) ? Cached("stock", c.MeleeId * 16 + costume, dark, img, false) : IconOf(c, dark);

        readonly Dictionary<(Texture2D, float), Sprite> sprites = new Dictionary<(Texture2D, float), Sprite>();
        /// A sprite of the texture, `height` units tall, centred.
        public Sprite Sprite(Texture2D t, float height) {
            if (t == null) return null;
            if (!sprites.TryGetValue((t, height), out var s))
                sprites[(t, height)] = s = UnityEngine.Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.height / height);
            return s;
        }

        static readonly Color Gold = new Color(1f, 0.85f, 0.25f);

        /// A character change loading (the core restarting): the choice's portrait, and that it's on its way.
        public void DrawLoading(Character c, int costume) {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            float sw = Screen.width, sh = Screen.height, u = sh / 1080f;
            const int bands = 12;
            for (int i = 0; i < bands; i++) {
                float f = (float)i / (bands - 1);
                GUI.color = new Color(0.03f + 0.07f * (1 - f), 0.05f + 0.1f * (1 - f), 0.2f + 0.25f * (1 - f), 0.93f);
                GUI.DrawTexture(new Rect(0, sh * i / bands, sw, sh / bands + 1), white);
            }
            GUI.color = Color.white;
            var big = Portrait(c, costume, false);
            float h = 376 * u, w = h * 136f / 188f, y = sh * 0.2f;
            if (big != null) GUI.DrawTexture(new Rect((sw - w) / 2, y, w, h), big, ScaleMode.ScaleToFit);
            Shadowed(new Rect(0, y + h + 20 * u, sw, 80 * u), c.Name.ToUpperInvariant(), Style(56 * u, FontStyle.BoldAndItalic), Gold);
            Shadowed(new Rect(0, y + h + 100 * u, sw, 50 * u), "Ready...", Style(30 * u, FontStyle.Italic), Color.white);
        }

        static GUIStyle Style(float size, FontStyle f) =>
            new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(8, (int)size), fontStyle = f, alignment = TextAnchor.MiddleCenter, wordWrap = false };

        static void Shadowed(Rect r, string text, GUIStyle s, Color c) {
            GUI.color = new Color(0, 0, 0, 0.8f); GUI.Label(new Rect(r.x + 3, r.y + 3, r.width, r.height), text, s);
            GUI.color = c; GUI.Label(r, text, s);
            GUI.color = Color.white;
        }
    }
}
