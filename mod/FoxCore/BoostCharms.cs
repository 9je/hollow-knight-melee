namespace FoxMelee {
    /// A fighter's attributes before any charm (read when its match starts).
    public readonly struct FighterBase {
        public readonly float Dash, Run, Weight;
        public FighterBase(float dash, float run, float weight) { Dash = dash; Run = run; Weight = weight; }
    }

    /// Spec 2026-10-12-melee-roster-design.md §1, the charms that stay charms with an effect changed for Melee characters.
    /// Sprintmaster (37) and Steady Body (14) are deliberate deviations from Melee, flagged in the spec.
    public static class BoostCharms {
        public const int Sprintmaster = 37, SteadyBody = 14, Strength = 25, DeepFocus = 34;
        public const float SprintScale = 1.1f, SteadyWeight = 20f;

        /// Melee's dash_initial_velocity and dash_max_velocity (the run) x1.1 with Sprintmaster; weight +20 with Steady Body.
        public static (float dash, float run, float weight) Attributes(FighterBase b, bool sprint, bool steady) =>
            (sprint ? b.Dash * SprintScale : b.Dash, sprint ? b.Run * SprintScale : b.Run, steady ? b.Weight + SteadyWeight : b.Weight);

        /// Fragile/Unbreakable Strength: hits deal 1.5x Hollow Knight damage to enemies.
        public static double DamageScale(bool strength) => strength ? 1.5 : 1.0;

        /// Deep Focus, as in Hollow Knight: Focus takes 1.65 times as long and heals two masks.
        public static (int frames, int masks) Focus(int frames, bool deep) => deep ? ((int)System.Math.Round(frames * 1.65), 2) : (frames, 1);
    }
}
