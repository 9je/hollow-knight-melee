using System.Linq;

namespace FoxMelee {
    /// The inventory's Characters page (CharacterPaneUi): what its text says and where its tiles go.
    public static class CharacterPane {
        public struct Point { public double X, Y; }

        /// The page's name and description for the character under the cursor (Hollow Knight's Text Name / Text Desc).
        public static (string name, string desc) Describe(Character c, bool owned, bool playing, bool atBench) {
            if (!owned) return ("???", c.IselaShop ? "Sold by Iselda in Dirtmouth." : "Found with one of Hallownest's charms.");
            if (playing) return (c.Name, $"Playing as {c.Name}.");
            return (c.Name, atBench ? $"Play as {c.Name}." : $"Rest at a bench to play as {c.Name}.");
        }

        /// Choosing changes something only at a bench (as charms), for a character the save has, and when it is someone
        /// else or the same one in another color.
        public static bool CanChoose(bool owned, bool atBench, bool playing, bool costumeChanged) =>
            owned && atBench && (!playing || costumeChanged);

        /// The tiles' centres, row by row: the widest row spans left..right, shorter rows are centred under it (Melee's
        /// layout), rows rowStep apart from top down.
        public static Point[][] Layout(int[] rows, double left, double right, double top, double rowStep) {
            int widest = rows.Max();
            double step = Step(rows, left, right);
            return rows.Select((n, r) => Enumerable.Range(0, n)
                .Select(i => new Point { X = left + (widest - n) / 2.0 * step + i * step, Y = top - r * rowStep }).ToArray()).ToArray();
        }

        public static double Step(int[] rows, double left, double right) {
            int widest = rows.Max();
            return widest > 1 ? (right - left) / (widest - 1) : 0;
        }
    }
}
