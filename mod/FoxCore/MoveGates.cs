namespace FoxMelee {
    /// What the Knight has unlocked (PlayerData), for move gating (spec §6).
    public struct HkUnlocks { public bool VengefulSpirit, MothwingCloak, MantisClaw, CrystalHeart, DesolateDive, MonarchWings; }

    /// Spec §6: Fox's moves that wait for a Hollow Knight unlock. The bits are the core's MC_GATE_* (mc_set_gates).
    public static class MoveGates {
        public const uint DoubleJump = 1, WallJump = 2, Blaster = 4, Illusion = 8, FireFox = 16, Shine = 32;

        public static uint Locked(HkUnlocks u) =>
            (u.MonarchWings ? 0 : DoubleJump) | (u.MantisClaw ? 0 : WallJump) | (u.DesolateDive ? 0 : Blaster)
            | (u.MothwingCloak ? 0 : Illusion) | (u.CrystalHeart ? 0 : FireFox) | (u.VengefulSpirit ? 0 : Shine);
    }
}
