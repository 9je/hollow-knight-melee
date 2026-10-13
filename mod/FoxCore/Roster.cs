using System;
using System.Collections.Generic;

namespace FoxMelee {
    /// One playable Melee character (spec 2026-10-12-melee-roster-design.md §1).
    public sealed class Character {
        public string Name;
        public int MeleeId;          // Melee's external id (the player block's character byte)
        public int[] Banks;          // its sound banks (melee-<bank>.sfx)
        public bool LockDownB;       // Zelda and Sheik: no transform
        public int Charm;            // the Hollow Knight charm that unlocks it (0: none)
        public bool IselaShop;       // sold in Iselda's map shop (Marth)
    }

    public static class Roster {
        static Character C(string name, int id, int charm, params int[] banks) =>
            new Character { Name = name, MeleeId = id, Charm = charm, Banks = banks };

        public static readonly IReadOnlyList<Character> All = new List<Character> {
            C("Fox", 2, 0, 11),
            new Character { Name = "Marth", MeleeId = 9, Banks = new[] { 19 }, IselaShop = true },
            C("Jigglypuff", 15, 32, 25), C("Falco", 20, 13, 10),
            new Character { Name = "Sheik", MeleeId = 19, Charm = 16, Banks = new[] { 27 }, LockDownB = true },
            C("Captain Falcon", 0, 31, 6), C("Peach", 12, 19, 22), C("Ice Climbers", 14, 39, 13),
            C("Pikachu", 13, 7, 24), C("Yoshi", 17, 33, 28), C("Samus", 16, 35, 26), C("Dr. Mario", 22, 20, 9),
            C("Ganondorf", 25, 15, 30), C("Mario", 8, 21, 18), C("Young Link", 21, 18, 7), C("Link", 6, 26, 16),
            C("Game & Watch", 3, 30, 29), C("Luigi", 7, 11, 17), C("Donkey Kong", 1, 10, 8), C("Mewtwo", 10, 28, 20),
            C("Roy", 23, 6, 31),
            new Character { Name = "Zelda", MeleeId = 18, Charm = 38, Banks = new[] { 27 }, LockDownB = true },
            C("Ness", 11, 17, 21), C("Pichu", 24, 3, 23), C("Bowser", 5, 5, 15), C("Kirby", 4, 22, 14, 33),
        };

        public static readonly Character Fox = All[0];

        /// The character named `name` (case and surrounding spaces ignored); Fox when there is none.
        public static Character Find(string name, out bool known) {
            var n = name?.Trim() ?? "";
            foreach (var c in All)
                if (string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)) { known = true; return c; }
            known = false;
            return Fox;
        }
    }
}
