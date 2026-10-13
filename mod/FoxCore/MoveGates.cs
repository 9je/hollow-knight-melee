namespace FoxMelee {
    /// The moves the core locks (its MC_GATE_* bits, mc_set_gates). Spec 2026-10-12-melee-roster-design.md: every
    /// character's moveset is complete, except wall jump until Mantis Claw, and Zelda's and Sheik's transform (down-B).
    public static class MoveGates {
        public const uint DoubleJump = 1, WallJump = 2, SpecialN = 4, SpecialS = 8, SpecialHi = 16, SpecialLw = 32;

        public static uint ForCharacter(Character c, bool mantisClaw) =>
            (mantisClaw ? 0 : WallJump) | (c.LockDownB ? SpecialLw : 0);
    }
}
