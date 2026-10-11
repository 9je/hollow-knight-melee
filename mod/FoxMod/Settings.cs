namespace FoxMelee {
    /// Global settings (Modding API saves folder, FoxMelee.GlobalSettings.json). Editable between sessions.
    public class Settings {
        public string IsoPath = "";                 // the player's Melee NTSC 1.02 ISO
        public double WorldScale = 6.0;             // Melee units per Hollow Knight unit (calibrate: Fox's jump ≈ the Knight's)
        public double CameraMargin = 40.0;          // Melee units beyond the room's terrain (Melee's camera box)
        public bool ShowPlaceholder = true;
        public bool ShowModel = true;               // Fox's Melee model (fox.model, extracted at install); placeholder if missing
        public bool KeyboardFallback = true;
        public bool GameCubeAdapter = true;         // a GameCube controller through a Wii U / Mayflash adapter, read natively
        public int GameCubePort = 0;                // 1-4: that adapter port; 0: the first port with a controller
        public bool FoxHits = true;
        public double FocusSeconds = 1.141;         // Hollow Knight's Focus time for one mask (D-pad down)                 // Fox's Melee hitboxes damage enemies (spec §5.1)
    }
}
