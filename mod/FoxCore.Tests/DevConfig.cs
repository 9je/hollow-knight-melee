using System;
using System.IO;
using System.Text.RegularExpressions;

namespace FoxMelee.Tests {
    /// Paths to the native core and the game files, from the repo's dev.toml. Null when unavailable.
    static class DevConfig {
        public static readonly string Repo = FindRepo();
        public static string CoreSo => Existing(Path.Combine(Repo, "core/build/libmelee_core.so"));
        public static string Mem1 => Existing(Path.Combine(Repo, "core/build/gen-core/mem1.bin"));
        public static string Iso {
            get {
                var toml = Path.Combine(Repo, "dev.toml");
                if (!File.Exists(toml)) return null;
                var m = Regex.Match(File.ReadAllText(toml), "^iso\\s*=\\s*\"([^\"]+)\"", RegexOptions.Multiline);
                return m.Success ? Existing(m.Groups[1].Value) : null;
            }
        }
        public static bool Available => CoreSo != null && Mem1 != null && Iso != null;
        static string Existing(string p) => File.Exists(p) ? p : null;
        static string FindRepo() {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "dev.toml")) && !Directory.Exists(Path.Combine(d.FullName, "core"))) d = d.Parent;
            return d?.FullName ?? "";
        }
    }
}
