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

        public void Move(int dx, int dy) {
            Row = Math.Max(0, Math.Min(Rows.Count - 1, Row + dy));
            Column = Math.Max(0, Math.Min(Rows[Row].Count - 1, Column + dx));
        }
    }
}
