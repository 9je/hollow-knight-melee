using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace FoxMelee {
    /// The inventory's Characters page, made of Hollow Knight's own Charms page: its frame, cursor, fade, the name and
    /// description under the big picture at the right and the button prompt stay; the charms go. Melee's tiles fill the
    /// charm grid in Melee's three rows, the character under the cursor is the big picture (its portrait), and the row
    /// where equipped charms sit is that character's colors. Left and right past the grid reach the page arrows (another
    /// page), up from the grid reaches the colors; Confirm plays the character, at a bench only, as charms are equipped.
    /// Hollow Knight's own inputs drive it (any controller or the keyboard), plus X / Y to turn the color as in Melee.
    sealed class CharacterPaneUi {
        public const string NameKey = "FOXMELEE_CHAR_NAME", DescKey = "FOXMELEE_CHAR_DESC", PageKey = "PANE_FOXMELEE_CHARACTERS",
                            ColorKey = "FOXMELEE_COLOR", ChooseKey = "FOXMELEE_CHOOSE";
        /// What Hollow Knight's text shows for this page's keys (FoxMod's language hook).
        public static string NameText = "", DescText = "";
        public static string Language(string key, string sheet, string orig) {
            switch (key) {
                case NameKey: return NameText;
                case DescKey: return DescText;
                case PageKey: return "Characters";
                case ColorKey: return "Color";
                case ChooseKey: return "Choose";
                default: return orig;
            }
        }

        public readonly GameObject Page;
        readonly PlayMakerFSM inv;
        readonly CssArt art;
        readonly Action<string> log;
        readonly GameObject[][] tiles;
        readonly SpriteRenderer[][] tileArt;
        readonly GameObject[] colors;
        readonly SpriteRenderer[] colorArt;
        readonly SpriteRenderer portrait;
        readonly PlayMakerFSM cursorFsm, textFsm;
        readonly PlayMakerFSM[] cursorObjFsms;
        readonly GameObject confirm;
        readonly float tileH, colorH, portraitH;

        enum Spot { Grid, Colors, ArrowL, ArrowR }
        Spot spot;
        CharacterGrid grid;
        int colorIndex;
        bool shown, active;
        readonly Dictionary<Character, int> costumePick = new Dictionary<Character, int>();
        HashSet<Character> owned = new HashSet<Character>();
        int repeatDx, repeatDy; float repeatAt;
        Character playing = Roster.Fox;               // who plays, and in which color (Update's)
        Func<Character, int> costumeOf = c => 0;

        public struct Pick { public Character Character; public int Costume; }

        CharacterPaneUi(GameObject page, PlayMakerFSM inv, CssArt art, Action<string> log, GameObject[][] tiles, SpriteRenderer[][] tileArt,
                        GameObject[] colors, SpriteRenderer[] colorArt, SpriteRenderer portrait, PlayMakerFSM cursorFsm, PlayMakerFSM textFsm,
                        PlayMakerFSM[] cursorObjFsms, GameObject confirm, float tileH, float colorH, float portraitH) {
            Page = page; this.inv = inv; this.art = art; this.log = log; this.tiles = tiles; this.tileArt = tileArt;
            this.colors = colors; this.colorArt = colorArt; this.portrait = portrait; this.cursorFsm = cursorFsm; this.textFsm = textFsm;
            this.cursorObjFsms = cursorObjFsms; this.confirm = confirm; this.tileH = tileH; this.colorH = colorH; this.portraitH = portraitH;
        }

        /// Builds the page from a copy of the Charms page (call while the inventory is closed: its pages are put away then).
        /// Null, logged, when the Charms page isn't as expected; then there is no Characters page.
        public static CharacterPaneUi Build(PlayMakerFSM inv, CssArt art, Action<string> log) {
            var root = inv.gameObject.transform;
            var charms = root.Find("Charms");
            if (charms == null) { log("inventory: no Charms page to make the Characters page of"); return null; }
            if (charms.gameObject.activeSelf) return null;   // not put away yet: next time
            var page = UnityEngine.Object.Instantiate(charms.gameObject, root);
            page.name = "Characters";
            page.SetActive(false);
            try {
                var t = page.transform;
                // the charm grid's slots and the equipped row, before the charms go
                var bbs = t.Find("Backboards")?.Cast<Transform>().ToList() ?? new List<Transform>();
                if (bbs.Count < 10) throw new Exception($"the charm grid has {bbs.Count} slots");
                var slots = bbs.Select(b => t.InverseTransformPoint(b.position)).ToList();
                var rowYs = slots.Select(p => (float)Math.Round(p.y, 2)).Distinct().OrderByDescending(y => y).ToList();
                float left = slots.Min(p => p.x), right = slots.Max(p => p.x);
                float rowStep = rowYs.Count > 1 ? rowYs[0] - rowYs[1] : 1.45f;
                var bbArt = bbs[0].GetComponent<SpriteRenderer>();
                int layerId = bbArt != null ? bbArt.sortingLayerID : 0, order = bbArt != null ? bbArt.sortingOrder + 1 : 1;
                var nextDot = t.Find("Equipped Charms/Next Dot");
                Vector3 colorsAt = nextDot != null ? t.InverseTransformPoint(nextDot.position) : new Vector3(left, rowYs[0] + 4 * rowStep, 0);
                var detail = t.Find("Details/Detail Sprite")?.GetComponent<SpriteRenderer>();
                if (detail == null) throw new Exception("no Details/Detail Sprite");

                foreach (var path in new[] { "Backboards", "Collected Charms", "Tweener Charm", "Overcharm", "Grimm Flame UI",
                                             "Equipped Charms/Charms", "Equipped Charms/Next Dot", "Equipped Charms/Notches",
                                             "Equipped Charms/Text Overcharmed", "Details/Cost" }) {
                    var c = t.Find(path);
                    if (c != null) UnityEngine.Object.DestroyImmediate(c.gameObject);
                }
                foreach (var b in page.GetComponentsInChildren<BuildEquippedCharms>(true)) UnityEngine.Object.DestroyImmediate(b);   // it builds the equipped row
                foreach (var b in page.GetComponentsInChildren<CharmVibrations>(true)) UnityEngine.Object.DestroyImmediate(b);
                var cursor = t.Find("Cursor");
                var text = new[] { t.Find("Text Name"), t.Find("Text Desc") };
                // only the cursor's and the text's state machines stay (the charm screen's own would run the charms)
                foreach (var f in page.GetComponentsInChildren<PlayMakerFSM>(true)) {
                    bool keep = f.FsmName == "Update Cursor" || f.FsmName == "Update Text"
                             || (cursor != null && f.transform.IsChildOf(cursor)) || text.Any(x => x != null && f.transform.IsChildOf(x));
                    if (!keep) { log($"Characters page: dropped {f.FsmName} on {f.gameObject.name}"); UnityEngine.Object.DestroyImmediate(f); }
                }
                var cursorFsm = page.GetComponentsInChildren<PlayMakerFSM>(true).FirstOrDefault(f => f.FsmName == "Update Cursor");
                var textFsm = page.GetComponentsInChildren<PlayMakerFSM>(true).FirstOrDefault(f => f.FsmName == "Update Text");
                if (cursorFsm == null || textFsm == null || cursor == null) throw new Exception("no cursor or text state machine");

                // the tiles, centred in the charm grid (Melee's 9 / 10 / 7), a row's height apart from the grid's top row
                var counts = CharacterGrid.Rows.Select(r => r.Count).ToArray();
                // the slots' spacing within a row (alternate rows are offset by half a slot), centred on the grid
                float step = slots.GroupBy(p => (float)Math.Round(p.y, 2))
                    .SelectMany(g => g.Select(p => p.x).OrderBy(x => x).Zip(g.Select(p => p.x).OrderBy(x => x).Skip(1), (a, b) => b - a))
                    .Where(d => d > 0.1f).DefaultIfEmpty(1.5f).Min();
                float centre = (left + right) / 2, half = (counts.Max() - 1) / 2f * step;
                left = centre - half; right = centre + half;
                float tileW = step * 0.94f, tileH = tileW * 56f / 64f;
                float top = rowYs[0] - (rowYs.Count >= 4 ? rowStep * 0.5f : 0);   // three rows in the grid's four
                var at = CharacterPane.Layout(counts, left, right, top, rowStep);
                var tiles = new GameObject[counts.Length][];
                var tileArt = new SpriteRenderer[counts.Length][];
                for (int r = 0; r < counts.Length; r++) {
                    tiles[r] = new GameObject[counts[r]]; tileArt[r] = new SpriteRenderer[counts[r]];
                    for (int c = 0; c < counts[r]; c++) {
                        (tiles[r][c], tileArt[r][c]) = Item(page, $"Tile {CharacterGrid.Rows[r][c].Name}", new Vector3((float)at[r][c].X, (float)at[r][c].Y, 0), tileW, tileH, layerId, order);
                    }
                }
                // the colors: the equipped row's places, a grid step apart
                int maxCostumes = Roster.All.Max(c => c.Costumes);
                var colors = new GameObject[maxCostumes]; var colorArt = new SpriteRenderer[maxCostumes];
                float colorH = tileH * 0.9f;
                for (int k = 0; k < maxCostumes; k++)
                    (colors[k], colorArt[k]) = Item(page, $"Color {k + 1}", colorsAt + new Vector3(k * step, 0, 0), colorH, colorH, layerId, order);
                // the portrait where the charm's big picture was (1.98 units: 132 px at 100 per unit, scaled 1.5), half as
                // tall again (Melee's portraits are 136 x 188) and grown upwards, so the name under it stays clear
                var ds = detail.transform;
                float iconH = detail.sprite != null ? detail.sprite.bounds.size.y * ds.localScale.y : 1.98f;
                float tallH = iconH * 1.5f;
                ds.localPosition += new Vector3(0, (tallH - iconH) / 2, 0);   // in Details' units, as iconH
                float portraitH = tallH / Math.Max(0.01f, ds.localScale.y);

                // the fade: Hollow Knight fades the page's renderers in and out (FadeGroup); the charms' are gone, the tiles join
                var mine = tileArt.SelectMany(r => r).Concat(colorArt).ToArray();
                var groups = page.GetComponentsInChildren<FadeGroup>(true);
                foreach (var g in groups) {
                    g.spriteRenderers = (g.spriteRenderers ?? new SpriteRenderer[0]).Where(x => x != null).ToArray();
                    g.texts = (g.texts ?? new TextMeshPro[0]).Where(x => x != null).ToArray();
                    g.animators = (g.animators ?? new InvAnimateUpAndDown[0]).Where(x => x != null).ToArray();
                }
                var fade = page.GetComponent<FadeGroup>() ?? groups.FirstOrDefault();
                if (fade != null) fade.spriteRenderers = fade.spriteRenderers.Concat(mine).ToArray();
                else log("Characters page: no fade group (the tiles show without fading)");

                // the labels: "Equipped" says Color, the button prompt says Choose
                Relabel(t.Find("Equipped Charms/Text Equipped"), ColorKey);
                var confirm = t.Find("Confirm Action")?.gameObject;
                if (confirm != null) foreach (var tx in confirm.GetComponentsInChildren<SetTextMeshProGameText>(true)) { tx.sheetName = "UI"; tx.convName = ChooseKey; }

                var cursorObjFsms = cursor.GetComponents<PlayMakerFSM>();
                log($"Characters page made of the Charms page: {tiles.Sum(r => r.Length)} tiles {tileW:0.00} wide, {groups.Length} fade groups, cursor {cursorObjFsms.Length} state machines");
                return new CharacterPaneUi(page, inv, art, log, tiles, tileArt, colors, colorArt, detail, cursorFsm, textFsm, cursorObjFsms, confirm, tileH, colorH, portraitH);
            } catch (Exception e) {
                log("inventory: Characters page not made: " + e.Message);
                UnityEngine.Object.DestroyImmediate(page);
                return null;
            }
        }

        /// A sprite the cursor can land on (Hollow Knight's cursor sizes itself to the target's box collider).
        static (GameObject, SpriteRenderer) Item(GameObject page, string name, Vector3 at, float w, float h, int layerId, int order) {
            var go = new GameObject(name) { layer = page.layer };
            go.transform.SetParent(page.transform, false);
            go.transform.localPosition = at;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingLayerID = layerId; sr.sortingOrder = order;
            var box = go.AddComponent<BoxCollider2D>();
            box.isTrigger = true;
            box.size = new Vector2(w, h);
            return (go, sr);
        }

        static void Relabel(Transform label, string key) {
            var tx = label?.GetComponent<SetTextMeshProGameText>();
            if (tx == null) return;
            tx.sheetName = "UI"; tx.convName = key;
            tx.UpdateText();
        }

        /// Each frame. The page is live while the inventory is open on it; a pick (at a bench) comes back to be played.
        public Pick? Update(Character playing, Func<Character, int> costumeOf, Func<HashSet<Character>> ownedNow, bool atBench) {
            if (Page == null) return null;
            this.playing = playing; this.costumeOf = costumeOf;
            // shown (opening, or turned to): the page fills in; open (Opened, the fade done): the cursor and input are live
            var current = inv.FsmVariables.GetFsmGameObject("Current Pane")?.Value;
            var state = inv.ActiveStateName;
            bool nowShown = current == Page && state != "Closed";
            bool now = state == "Opened" && current == Page;
            if (nowShown && !shown) Refresh(playing, costumeOf, ownedNow());
            shown = nowShown;
            if (now && !active) { foreach (var f in cursorObjFsms) f.SendEvent("CURSOR ACTIVATE"); Show(); log($"Characters page open: {owned.Count} characters, playing {playing.Name}"); }
            if (!now && active) Deactivate();
            active = now;
            if (!active) return null;

            var ia = InputHandler.Instance?.inputActions;
            if (ia == null) return null;
            int dx = (ia.right.IsPressed ? 1 : 0) - (ia.left.IsPressed ? 1 : 0);
            int dy = (ia.down.IsPressed ? 1 : 0) - (ia.up.IsPressed ? 1 : 0);
            bool fresh = dx != repeatDx || dy != repeatDy;
            if (fresh) { repeatDx = dx; repeatDy = dy; repeatAt = Time.unscaledTime + 0.35f; if (dx != 0 || dy != 0) Move(dx, dy, true); }
            else if ((dx != 0 || dy != 0) && Time.unscaledTime >= repeatAt) { repeatAt = Time.unscaledTime + 0.12f; Move(dx, dy, false); }

            var dev = InControl.InputManager.ActiveDevice;
            if (spot == Spot.Grid || spot == Spot.Colors) {
                var ch = grid.Current;
                if (owned.Contains(ch) && dev != null && (dev.Action3.WasPressed || dev.Action4.WasPressed)) {   // Melee's color buttons
                    TurnColor(ch, dev.Action3.WasPressed ? 1 : -1);
                }
            }
            if (!ia.menuSubmit.WasPressed) return null;
            if (spot == Spot.ArrowL) { inv.SendEvent("MOVE PANE L"); return null; }
            if (spot == Spot.ArrowR) { inv.SendEvent("MOVE PANE R"); return null; }
            var pick = grid.Current;
            if (!CharacterPane.CanChoose(owned.Contains(pick), atBench, pick == playing, costumePick[pick] != costumeOf(pick))) return null;
            log($"Characters page: {pick.Name} chosen (color {costumePick[pick] + 1})");
            return new Pick { Character = pick, Costume = costumePick[pick] };

            void Move(int mx, int my, bool pressed) {
                switch (spot) {
                    case Spot.ArrowL:
                        if (mx < 0 && pressed) inv.SendEvent("MOVE PANE L");
                        else if (mx > 0) { spot = Spot.Grid; Show(); }
                        return;
                    case Spot.ArrowR:
                        if (mx > 0 && pressed) inv.SendEvent("MOVE PANE R");
                        else if (mx < 0) { spot = Spot.Grid; Show(); }
                        return;
                    case Spot.Colors:
                        if (my > 0) { spot = Spot.Grid; Show(); return; }
                        if (mx != 0) {
                            var ch = grid.Current;
                            int n = owned.Contains(ch) ? ch.Costumes : 1;
                            int k = colorIndex + mx;
                            if (k < 0) { spot = Spot.ArrowL; Show(); return; }
                            if (k >= n) { spot = Spot.ArrowR; Show(); return; }
                            colorIndex = k;
                            if (owned.Contains(ch)) costumePick[ch] = k;
                            Show();
                        }
                        return;
                    default:
                        int edge = grid.Move(mx, my);
                        if (edge == CharacterGrid.Left) spot = Spot.ArrowL;
                        else if (edge == CharacterGrid.Right) spot = Spot.ArrowR;
                        else if (edge == CharacterGrid.Top) { spot = Spot.Colors; colorIndex = owned.Contains(grid.Current) ? costumePick[grid.Current] : 0; }
                        Show();
                        return;
                }
            }
        }

        void TurnColor(Character ch, int by) {
            costumePick[ch] = (costumePick[ch] + by + ch.Costumes) % ch.Costumes;
            if (spot == Spot.Colors) colorIndex = costumePick[ch];
            Show();
        }

        void Refresh(Character playing, Func<Character, int> costumeOf, HashSet<Character> ownedNow) {
            owned = ownedNow;
            grid = new CharacterGrid(playing);
            costumePick.Clear();
            foreach (var ch in Roster.All) costumePick[ch] = costumeOf(ch);
            spot = Spot.Grid;
            repeatDx = repeatDy = 0;
            for (int r = 0; r < tiles.Length; r++)
                for (int c = 0; c < tiles[r].Length; c++) {
                    var ch = CharacterGrid.Rows[r][c];
                    tileArt[r][c].sprite = art.Sprite(art.Tile(ch, !owned.Contains(ch)), tileH);
                }
            Show();
        }

        void Deactivate() {
            foreach (var f in cursorObjFsms) if (f != null) f.SendEvent("DOWN");
            if (confirm != null) confirm.SetActive(false);
        }

        // the cursor on its spot, and what the page shows for the character under it
        void Show() {
            var ch = grid.Current;
            bool has = owned.Contains(ch);
            int costume = has ? costumePick[ch] : 0;
            portrait.sprite = art.Sprite(art.Portrait(ch, costume, !has), portraitH);
            for (int k = 0; k < colors.Length; k++) {
                bool shown = has ? k < ch.Costumes : k == 0;
                colors[k].SetActive(shown);
                if (shown) colorArt[k].sprite = art.Sprite(art.Stock(ch, k, !has || k != costume), colorH);
            }
            GameObject target; string name = "UI_BLANK", desc = "UI_BLANK";
            switch (spot) {
                case Spot.ArrowL: target = inv.FsmVariables.GetFsmGameObject("Arrow L")?.Value; break;
                case Spot.ArrowR: target = inv.FsmVariables.GetFsmGameObject("Arrow R")?.Value; break;
                case Spot.Colors: target = colors[Math.Min(colorIndex, colors.Length - 1)]; name = NameKey; desc = DescKey; break;
                default: target = tiles[grid.Row][grid.Column]; name = NameKey; desc = DescKey; break;
            }
            bool atBench = PlayerData.instance != null && PlayerData.instance.GetBool("atBench");
            bool plays = playing == ch, recolor = costume != costumeOf(ch);
            (NameText, DescText) = CharacterPane.Describe(ch, has, plays && !recolor, atBench);
            if (confirm != null) confirm.SetActive(spot <= Spot.Colors && CharacterPane.CanChoose(has, atBench, plays, recolor));
            if (target != null) {
                cursorFsm.FsmVariables.GetFsmGameObject("Item").Value = target;
                cursorFsm.SendEvent("UPDATE CURSOR");
            }
            textFsm.FsmVariables.GetFsmString("Convo Name").Value = name;
            textFsm.FsmVariables.GetFsmString("Convo Desc").Value = desc;
            textFsm.SendEvent("UPDATE TEXT");
        }

    }
}
