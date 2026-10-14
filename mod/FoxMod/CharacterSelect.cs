using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FoxMelee {
    /// The bench's character select, drawn as Melee's: the characters in Melee's layout on its tiles (the faces with their
    /// name plates, from the player's ISO: css.bin), the ones not owned dark with a "?", Player 1's coin on the choice, and
    /// below it Player 1's red panel with the choice's portrait in the costume picked (X / Y turn it, as Melee's costume
    /// buttons) and its name; under that the row of Hollow Knight's charms that are still charms (CharmRow). Without
    /// css.bin the stock icons (stocks.bin) stand in; without those, names.
    sealed class CharacterSelect {
        readonly Texture2D[] icons = new Texture2D[26];
        readonly bool haveIcons;
        CssAssets css;
        readonly Dictionary<int, Texture2D> tiles = new Dictionary<int, Texture2D>();
        readonly Dictionary<(int, int), Texture2D> portraits = new Dictionary<(int, int), Texture2D>(), stocks = new Dictionary<(int, int), Texture2D>();
        Texture2D white;

        public CharacterSelect(Action<string> log) {
            try {
                var s = StockIcons.Parse(File.ReadAllBytes(Path.Combine(FoxMod.ModDir, "stocks.bin")));
                for (int i = 0; i < icons.Length && i < s.Rgba.Length; i++) icons[i] = Texture(s.Rgba[i], s.Width, s.Height);
                haveIcons = true;
            } catch (Exception e) { log($"stocks.bin not used ({e.Message}): characters are shown by name"); }
            try { css = CssAssets.Parse(File.ReadAllBytes(Path.Combine(FoxMod.ModDir, "css.bin"))); }
            catch (Exception e) { log($"css.bin not used ({e.Message}): the select shows stock icons"); }
        }

        /// A Unity texture of an RGBA image stored top row first.
        public static Texture2D Texture(byte[] rgba, int w, int h, bool smooth = false) {
            var flipped = new byte[rgba.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = smooth ? FilterMode.Bilinear : FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            t.LoadRawTextureData(flipped);
            t.Apply(false, true);
            return t;
        }

        public Texture2D Icon(Character c) => haveIcons && c.MeleeId >= 0 && c.MeleeId < icons.Length ? icons[c.MeleeId] : null;

        Texture2D Tile(Character c) {
            if (css == null || !css.Tiles.TryGetValue(c.MeleeId, out var img)) return null;
            if (!tiles.TryGetValue(c.MeleeId, out var t)) tiles[c.MeleeId] = t = Texture(img.Rgba, img.Width, img.Height, true);
            return t;
        }
        Texture2D Portrait(Character c, int costume) {
            if (css == null) return null;
            int id = c.MeleeId;
            if (c.Name == "Sheik" && !css.Portraits.ContainsKey((19, 0))) id = 18;   // without her own (css.bin made with no renders of her): Zelda's, as Melee's select
            if (!css.Portraits.TryGetValue((id, costume), out var img) && !css.Portraits.TryGetValue((id, 0), out img)) return null;
            if (!portraits.TryGetValue((id, costume), out var t)) portraits[(id, costume)] = t = Texture(img.Rgba, img.Width, img.Height, true);
            return t;
        }
        Texture2D Stock(Character c, int costume) {
            if (css == null || !css.Stocks.TryGetValue((c.MeleeId, costume), out var img)) return Icon(c);
            if (!stocks.TryGetValue((c.MeleeId, costume), out var t)) stocks[(c.MeleeId, costume)] = t = Texture(img.Rgba, img.Width, img.Height);
            return t;
        }

        static readonly Color P1 = new Color(0.78f, 0.1f, 0.1f), Gold = new Color(1f, 0.85f, 0.25f);

        public void Draw(CharacterGrid grid, Func<Character, bool> owned, Func<Character, int> costumeOf, CharmRow charms, bool opaque) {
            Backdrop(out float sw, out float sh, out float u, opaque);
            // the grid: Melee's rows of tiles, Player 1's coin on the choice
            float slot = 112 * u, th = slot * 56 / 64, gap = 6 * u, top = sh * 0.09f;
            Character current = grid.Current;
            bool onGrid = !charms.Active;
            for (int r = 0; r < CharacterGrid.Rows.Count; r++) {
                var row = CharacterGrid.Rows[r];
                float width = row.Count * slot + (row.Count - 1) * gap, x0 = (sw - width) / 2, y = top + r * (th + gap);
                for (int c = 0; c < row.Count; c++) {
                    var ch = row[c];
                    float x = x0 + c * (slot + gap);
                    bool has = owned(ch), here = onGrid && r == grid.Row && c == grid.Column;
                    Rect(x - 2 * u, y - 2 * u, slot + 4 * u, th + 4 * u, here ? Gold : new Color(0.1f, 0.12f, 0.2f));
                    var tile = Tile(ch);
                    if (tile != null) {
                        GUI.color = has ? Color.white : new Color(0.25f, 0.25f, 0.3f, 1f);
                        GUI.DrawTexture(new Rect(x, y, slot, th), tile, ScaleMode.StretchToFill);
                        GUI.color = Color.white;
                    } else {   // no tile (Sheik, or no css.bin): a dark plate with the stock icon and the name
                        Rect(x, y, slot, th, has ? new Color(0.16f, 0.17f, 0.24f) : new Color(0.08f, 0.08f, 0.1f));
                        var icon = Icon(ch);
                        if (icon != null) {
                            GUI.color = has ? Color.white : new Color(0.3f, 0.3f, 0.35f);
                            GUI.DrawTexture(new Rect(x + slot / 2 - 20 * u, y + 6 * u, 40 * u, 40 * u), icon, ScaleMode.ScaleToFit);
                            GUI.color = Color.white;
                        }
                        Shadowed(new Rect(x, y + th - 26 * u, slot, 24 * u), Short(ch).ToUpperInvariant(), Style(15 * u, FontStyle.Bold, TextAnchor.MiddleCenter), has ? Color.white : Color.gray);
                    }
                    if (!has) Shadowed(new Rect(x, y, slot, th), "?", Style(48 * u, FontStyle.Bold, TextAnchor.MiddleCenter), new Color(0.75f, 0.75f, 0.8f));
                    if (here) {   // Player 1's coin, bobbing on the tile
                        float t = 30 * u, bob = Mathf.Sin(Time.unscaledTime * 6f) * 3 * u, cx = x + slot - t * 0.7f, cy = y - t * 0.4f + bob;
                        Rect(cx - 2 * u, cy - 2 * u, t + 4 * u, t + 4 * u, new Color(0.3f, 0.02f, 0.02f));
                        Rect(cx, cy, t, t, P1);
                        Shadowed(new Rect(cx, cy, t, t), "P1", Style(15 * u, FontStyle.Bold, TextAnchor.MiddleCenter), Color.white);
                    }
                }
            }
            // Player 1's panel: the portrait in its costume, the name, the costumes as stock icons
            bool own = owned(current);
            int costume = own ? costumeOf(current) : 0;
            float ph = 220 * u, pw = 620 * u, px = (sw - pw) / 2, py = top + 3 * (th + gap) + 20 * u;
            Rect(px - 4 * u, py - 4 * u, pw + 8 * u, ph + 8 * u, new Color(0.25f, 0.02f, 0.02f));
            Rect(px, py, pw, ph, P1);
            Rect(px, py + ph - 46 * u, pw, 46 * u, new Color(0.45f, 0.05f, 0.05f));
            var big = Portrait(current, costume);
            float portW = ph * 136f / 188f;
            if (big != null) {
                GUI.color = own ? Color.white : new Color(0.1f, 0.1f, 0.12f, 1f);
                GUI.DrawTexture(new Rect(px + 10 * u, py + 2 * u, portW, ph - 4 * u), big, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            } else {
                var icon = Icon(current);
                if (icon != null) {
                    GUI.color = own ? Color.white : new Color(0, 0, 0, 0.85f);
                    GUI.DrawTexture(new Rect(px + 20 * u, py + 20 * u, ph - 80 * u, ph - 80 * u), icon, ScaleMode.ScaleToFit);
                    GUI.color = Color.white;
                }
                portW = ph - 60 * u;
            }
            float tx = px + portW + 24 * u, tw = pw - portW - 34 * u;
            Shadowed(new Rect(tx, py + 18 * u, tw, 70 * u), own ? current.Name.ToUpperInvariant() : "???", Style(46 * u, FontStyle.BoldAndItalic, TextAnchor.MiddleLeft), Color.white);
            if (own) {
                // the costumes: a stock icon each, the chosen one up and bright
                float ix = tx, iy = py + 100 * u, isz = 44 * u;
                for (int k = 0; k < current.Costumes; k++) {
                    var st = Stock(current, k);
                    bool chosen = k == costume;
                    Rect(ix - 2 * u, iy - (chosen ? 8 * u : 0) - 2 * u, isz + 4 * u, isz + 4 * u, chosen ? Gold : new Color(0.35f, 0.04f, 0.04f));
                    Rect(ix, iy - (chosen ? 8 * u : 0), isz, isz, new Color(0.12f, 0.02f, 0.02f));
                    if (st != null) {
                        GUI.color = chosen ? Color.white : new Color(0.7f, 0.7f, 0.7f);
                        GUI.DrawTexture(new Rect(ix + 4 * u, iy - (chosen ? 8 * u : 0) + 4 * u, isz - 8 * u, isz - 8 * u), st, ScaleMode.ScaleToFit);
                        GUI.color = Color.white;
                    }
                    ix += isz + 10 * u;
                }
            } else {
                Shadowed(new Rect(tx, py + 100 * u, tw, 60 * u), current.IselaShop ? "Sold by Iselda in Dirtmouth" : "Find its charm", Style(24 * u, FontStyle.Bold, TextAnchor.MiddleLeft), new Color(1f, 0.85f, 0.85f));
            }
            CharmRowDraw(charms, sw, sh, u, py + ph + 26 * u);
        }

        /// The charms that are still charms, under the panel: Hollow Knight's icons, the equipped ones bright with a gold
        /// frame, their notch cost as pips, the cursor's one named; the notches in use at the right.
        void CharmRowDraw(CharmRow charms, float sw, float sh, float u, float y) {
            int n = charms.Charms.Count;
            if (n == 0) return;
            float sz = 64 * u, gap = 14 * u, width = n * sz + (n - 1) * gap;
            if (width > sw - 200 * u) { sz = (sw - 200 * u - (n - 1) * gap) / n; width = n * sz + (n - 1) * gap; }
            float x0 = (sw - width) / 2;
            string name = null; int cost = 0;
            for (int i = 0; i < n; i++) {
                var v = CharmScreen.View(charms.Charms[i]);
                bool here = charms.Active && charms.Index == i;
                float x = x0 + i * (sz + gap), bob = here ? Mathf.Sin(Time.unscaledTime * 6f) * 2 * u : 0;
                Rect(x - 3 * u, y - 3 * u - bob, sz + 6 * u, sz + 6 * u, here ? Color.white : v.Equipped ? Gold : new Color(0.1f, 0.12f, 0.2f));
                Rect(x, y - bob, sz, sz, v.Equipped ? new Color(0.22f, 0.2f, 0.12f) : new Color(0.08f, 0.09f, 0.14f));
                if (v.Tex != null) {
                    GUI.color = v.Equipped || here ? Color.white : new Color(0.55f, 0.55f, 0.6f, 1f);
                    GUI.DrawTextureWithTexCoords(new Rect(x + 4 * u, y - bob + 4 * u, sz - 8 * u, sz - 8 * u), v.Tex, v.Uv);
                    GUI.color = Color.white;
                } else Shadowed(new Rect(x, y - bob, sz, sz), v.Id.ToString(), Style(20 * u, FontStyle.Bold, TextAnchor.MiddleCenter), Color.white);
                for (int k = 0; k < v.Cost; k++) Rect(x + 4 * u + k * 10 * u, y + sz + 5 * u, 7 * u, 7 * u, v.Equipped ? Gold : new Color(0.5f, 0.45f, 0.3f));
                if (here) { name = v.Name ?? ("Charm " + v.Id); cost = v.Cost; }
            }
            var (filled, slots) = CharmScreen.Notches();
            for (int k = 0; k < slots; k++) Rect(sw - 100 * u - (slots - k) * 16 * u, y + sz / 2 - 6 * u, 12 * u, 12 * u, k < filled ? Gold : new Color(0.25f, 0.25f, 0.3f));
            if (name != null) Shadowed(new Rect(0, y + sz + 20 * u, sw, 40 * u), name, Style(26 * u, FontStyle.Bold, TextAnchor.MiddleCenter), Color.white);
        }

        /// A character change loading (the core restarting): the choice's portrait, and that it's on its way.
        public void DrawLoading(Character c, int costume) {
            Backdrop(out float sw, out float sh, out float u);
            var big = Portrait(c, costume);
            float h = 376 * u, w = h * 136f / 188f, y = sh * 0.2f;
            if (big != null) GUI.DrawTexture(new Rect((sw - w) / 2, y, w, h), big, ScaleMode.ScaleToFit);
            else { var icon = Icon(c); if (icon != null) GUI.DrawTexture(new Rect((sw - 200 * u) / 2, y + 80 * u, 200 * u, 200 * u), icon, ScaleMode.ScaleToFit); }
            Shadowed(new Rect(0, y + h + 20 * u, sw, 80 * u), c.Name.ToUpperInvariant(), Style(56 * u, FontStyle.BoldAndItalic, TextAnchor.MiddleCenter), Gold);
            Shadowed(new Rect(0, y + h + 100 * u, sw, 50 * u), "Ready...", Style(30 * u, FontStyle.Italic, TextAnchor.MiddleCenter), Color.white);
        }

        /// Melee's deep blue, darker towards the bottom, over the room (or over Hollow Knight's charm screen: then solid).
        void Backdrop(out float sw, out float sh, out float u, bool opaque = false) {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            sw = Screen.width; sh = Screen.height; u = sh / 1080f;
            const int bands = 12;
            for (int i = 0; i < bands; i++) {
                float f = (float)i / (bands - 1);
                Rect(0, sh * i / bands, sw, sh / bands + 1, new Color(0.03f + 0.07f * (1 - f), 0.05f + 0.1f * (1 - f), 0.2f + 0.25f * (1 - f), opaque ? 1f : 0.93f));
            }
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
