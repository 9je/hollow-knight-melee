using System.Collections.Generic;

namespace FoxMelee {
    /// The bench's character choice: the owned characters in a row, the cursor on whoever plays (Fox if they're no longer
    /// owned), moving with wrap-around.
    public sealed class CharacterMenu {
        public readonly IReadOnlyList<Character> Owned;
        int at;

        public CharacterMenu(IReadOnlyList<Character> owned, Character playing) {
            Owned = owned;
            at = 0;
            for (int i = 0; i < owned.Count; i++) if (owned[i] == playing) at = i;
        }

        public Character Current => Owned[at];
        public int Index => at;

        public void Move(int by) {
            int n = Owned.Count;
            at = ((at + by) % n + n) % n;
        }
    }
}
