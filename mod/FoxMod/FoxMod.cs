using System.IO;
using System.Reflection;
using Modding;
using UnityEngine;

namespace FoxMelee {
    /// Hollow Knight Modding API entry point. All work happens in FoxRunner, a MonoBehaviour that lives
    /// for the whole session.
    public class FoxMod : Mod, IGlobalSettings<Settings>, ILocalSettings<SaveData> {
        public static FoxMod Instance;
        public Settings Settings = new Settings();
        internal GcInput GameCube;                  // null when the GameCube adapter is off in settings
        public FoxMod() : base("Hollow Knight: Melee") { Instance = this; }
        public void SaveSettings() => SaveGlobalSettings();
        public override string GetVersion() => "0.2.0";
        public void OnLoadGlobal(Settings s) => Settings = s;
        public Settings OnSaveGlobal() => Settings;
        public SaveData Save = new SaveData();      // this save file's (Marth bought from Iselda)
        public void OnLoadLocal(SaveData s) => Save = s ?? new SaveData();
        public SaveData OnSaveLocal() => Save;

        public static string ModDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public override void Initialize() {
            var go = new GameObject("FoxMelee");
            Object.DontDestroyOnLoad(go);
            if (Settings.GameCubeAdapter) GameCube = new GcInput(Settings.GameCubePort);
            go.AddComponent<FoxRunner>();
            IselaShop.Hook();
        }
    }
}
