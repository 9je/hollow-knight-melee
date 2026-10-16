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
        public bool EnemiesHitFox = true;           // enemies' hits go through Melee (shield, dodges, DI); off: the Knight takes them
        public EnemyHitTuning EnemyHits = new EnemyHitTuning();   // how a Hollow Knight hit becomes a Melee hit
        public bool DrawOffThread = true;           // Melee's drawing (3/4 of its frame) runs on the core's thread while Unity goes on
        public bool EdgeAssist = true;              // not Melee: a full-tilt walk teeters at an edge as a slower walk does (dash/run still go off)
        public bool AbilityGates = true;            // doors the Knight couldn't reach without an ability you lack are closed (GateLogic)
        public bool UnlockAllCharacters = true;     // every character at the bench, without their charms (false: each with its charm)
        public System.Collections.Generic.Dictionary<string, int> Costumes = new System.Collections.Generic.Dictionary<string, int>();   // each character's chosen costume (0: its first)
        public KnockbackTuning Knockback = new KnockbackTuning();   // Fox's hits launch small enemies with Melee's knockback and hitstun
        public PogoTuning Pogo = new PogoTuning();   // spec §5.4: down-air bounces off spikes (not Melee: the one exception)
        public GrabTuning Grabs = new GrabTuning();   // spec §5 Grab: Fox's grab and Melee's push against small enemies (plan 7)
    }

    /// Kept in each save file (the Modding API's per-save data).
    public sealed class SaveData {
        public bool MarthBought;                    // Iselda's Marth (IselaShop)
        public string Character;                    // who this save plays (a Melee character's name, Roster); null: Fox, as every save starts
    }
}
