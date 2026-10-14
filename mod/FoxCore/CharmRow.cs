using System;
using System.Collections.Generic;

namespace FoxMelee {
    /// The row of Hollow Knight's own charms (the ones that aren't a character) under the character select in the bench's
    /// charm screen: entered from the grid's bottom row, left under the column the cursor was in, and left again upwards.
    public sealed class CharmRow {
        public readonly IReadOnlyList<int> Charms;
        /// The charm the cursor is on, or -1 while the cursor is on the character grid.
        public int Index { get; private set; } = -1;
        public bool Active => Index >= 0;
        /// The charm id under the cursor; 0 off the row.
        public int Current => Active ? Charms[Index] : 0;

        public CharmRow(IReadOnlyList<int> charms) { Charms = charms; }

        /// Down from the grid's `column` of `columns`: the charm under it. False with no charms to go to.
        public bool Enter(int column, int columns) {
            if (Charms.Count == 0) return false;
            int at = columns <= 1 ? 0 : (int)Math.Round((double)column * (Charms.Count - 1) / (columns - 1));
            Index = Math.Max(0, Math.Min(Charms.Count - 1, at));
            return true;
        }

        public void Leave() { Index = -1; }

        public void Move(int dx) {
            if (!Active) return;
            Index = Math.Max(0, Math.Min(Charms.Count - 1, Index + dx));
        }
    }

    public static class CharmRules {
        public const int Count = 40;   // Hollow Knight's charms are 1..40

        /// Equipping a charm of `cost` notches with `filled` of `slots` in use: while it fits (overcharming isn't offered).
        public static bool CanEquip(int cost, int slots, int filled) => filled + cost <= slots;

        /// The save's charms that stay charms (not a character's), in Hollow Knight's order.
        public static List<int> Normal(Func<int, bool> has) {
            var l = new List<int>();
            for (int id = 1; id <= Count; id++) if (Roster.ForCharm(id) == null && has(id)) l.Add(id);
            return l;
        }
    }
}
