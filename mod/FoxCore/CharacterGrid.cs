using System;
using System.Collections.Generic;
using System.Linq;

namespace FoxMelee {
    /// The bench's character select, laid out as Melee's (Dr. Mario ... Ganondorf; Falco ... Young Link; Pichu ... Roy),
    /// with Sheik in her own slot beside Zelda. The cursor moves a slot at a time and stops at the edges; moving to a
    /// shorter row lands on its last slot.
    public sealed class CharacterGrid {
        public static readonly IReadOnlyList<IReadOnlyList<Character>> Rows = new[] {
            new[] { "Dr. Mario", "Mario", "Luigi", "Bowser", "Peach", "Yoshi", "Donkey Kong", "Captain Falcon", "Ganondorf" },
            new[] { "Falco", "Fox", "Ness", "Ice Climbers", "Kirby", "Samus", "Zelda", "Sheik", "Link", "Young Link" },
            new[] { "Pichu", "Pikachu", "Jigglypuff", "Mewtwo", "Game & Watch", "Marth", "Roy" },
        }.Select(r => (IReadOnlyList<Character>)r.Select(n => Roster.Find(n, out _)).ToArray()).ToArray();

        public int Row { get; private set; }
        public int Column { get; private set; }

        public CharacterGrid(Character playing) {
            for (int r = 0; r < Rows.Count; r++)
                for (int c = 0; c < Rows[r].Count; c++)
                    if (Rows[r][c] == playing) { Row = r; Column = c; }
        }

        public Character Current => Rows[Row][Column];

        /// Moving past the grid's sides or top: Move stays and says which (the page's arrows, the colors above).
        public const int Left = -1, Right = 1, Top = 2;

        public int Move(int dx, int dy) {
            if (dy < 0 && Row == 0) return Top;
            if (dx < 0 && Column == 0 && dy == 0) return Left;
            if (dx > 0 && Column == Rows[Row].Count - 1 && dy == 0) return Right;
            Row = Math.Max(0, Math.Min(Rows.Count - 1, Row + dy));
            Column = Math.Max(0, Math.Min(Rows[Row].Count - 1, Column + dx));
            return 0;
        }
    }
}
