using System;
using System.IO;
using UnityEngine;

namespace FoxMelee {
    /// The bench's character select, styled after Melee's: the 26 characters in Melee's layout with their stock icons
    /// (from the player's ISO: stocks.bin), the ones not owned dark, a cursor and Player 1's panel with the choice's name.
    sealed class CharacterSelect {
        readonly Texture2D[] icons = new Texture2D[26];
        readonly bool haveIcons;
        Texture2D white;

        public CharacterSelect(Action<string> log) {
            var path = Path.Combine(FoxMod.ModDir, "stocks.bin");
            try {
                var s = StockIcons.Parse(File.ReadAllBytes(path));
                for (int i = 0; i < icons.Length && i < s.Rgba.Length; i++) icons[i] = Texture(s.Rgba[i], s.Width, s.Height);
                haveIcons = true;
            } catch (Exception e) { log($"stocks.bin not used ({e.Message}): characters are shown by name"); }
        }

        /// A Unity texture of an RGBA image stored top row first.
        public static Texture2D Texture(byte[] rgba, int w, int h) {
            var flipped = new byte[rgba.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.LoadRawTextureData(flipped);
            t.Apply(false, true);
            return t;
        }

        public Texture2D Icon(Character c) => haveIcons && c.MeleeId >= 0 && c.MeleeId < icons.Length ? icons[c.MeleeId] : null;

        public void Draw(CharacterGrid grid, Func<Character, bool> owned) {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            float sw = Screen.width, sh = Screen.height, u = sh / 1080f;   // laid out for 1080p, scaled
            Rect(0, 0, sw, sh, new Color(0.02f, 0.02f, 0.06f, 0.88f));
            Rect(0, sh * 0.06f, sw, 4 * u, new Color(0.85f, 0.75f, 0.3f, 0.9f));
            var title = Style(56 * u, FontStyle.BoldAndItalic, TextAnchor.MiddleCenter);
            Shadowed(new Rect(0, sh * 0.07f, sw, 90 * u), "CHOOSE YOUR CHARACTER", title, new Color(1f, 0.92f, 0.55f));
            // the grid
            float slot = 104 * u, gap = 10 * u, top = sh * 0.2f;
            Character current = grid.Current;
            for (int r = 0; r < CharacterGrid.Rows.Count; r++) {
                var row = CharacterGrid.Rows[r];
                float width = row.Count * slot + (row.Count - 1) * gap, x0 = (sw - width) / 2, y = top + r * (slot + gap);
                for (int c = 0; c < row.Count; c++) {
                    var ch = row[c];
                    float x = x0 + c * (slot + gap);
                    bool has = owned(ch), here = r == grid.Row && c == grid.Column;
                    Rect(x - 3 * u, y - 3 * u, slot + 6 * u, slot + 6 * u, here ? new Color(1f, 0.85f, 0.2f) : new Color(0.25f, 0.25f, 0.3f));
                    Rect(x, y, slot, slot, has ? new Color(0.16f, 0.17f, 0.24f) : new Color(0.08f, 0.08f, 0.1f));
                    var icon = Icon(ch);
                    if (icon != null) {
                        GUI.color = has ? Color.white : new Color(0, 0, 0, 0.85f);   // not owned: a silhouette
                        GUI.DrawTexture(new Rect(x + 8 * u, y + 8 * u, slot - 16 * u, slot - 16 * u), icon, ScaleMode.ScaleToFit);
                        GUI.color = Color.white;
                    } else {
                        Shadowed(new Rect(x, y, slot, slot), Short(ch), Style(18 * u, FontStyle.Bold, TextAnchor.MiddleCenter), has ? Color.white : Color.gray);
                    }
                    if (!has) Shadowed(new Rect(x, y, slot, slot), "?", Style(44 * u, FontStyle.Bold, TextAnchor.MiddleCenter), new Color(0.5f, 0.5f, 0.55f));
                    if (here) {   // Player 1's token on the slot, as Melee's coin
                        float t = 26 * u, bob = Mathf.Sin(Time.unscaledTime * 6f) * 3 * u;
                        Rect(x + slot - t, y - t / 2 + bob, t * 1.4f, t, new Color(0.85f, 0.12f, 0.12f));
                        Shadowed(new Rect(x + slot - t, y - t / 2 + bob, t * 1.4f, t), "P1", Style(18 * u, FontStyle.Bold, TextAnchor.MiddleCenter), Color.white);
                    }
                }
            }
            // Player 1's panel: the choice, big
            float pw = 520 * u, ph = 170 * u, px = (sw - pw) / 2, py = top + 3 * (slot + gap) + 30 * u;
            Rect(px - 4 * u, py - 4 * u, pw + 8 * u, ph + 8 * u, new Color(0.95f, 0.85f, 0.4f));
            Rect(px, py, pw, ph, new Color(0.55f, 0.08f, 0.08f));
            var big = Icon(current);
            bool own = owned(current);
            if (big != null) {
                GUI.color = own ? Color.white : new Color(0, 0, 0, 0.85f);
                GUI.DrawTexture(new Rect(px + 20 * u, py + 15 * u, ph - 30 * u, ph - 30 * u), big, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            }
            Shadowed(new Rect(px + ph, py + 10 * u, pw - ph - 10 * u, 90 * u), own ? current.Name.ToUpperInvariant() : "???", Style(44 * u, FontStyle.BoldAndItalic, TextAnchor.MiddleLeft), Color.white);
            Shadowed(new Rect(px + ph, py + 95 * u, pw - ph - 10 * u, 60 * u), own ? "P1" : (current.IselaShop ? "Sold by Iselda in Dirtmouth" : "Find its charm"),
                     Style(24 * u, FontStyle.Bold, TextAnchor.MiddleLeft), new Color(1f, 0.85f, 0.85f));
            Shadowed(new Rect(0, sh - 70 * u, sw, 50 * u), "A: play     B: back     Stick / D-pad: choose", Style(24 * u, FontStyle.Normal, TextAnchor.MiddleCenter), new Color(0.85f, 0.85f, 0.9f));
        }

        /// A character change loading (the core restarting): the choice, and that it's on its way.
        public void DrawLoading(Character c) {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            float sw = Screen.width, sh = Screen.height, u = sh / 1080f;
            Rect(0, 0, sw, sh, new Color(0.02f, 0.02f, 0.06f, 0.88f));
            var icon = Icon(c);
            if (icon != null) GUI.DrawTexture(new Rect((sw - 200 * u) / 2, sh * 0.32f, 200 * u, 200 * u), icon, ScaleMode.ScaleToFit);
            Shadowed(new Rect(0, sh * 0.32f + 220 * u, sw, 80 * u), c.Name.ToUpperInvariant(), Style(56 * u, FontStyle.BoldAndItalic, TextAnchor.MiddleCenter), new Color(1f, 0.92f, 0.55f));
            Shadowed(new Rect(0, sh * 0.32f + 300 * u, sw, 50 * u), "Ready...", Style(30 * u, FontStyle.Italic, TextAnchor.MiddleCenter), Color.white);
        }

        static string Short(Character c) => c.Name.Replace("Captain ", "C. ").Replace("Donkey Kong", "DK").Replace("Game & Watch", "G&W").Replace("Ice Climbers", "ICs");

        void Rect(float x, float y, float w, float h, Color c) { GUI.color = c; GUI.DrawTexture(new Rect(x, y, w, h), white); GUI.color = Color.white; }

        static GUIStyle Style(float size, FontStyle f, TextAnchor a) =>
            new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(8, (int)size), fontStyle = f, alignment = a, wordWrap = false };

        static void Shadowed(Rect r, string text, GUIStyle s, Color c) {
            var sh = new Rect(r.x + 3, r.y + 3, r.width, r.height);
            GUI.color = new Color(0, 0, 0, 0.8f); GUI.Label(sh, text, s);
            GUI.color = c; GUI.Label(r, text, s);
            GUI.color = Color.white;
        }
    }
}
