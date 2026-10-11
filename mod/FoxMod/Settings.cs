namespace FoxMelee {
    /// Global settings (Modding API saves folder, FoxMod.GlobalSettings.json). Editable between sessions.
    public class Settings {
        public string IsoPath = "";                 // the player's Melee NTSC 1.02 ISO
        public double WorldScale = 6.0;             // Melee units per Hollow Knight unit (calibrate: Fox's jump ≈ the Knight's)
        public double CameraMargin = 40.0;          // Melee units beyond the room's terrain (Melee's camera box)
        public bool ShowPlaceholder = true;
        public bool ShowModel = true;               // Fox's Melee model (fox.model, extracted at install); placeholder if missing
        public bool MeleeGraphics = true;           // Melee's own models and effects, drawn from its graphics (else fox.model)
        public bool KeyboardFallback = true;
        public bool GameCubeAdapter = true;         // a GameCube controller through a Wii U / Mayflash adapter, read natively
        public int GameCubePort = 0;                // 1-4: that adapter port; 0: the first port with a controller
        public bool FoxHits = true;                 // Fox's Melee hitboxes damage enemies (spec §5.1)
        public bool MeleeSfx = true;                // Melee's sound effects (melee.sfx, extracted at install); silent if missing
        public double SfxVolume = 0.8;              // 0..1
        public double FocusSeconds = 1.141;         // Hollow Knight's Focus time for one mask (D-pad down)
    }
}
