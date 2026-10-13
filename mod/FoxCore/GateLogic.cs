using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FoxMelee {
    /// The Knight's abilities (PlayerData), for the progression gates.
    public sealed class KnightAbilities {
        public bool Claw, Wings, Dash, SuperDash, Acid, ShadeCloak, Fireball, Quake, Scream, DreamNail, Lantern;
    }

    /// Hollow Knight's ability gates for Melee characters (spec 2026-10-12-melee-roster-design.md §4), from Hollow Knight
    /// Randomizer 4's logic (Data/, LGPL 2.1): in a room entered by a door, the room's other doors the Knight could not
    /// reach with the abilities the player has are closed (FoxRunner puts an invisible wall across them). Never trapping:
    /// the door you came in by stays open; a room not entered by a door (a bench, a save) closes nothing; a room whose only
    /// way back is a one-way drop keeps everything open if every exit would close. Terms the logic uses that aren't the
    /// Knight's abilities or the room's own doors and regions count as passable, so a doubt leaves a door open. No skips are
    /// assumed (as Hollow Knight intends), except walking through dark rooms, which the Knight can.
    public sealed class GateLogic {
        sealed class Door { public string Scene, Gate, Logic, OneWay; }
        readonly Dictionary<string, List<Door>> doors = new Dictionary<string, List<Door>>();
        readonly Dictionary<string, string> waypoints = new Dictionary<string, string>();
        readonly Dictionary<string, string> macros = new Dictionary<string, string>();
        readonly Dictionary<string, string> locations = new Dictionary<string, string>();

        public static GateLogic Load() {
            string Res(string n) {
                using var s = typeof(GateLogic).Assembly.GetManifestResourceStream(n) ?? throw new FileNotFoundException(n);
                using var r = new StreamReader(s, Encoding.UTF8);
                return r.ReadToEnd();
            }
            return Parse(Res("transitions.json"), Res("waypoints.json"), Res("macros.json"), Res("locations.json"));
        }

        public static GateLogic Parse(string transitions, string waypoints, string macros, string locations = null) {
            var g = new GateLogic();
            if (locations != null)
                foreach (var o in (List<object>)Json.Parse(locations)) {
                    var d = (Dictionary<string, object>)o;
                    g.locations[(string)d["name"]] = (string)d["logic"];
                }
            foreach (var o in (List<object>)Json.Parse(transitions)) {
                var d = (Dictionary<string, object>)o;
                var door = new Door { Scene = (string)d["sceneName"], Gate = (string)d["gateName"], Logic = (string)d["logic"], OneWay = (string)d["oneWayType"] };
                if (!g.doors.TryGetValue(door.Scene, out var list)) g.doors[door.Scene] = list = new List<Door>();
                list.Add(door);
            }
            foreach (var o in (List<object>)Json.Parse(waypoints)) {
                var d = (Dictionary<string, object>)o;
                g.waypoints[(string)d["name"]] = (string)d["logic"];
            }
            foreach (var kv in (Dictionary<string, object>)Json.Parse(macros)) g.macros[kv.Key] = (string)kv.Value;
            return g;
        }

        public IEnumerable<string> Scenes() => doors.Keys;
        public string LogicOf(string scene, string gate) => doors.TryGetValue(scene, out var l) ? l.FirstOrDefault(d => d.Gate == gate)?.Logic : null;
        public IEnumerable<string> Doors(string scene) => doors.TryGetValue(scene, out var l) ? l.Select(d => d.Gate) : Enumerable.Empty<string>();

        static readonly KnightAbilities Everything = new KnightAbilities { Claw = true, Wings = true, Dash = true, SuperDash = true,
            Acid = true, ShadeCloak = true, Fireball = true, Quake = true, Scream = true, DreamNail = true, Lantern = true };

        /// The gates of `scene` to close, for a player who came in by `entryGate` with `a`: only those an ability `a` lacks would
        /// open (a door the logic can't reach even with every ability is left to the game: its state lives in the save, which
        /// the logic doesn't see, as a lever pulled or a floor broken from the other side). If that would leave no exit open,
        /// nothing closes (never trapped).
        public List<string> Blocked(string scene, string entryGate, KnightAbilities a) {
            var none = new List<string>();
            if (!doors.TryGetValue(scene, out var list) || !list.Any(d => d.Gate == entryGate)) return none;
            var withAll = new HashSet<string>(Unreachable(scene, entryGate, Everything, out _, out _));
            var blocked = Unreachable(scene, entryGate, a, out var reached, out var regions).Where(g => !withAll.Contains(g)).ToList();
            // a way out the Knight can actually take from here, with what you have: another exit it reaches, or back through the
            // door it came in by, judged by that door's own requirement from inside (falling in through a top door may need
            // wings to go back up)
            var entry = list.First(d => d.Gate == entryGate);
            var local = LocalRegions(scene);
            var self = $"{scene}[{entryGate}]";
            bool back = entry.OneWay != "OneWayIn"
                        && Evaluate(entry.Logic, t => t == self ? false : Term(t, scene, a, reached, regions, local));
            bool wayOut = back || list.Any(d => d.OneWay != "OneWayIn" && d.Gate != entryGate && reached.Contains($"{scene}[{d.Gate}]"));
            return wayOut ? blocked : none;
        }

        /// Whether the pickup that sets the PlayerData bool `flag` (a charm, an ability) may be taken in `scene`, entered by
        /// `entryGate`, with `a`: closed only when the logic says the Knight needs an ability `a` lacks to reach it (a Melee
        /// character's recovery crosses gaps the Knight dashes or wings over). Open when anything is in doubt: no door entry, an
        /// item the logic doesn't know, or one it can't reach even with every ability (the save holds state it doesn't see).
        public bool ItemOpen(string scene, string entryGate, KnightAbilities a, string flag) {
            var name = LocationOf(flag);
            if (name == null || !locations.TryGetValue(name, out var logic)) return true;
            if (!doors.TryGetValue(scene, out var list) || !list.Any(d => d.Gate == entryGate)) return true;
            var local = LocalRegions(scene);
            if (!logic.Contains(scene + "[") && !local.Any(r => Mentions(logic, r))) return true;   // another room's pickup
            bool Reaches(KnightAbilities k) {
                Unreachable(scene, entryGate, k, out var reached, out var regions);
                return Evaluate(logic, t => Skip(t) ? false : Term(t, scene, k, reached, regions, local));
            }
            return !Reaches(Everything) || !NeedsAbility(logic, local, 0) || Reaches(WithOwn(a, flag));
        }

        // `a` and the pickup's own ability: an item never waits for itself (Isma's Tear from its lower door needs Isma's Tear)
        static KnightAbilities WithOwn(KnightAbilities a, string flag) {
            var k = new KnightAbilities { Claw = a.Claw, Wings = a.Wings, Dash = a.Dash, SuperDash = a.SuperDash, Acid = a.Acid,
                ShadeCloak = a.ShadeCloak, Fireball = a.Fireball, Quake = a.Quake, Scream = a.Scream, DreamNail = a.DreamNail, Lantern = a.Lantern };
            switch (flag) {
            case "hasDash": k.Dash = true; break;
            case "hasWalljump": k.Claw = true; break;
            case "hasDoubleJump": k.Wings = true; break;
            case "hasSuperDash": k.SuperDash = true; break;
            case "hasAcidArmour": k.Acid = true; break;
            case "hasShadowDash": k.ShadeCloak = true; break;
            }
            return k;
        }

        // the logic's skip helpers (pogo off a shriek, a spell's air stall, a shade's hits): never the intended way
        static bool Skip(string t) => t.StartsWith("$SHRIEKPOGO", StringComparison.Ordinal) || t.StartsWith("$CASTSPELL", StringComparison.Ordinal)
                                      || t.StartsWith("$SHADESKIP", StringComparison.Ordinal) || t.StartsWith("$SLOPEBALL", StringComparison.Ordinal);

        static readonly string[] CharmNames = { null, "Gathering_Swarm", "Wayward_Compass", "Grubsong", "Stalwart_Shell", "Baldur_Shell",
            "Fury_of_the_Fallen", "Quick_Focus", "Lifeblood_Heart", "Lifeblood_Core", "Defender's_Crest", "Flukenest", "Thorns_of_Agony",
            "Mark_of_Pride", "Steady_Body", "Heavy_Blow", "Sharp_Shadow", "Spore_Shroom", "Longnail", "Shaman_Stone", "Soul_Catcher",
            "Soul_Eater", "Glowing_Womb", "Fragile_Heart", "Fragile_Greed", "Fragile_Strength", "Nailmaster's_Glory", "Joni's_Blessing",
            "Shape_of_Unn", "Hiveblood", "Dream_Wielder", "Dashmaster", "Quick_Slash", "Spell_Twister", "Deep_Focus", "Grubberfly's_Elegy",
            null, "Sprintmaster", "Dreamshield", "Weaversong" };

        /// The randomizer's name for the pickup that sets PlayerData bool `flag`, or null.
        public static string LocationOf(string flag) {
            switch (flag) {
            case "hasDash": return "Mothwing_Cloak";
            case "hasWalljump": return "Mantis_Claw";
            case "hasDoubleJump": return "Monarch_Wings";
            case "hasSuperDash": return "Crystal_Heart";
            case "hasAcidArmour": return "Isma's_Tear";
            case "hasShadowDash": return "Shade_Cloak";
            }
            if (flag != null && flag.StartsWith("gotCharm_", StringComparison.Ordinal) && int.TryParse(flag.Substring(9), out var n)
                && n > 0 && n < CharmNames.Length) return CharmNames[n];
            return null;
        }

        // the room's exits (not the entrance) the Knight can't reach from it with `a`, through the logic alone
        List<string> Unreachable(string scene, string entryGate, KnightAbilities a, out HashSet<string> reachedOut, out HashSet<string> regionsOut) {
            var list = doors[scene];
            var reached = reachedOut = new HashSet<string> { $"{scene}[{entryGate}]" };
            var regions = regionsOut = new HashSet<string>();
            var local = LocalRegions(scene);
            bool grew = true;
            for (int pass = 0; grew && pass < 64; pass++) {
                grew = false;
                foreach (var w in local)
                    if (!regions.Contains(w) && Evaluate(waypoints[w], t => Term(t, scene, a, reached, regions, local)) && regions.Add(w)) grew = true;
                foreach (var d in list) {
                    var name = $"{scene}[{d.Gate}]";
                    if (d.OneWay == "OneWayIn" || reached.Contains(name)) continue;   // a drop in: never an exit
                    if (Evaluate(d.Logic, t => Term(t, scene, a, reached, regions, local)) && reached.Add(name)) grew = true;
                }
            }
            // only ability gates: a door whose requirement doesn't involve one is the game's own business, never walled
            var r = reached;
            return list.Where(d => d.OneWay != "OneWayIn" && d.Gate != entryGate && !r.Contains($"{scene}[{d.Gate}]")
                                   && NeedsAbility(d.Logic, local, 0)).Select(d => d.Gate).ToList();
        }

        // a term's value in this room: its doors and regions as reached so far, the Knight's abilities, the skip settings;
        // null (passable) for anything else
        bool? Term(string t, string scene, KnightAbilities a, HashSet<string> reached, HashSet<string> regions, HashSet<string> local) {
            int br = t.IndexOf('[');
            if (br > 0 && t.EndsWith("]") && !t.StartsWith("$")) {
                string s = t.Substring(0, br);
                if (doors.ContainsKey(s)) return s == scene && reached.Contains(t);   // another room's door: not here
            }
            if (waypoints.ContainsKey(t)) return local.Contains(t) ? regions.Contains(t) : (bool?)null;   // another room's state: passable
            if (macros.TryGetValue(t, out var m)) return Evaluate(m, x => Term(x, scene, a, reached, regions, local));
            switch (t) {
            case "LEFTCLAW": case "RIGHTCLAW": return a.Claw;
            case "WINGS": return a.Wings;
            case "LEFTDASH": case "RIGHTDASH": return a.Dash;
            case "LEFTSUPERDASH": case "RIGHTSUPERDASH": return a.SuperDash;
            case "ACID": return a.Acid;
            case "FIREBALL": return a.Fireball;
            case "QUAKE": return a.Quake;
            case "SCREAM": return a.Scream;
            case "DREAMNAIL": return a.DreamNail;
            case "LANTERN": return a.Lantern;
            case "SWIM": case "UPSLASH": case "LEFTSLASH": case "RIGHTSLASH": case "FOCUS": case "DARKROOMS": return true;   // the Knight's own
            case "NONE": case "FALSE": return false;
            case "TRUE": case "ANY": return true;
            case "ITEMRANDO": case "MAPAREARANDO": case "FULLAREARANDO": case "ROOMRANDO": case "PRECISEMOVEMENT": case "PROFICIENTCOMBAT":
            case "BACKGROUNDPOGOS": case "ENEMYPOGOS": case "OBSCURESKIPS": case "SHADESKIPS": case "INFECTIONSKIPS": case "FIREBALLSKIPS":
            case "SPIKETUNNELS": case "ACIDSKIPS": case "DAMAGEBOOSTS": case "DANGEROUSSKIPS": case "COMPLEXSKIPS": case "DIFFICULTSKIPS":
            case "SLOPEBALLSKIPS": case "SHRIEKPOGOSKIPS": case "RANDOMELEVATORS": case "CURSED":
                return false;                                                     // randomizer settings and skips: none
            }
            return null;
        }

        static readonly HashSet<string> Abilities = new HashSet<string> {
            "LEFTCLAW", "RIGHTCLAW", "WINGS", "LEFTDASH", "RIGHTDASH", "LEFTSUPERDASH", "RIGHTSUPERDASH", "ACID",
            "FIREBALL", "QUAKE", "SCREAM", "DREAMNAIL", "LANTERN",
        };

        // the expression involves an ability: directly, through a macro, or through one of this room's regions
        bool NeedsAbility(string expr, HashSet<string> local, int depth) {
            if (depth > 16) return false;
            foreach (var raw in expr.Split(new[] { ' ', '(', ')', '|', '+' }, StringSplitOptions.RemoveEmptyEntries)) {
                var t = raw;
                if (Abilities.Contains(t)) return true;
                if (macros.TryGetValue(t, out var m) && NeedsAbility(m, local, depth + 1)) return true;
                if (local.Contains(t) && NeedsAbility(waypoints[t], local, depth + 1)) return true;
            }
            return false;
        }

        // this room's regions: named after it, or reached through its doors or its other regions (others are other rooms')
        readonly Dictionary<string, HashSet<string>> localCache = new Dictionary<string, HashSet<string>>();
        HashSet<string> LocalRegions(string scene) {
            if (localCache.TryGetValue(scene, out var l)) return l;
            l = new HashSet<string>();
            if (waypoints.ContainsKey(scene)) l.Add(scene);
            bool grew = true;
            while (grew) {
                grew = false;
                foreach (var w in waypoints)
                    if (!l.Contains(w.Key) && !WorldState(w.Key) && (w.Value.Contains(scene + "[") || l.Any(x => Mentions(w.Value, x))) && l.Add(w.Key)) grew = true;
            }
            return localCache[scene] = l;
        }

        // a state the save keeps (a floor broken, a lever pulled, a boss beaten, an NPC rescued): the logic only knows how to make
        // it happen during this visit, the save may already have it, so it counts as passable
        static readonly string[] StatePrefixes = { "Broke_", "Opened_", "Lever-", "Defeated_", "Rescued_", "Completed_", "Killed_", "Met_", "Upgraded_", "Hit_", "Pass_", "Activated_", "Lit_", "Unlocked_" };
        static bool WorldState(string name) => StatePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

        static bool Mentions(string expr, string name) {
            for (int i = expr.IndexOf(name, StringComparison.Ordinal); i >= 0; i = expr.IndexOf(name, i + 1, StringComparison.Ordinal)) {
                bool before = i == 0 || " (|+".IndexOf(expr[i - 1]) >= 0, after = i + name.Length == expr.Length || " )|+".IndexOf(expr[i + name.Length]) >= 0;
                if (before && after) return true;
            }
            return false;
        }

        /// `expr` (the logic's: `|` or, `+` and, parentheses; `+` binds tighter), each term's value from `term` (null: passable).
        public static bool Evaluate(string expr, Func<string, bool?> term) {
            var p = new ExprParser(expr, term);
            return p.Or();
        }

        sealed class ExprParser {
            readonly string s; readonly Func<string, bool?> term; int i;
            public ExprParser(string s, Func<string, bool?> term) { this.s = s; this.term = term; }
            void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
            public bool Or() {
                bool v = And();
                for (;;) { Ws(); if (i < s.Length && s[i] == '|') { i++; v |= And(); } else return v; }
            }
            bool And() {
                bool v = Atom();
                for (;;) { Ws(); if (i < s.Length && s[i] == '+') { i++; v &= Atom(); } else return v; }
            }
            bool Atom() {
                Ws();
                if (i >= s.Length) return true;
                if (s[i] == '(') { i++; bool v = Or(); Ws(); if (i < s.Length && s[i] == ')') i++; return v; }
                int start = i, depth = 0;
                while (i < s.Length) {
                    char c = s[i];
                    if (c == '[') depth++;
                    else if (c == ']') depth--;
                    else if (depth == 0 && (c == '|' || c == '+' || c == ')' || char.IsWhiteSpace(c))) break;
                    i++;
                }
                string t = s.Substring(start, i - start);
                if (t.Length == 0) { i++; return true; }
                if (t == "NONE" || t == "FALSE") return false;
                if (t == "TRUE" || t == "ANY") return true;
                int cmp = t.IndexOfAny(new[] { '<', '>', '=' });
                if (cmp > 0) return term(t) ?? true;              // a count (SIMPLE>1, NOFLOWER=FALSE): passable unless known
                return term(t) ?? true;
            }
        }
    }

    /// A small JSON reader for the logic files (comments and trailing commas allowed, as theirs have).
    static class Json {
        public static object Parse(string text) { int i = 0; var v = Value(text, ref i); return v; }

        static void Ws(string s, ref int i) {
            for (;;) {
                while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '﻿')) i++;
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') { i = s.IndexOf("*/", i + 2, StringComparison.Ordinal) + 2; continue; }
                return;
            }
        }

        static object Value(string s, ref int i) {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{') {
                var d = new Dictionary<string, object>(); i++;
                for (;;) {
                    Ws(s, ref i);
                    if (s[i] == '}') { i++; return d; }
                    var k = Str(s, ref i); Ws(s, ref i); i++;   // ':'
                    d[k] = Value(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') i++;
                }
            }
            if (c == '[') {
                var l = new List<object>(); i++;
                for (;;) {
                    Ws(s, ref i);
                    if (s[i] == ']') { i++; return l; }
                    l.Add(Value(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') i++;
                }
            }
            if (c == '"') return Str(s, ref i);
            int st = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            var w = s.Substring(st, i - st);
            return w == "true" ? true : w == "false" ? false : w == "null" ? null : (object)double.Parse(w, System.Globalization.CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i) {
            var b = new StringBuilder(); i++;
            while (s[i] != '"') {
                if (s[i] == '\\') { i++; char e = s[i]; b.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'u' ? (char)Convert.ToInt32(s.Substring(i + 1, 4), 16) : e); if (e == 'u') i += 4; }
                else b.Append(s[i]);
                i++;
            }
            i++;
            return b.ToString();
        }
    }
}
