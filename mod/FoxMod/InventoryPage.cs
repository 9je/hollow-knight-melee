using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace FoxMelee {
    /// Adds a page to Hollow Knight's inventory, after the Map: its Inventory Control state machine numbers the pages and
    /// switches on the number to open one and to turn left or right (InventoryPanes), so the page gets the next number,
    /// a copy of the Journal's states that open and name it, and the turning switches wrap round past it. Everything is
    /// checked before anything is changed: if the state machine isn't what this expects (another mod's pages, a game
    /// update), the inventory is left exactly as it was and the page isn't added.
    static class InventoryPage {
        public const string Event = "FOXMELEE CHARACTERS";

        /// The page's number, or -1 (and why, logged) when it couldn't be added.
        public static int Install(PlayMakerFSM inv, GameObject page, string nameKey, System.Action<string> log) {
            string why = null;
            FsmState S(string name) { var s = FsmEdit.State(inv, name); if (s == null) why ??= $"no state '{name}'"; return s; }
            var open = S("Check Current Pane"); var right = S("Check R Pane"); var left = S("Check L Pane"); var loop = S("Loop Through");
            var openJ = S("Open Journal"); var nextJ = S("Next Journal"); var nextJ2 = S("Next Journal 2"); var nextJ3 = S("Next Journal 3");
            var under = new[] { S("Under"), S("Under 2"), S("Under 3") };
            var single = S("Single Pane?"); var regain = new[] { S("Regain Control"), S("Regain Control 2") };
            if (why != null) { log("inventory: Characters page not added: " + why); return -1; }

            // the four switches, each extended (checked first, applied after)
            var switches = new List<(IntSwitch sw, int[] compare, FsmEvent[] events)>();
            int index = -1;
            foreach (var s in new[] { open, right, left, loop }) {
                var sw = FsmEdit.Action<IntSwitch>(s);
                if (sw == null) { log($"inventory: Characters page not added: no page switch in '{s.Name}'"); return -1; }
                var names = sw.sendEvent.Select(e => e?.Name).ToArray();
                if (!InventoryPanes.Extend(sw.compareTo.Select(c => c.Value).ToArray(), names, Event, out var compare, out _, out int at, out why)) {
                    log($"inventory: Characters page not added: '{s.Name}': {why}"); return -1;
                }
                if (index >= 0 && at != index) { log($"inventory: Characters page not added: '{s.Name}' numbers it {at}, not {index}"); return -1; }
                index = at;
                switches.Add((sw, compare, sw.sendEvent.Concat(new[] { FsmEvent.GetFsmEvent(Event) }).ToArray()));
            }
            var unders = under.Select(u => FsmEdit.Actions<SetIntValue>(u).FirstOrDefault(a => a.intValue.Value == index)).ToArray();
            if (unders.Any(u => u == null)) { log($"inventory: Characters page not added: an Under state doesn't wrap to {index}"); return -1; }
            var singleTest = FsmEdit.Action<PlayerDataBoolTest>(single);
            if (singleTest == null || singleTest.isTrue == null) { log("inventory: Characters page not added: 'Single Pane?' has no page test"); return -1; }
            if (FsmEdit.Actions<SetGameObject>(openJ).Length != 1 || FsmEdit.Actions<SetGameObject>(nextJ).Count(a => a.gameObject?.Name == "Journal Pane") != 1
                || FsmEdit.Action<GetLanguageString>(nextJ2) == null || FsmEdit.Action<GetLanguageString>(nextJ3) == null) {
                log("inventory: Characters page not added: the Journal's states aren't as expected"); return -1;
            }

            // apply
            foreach (var (sw, compare, events) in switches) {
                sw.compareTo = compare.Select(v => new FsmInt { Value = v }).ToArray();
                sw.sendEvent = events;
            }
            foreach (var u in unders) u.intValue = new FsmInt { Value = index + 1 };
            var pageVar = new FsmGameObject { Value = page };

            // opening on the page (it was the last one open): the Journal's, naming and setting this one
            var openC = FsmEdit.Copy(inv, openJ, "Open Characters");
            Name(openC, nameKey);
            foreach (var a in FsmEdit.Actions<SetIntValue>(openC)) a.intValue = new FsmInt { Value = index };
            foreach (var a in FsmEdit.Actions<SetGameObject>(openC)) a.gameObject = pageVar;
            FsmEdit.AddTransition(open, Event, openC);
            // turning onto it: Loop Through's copy sets it as the current page (its other SetGameObject keeps the previous)
            var nextC = FsmEdit.Copy(inv, nextJ, "Next Characters");
            FsmEdit.Remove<PlayerDataBoolTest>(nextC);           // always there
            Name(nextC, nameKey);
            foreach (var a in FsmEdit.Actions<SetGameObject>(nextC)) if (a.gameObject != null && a.gameObject.Name == "Journal Pane") a.gameObject = pageVar;
            FsmEdit.AddTransition(loop, Event, nextC);
            // the names shown beside the arrows (Check R Pane, Check L Pane)
            foreach (var (from, sw, name) in new[] { (nextJ2, right, "Next Characters 2"), (nextJ3, left, "Next Characters 3") }) {
                var c = FsmEdit.Copy(inv, from, name);
                FsmEdit.Remove<PlayerDataBoolTest>(c);
                Name(c, nameKey);
                FsmEdit.AddTransition(sw, Event, c);
            }
            // never a single page (the arrows show), and the page goes away with the inventory
            FsmEdit.Insert(single, System.Array.IndexOf(single.Actions, singleTest), new FsmEdit.SendNow { Send = singleTest.isTrue });
            foreach (var r in regain) FsmEdit.Insert(r, r.Actions.Length, new FsmEdit.Deactivate { Target = page });
            log($"inventory: Characters page added (page {index})");
            return index;
        }

        /// The Inventory page asks whether there are other pages before it shows its arrows: yes.
        public static void OtherPagesOnInventory(GameObject invPage, System.Action<string> log) {
            var ui = invPage?.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "UI Inventory");
            var s = ui != null ? FsmEdit.State(ui, "Any Other Panes?") : null;
            var test = FsmEdit.Action<PlayerDataBoolTest>(s);
            if (test == null || test.isTrue == null) { log("inventory: the Inventory page's arrows are left to Hollow Knight (no 'Any Other Panes?' test)"); return; }
            FsmEdit.Insert(s, System.Array.IndexOf(s.Actions, test), new FsmEdit.SendNow { Send = test.isTrue });
        }

        static void Name(FsmState s, string key) {
            foreach (var a in FsmEdit.Actions<GetLanguageString>(s)) { a.sheetName = new FsmString { Value = "UI" }; a.convName = new FsmString { Value = key }; }
        }
    }
}
