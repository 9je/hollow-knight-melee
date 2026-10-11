using System.IO;
using System.Reflection;
using Modding;
using UnityEngine;

namespace FoxMelee {
    /// Hollow Knight Modding API entry point. All work happens in FoxRunner, a MonoBehaviour that lives
    /// for the whole session.
    public class FoxMod : Mod, IGlobalSettings<Settings> {
        public static FoxMod Instance;
        public Settings Settings = new Settings();
        public FoxMod() : base("FoxMelee") { Instance = this; }
        public override string GetVersion() => "0.1.0-checkpoint-a";
        public void OnLoadGlobal(Settings s) => Settings = s;
        public Settings OnSaveGlobal() => Settings;

        public static string ModDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public override void Initialize() {
            var go = new GameObject("FoxMelee");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<FoxRunner>();
        }
    }
}
