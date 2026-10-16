using System.Linq;

namespace FoxMelee {
    /// Hollow Knight's inventory pages are numbered (PlayerData currentInvPane: Inventory 0, Charms 1, Journal 2, Map 3) and
    /// its Inventory Control state machine finds a page by switching on that number: on opening, and turning left or right
    /// (those switches also catch -1, UNDER, and the page count, OVER, to wrap round). A page added after the last one
    /// takes the next number, and OVER moves past it.
    public static class InventoryPanes {
        public static bool Extend(int[] compare, string[] events, string newEvent,
                                  out int[] newCompare, out string[] newEvents, out int index, out string why) {
            newCompare = null; newEvents = null; index = -1;
            if (compare == null || events == null || compare.Length != events.Length) { why = "the switch's values and events don't pair up"; return false; }
            if (events.Contains(newEvent)) { why = newEvent + " is there already"; return false; }
            var pages = compare.Where((v, i) => events[i] != "UNDER" && events[i] != "OVER").OrderBy(v => v).ToArray();
            for (int i = 0; i < pages.Length; i++)
                if (pages[i] != i) { why = $"the pages are numbered {string.Join(",", pages)}, not 0..{pages.Length - 1}"; return false; }
            index = pages.Length;
            int over = System.Array.IndexOf(events, "OVER");
            if (over >= 0 && compare[over] != index) { why = $"OVER is {compare[over]}, not the page count {index}"; index = -1; return false; }
            newCompare = compare.Concat(new[] { index }).ToArray();
            if (over >= 0) newCompare[over] = index + 1;
            newEvents = events.Concat(new[] { newEvent }).ToArray();
            why = null;
            return true;
        }
    }
}
