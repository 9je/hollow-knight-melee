namespace FoxMelee {
    /// Global settings (Modding API saves folder, FoxMelee.GlobalSettings.json). Editable between sessions.
    public class Settings {
        public string IsoPath = "";                 // the player's Melee NTSC 1.02 ISO
        public double WorldScale = 6.0;             // Melee units per Hollow Knight unit (calibrate: Fox's jump ≈ the Knight's)
        public double CameraMargin = 40.0;          // Melee units beyond the room's terrain (Melee's camera box)
        public bool ShowPlaceholder = true;
        public bool ShowModel = true;               // Fox's Melee model (fox.model, extracted at install); placeholder if missing
        public bool KeyboardFallback = true;
    }
}
