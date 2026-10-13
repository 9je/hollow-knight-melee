using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HkMelee.Installer {
    /// A download pinned by its SHA-256.
    sealed record Pin(string Name, string Url, string Sha256);

    static class Pins {
        public static readonly Pin ApiWindows = new("Modding API (Windows)",
            "https://github.com/hk-modding/api/releases/download/1.5.78.11833-77/moddingapi.v77.windows.zip",
            "bc9f0db3d0916b05cd5a2420bb602fb1b239ce3ff6c289fd84bffb682fb8f1d6");
        public static readonly Pin ApiLinux = new("Modding API (Linux)",
            "https://github.com/hk-modding/api/releases/download/1.5.78.11833-77/moddingapi.v77.linux.zip",
            "5d2327d76b2c48e2694e43e90689d867eaa9ac1d9e6fd2998c93087de7b61ddb");
        public static readonly Pin PythonWindows = new("Python 3.12 (embeddable)",
            "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip",
            "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3");
        public static readonly Pin PythonLinux = new("Python 3.12 (standalone)",
            "https://github.com/astral-sh/python-build-standalone/releases/download/20260901/cpython-3.12.14%2B20260901-x86_64-unknown-linux-gnu-install_only.tar.gz",
            "936c246dfdbbfa7cb22dd01814a21f582a892689fae96b06071a5e433baffa22");
        public static readonly Pin ZigWindows = new("Zig 0.14.1 (C compiler)",
            "https://ziglang.org/download/0.14.1/zig-x86_64-windows-0.14.1.zip",
            "554f5378228923ffd558eac35e21af020c73789d87afeabf4bfd16f2e6feed2c");
        public static readonly Pin ZigLinux = new("Zig 0.14.1 (C compiler)",
            "https://ziglang.org/download/0.14.1/zig-x86_64-linux-0.14.1.tar.xz",
            "24aeeec8af16c381934a6cd7d95c807a8cb2cf7df9fa40d359aa884195c4716c");
        public static IEnumerable<Pin> All => new[] { ApiWindows, ApiLinux, PythonWindows, PythonLinux, ZigWindows, ZigLinux };
    }

    /// Steam's library folders (steamapps/libraryfolders.vdf): each `"path" "..."` entry.
    static class Steam {
        public static IEnumerable<string> LibraryPaths(string vdf) =>
            Regex.Matches(vdf, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m => m.Groups[1].Value.Replace("\\\\", "\\"));

        /// Hollow Knight's folders in every Steam library this machine has (the game's Managed folder must exist).
        public static IEnumerable<string> HollowKnightFolders() {
            var roots = new List<string>();
            if (OperatingSystem.IsWindows()) {
                try {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                    if (key?.GetValue("SteamPath") is string sp) roots.Add(sp.Replace('/', '\\'));
                } catch (Exception) { }
                roots.Add(@"C:\Program Files (x86)\Steam");
            } else {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                roots.Add(Path.Combine(home, ".steam/steam"));
                roots.Add(Path.Combine(home, ".local/share/Steam"));
                roots.Add(Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"));
            }
            var libs = new List<string>(roots);
            foreach (var r in roots) {
                var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
                try { if (File.Exists(vdf)) libs.AddRange(LibraryPaths(File.ReadAllText(vdf))); } catch (Exception) { }
            }
            return libs.Select(l => Path.Combine(l, "steamapps", "common", "Hollow Knight"))
                       .Where(IsHollowKnight).Select(Path.GetFullPath).Distinct();
        }

        public static bool IsHollowKnight(string dir) => File.Exists(Path.Combine(Managed(dir), "Assembly-CSharp.dll"));
        public static string Managed(string dir) => Path.Combine(dir, "hollow_knight_Data", "Managed");
    }

    static class Versions {
        /// The release tag ("v0.2.10") is newer than this installer's version ("0.2.9").
        public static bool IsNewer(string tag, string mine) =>
            Version.TryParse(tag.TrimStart('v'), out var t) && Version.TryParse(mine, out var m) && t > m;
    }

    /// The mod's settings file (FoxMod.GlobalSettings.json): IsoPath set, everything else kept.
    static class SettingsFile {
        public static string WithIso(string json, string iso) {
            JsonObject o = null;
            try { o = json == null ? null : JsonNode.Parse(json) as JsonObject; } catch (JsonException) { }
            o ??= new JsonObject();
            o["IsoPath"] = iso;
            return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// Melee NTSC 1.02 ISOs in the usual places (Redump's size; the SHA-1 is checked by the build).
    static class IsoFinder {
        public const long RedumpSize = 1459978240;
        public const string RedumpSha1 = "d4e70c064cc714ba8400a849cf299dbd1aa326fc";   // NTSC 1.02, unmodified (Redump)

        /// The unmodified NTSC 1.02 Melee: its size and SHA-1 (modded ISOs such as 20XX or TM-CE have the same size).
        public static bool IsMelee102(string path) {
            try {
                if (!File.Exists(path) || new FileInfo(path).Length != RedumpSize) return false;
                using var f = File.OpenRead(path);
                return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(f)).ToLowerInvariant() == RedumpSha1;
            } catch (Exception) { return false; }
        }

        public static IEnumerable<string> Candidates(IEnumerable<string> dirs) {
            foreach (var d in dirs) {
                if (!Directory.Exists(d)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(d, "*.iso", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }); }
                catch (Exception) { continue; }
                foreach (var f in files) {
                    long size;
                    try { size = new FileInfo(f).Length; } catch (Exception) { continue; }
                    if (size == RedumpSize) yield return f;
                }
            }
        }

        public static IEnumerable<string> UsualPlaces() {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[] { "Downloads", "Documents", "Desktop", "Games", "ISOs", "Roms", "roms" }.Select(d => Path.Combine(home, d));
        }
    }

    /// Folder copies that never leave a half-done result behind.
    static class Files {
        public static void CopyDir(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
        }

        /// src copied to dst once: into dst.tmp, then renamed (an interrupted copy is redone, never taken as done). False if
        /// dst already exists (the first backup is kept).
        public static bool BackupOnce(string src, string dst) {
            if (Directory.Exists(dst)) return false;
            var tmp = dst + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            CopyDir(src, tmp);
            Directory.Move(tmp, dst);
            return true;
        }

        /// Earlier versions' mod downloads (payload-<version>) other than keep.
        public static void CleanOldPayloads(string cache, string keep) {
            foreach (var d in Directory.GetDirectories(cache, "payload-*"))
                if (Path.GetFileName(d) != keep) try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// target becomes a copy of source; if anything fails, target is as it was.
        public static void Restore(string source, string target) {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            var fresh = target + ".restoring";
            var aside = target + ".uninstalling";
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            if (Directory.Exists(aside)) Directory.Delete(aside, true);
            CopyDir(source, fresh);
            Directory.Move(target, aside);
            try { Directory.Move(fresh, target); }
            catch { Directory.Move(aside, target); throw; }
            Directory.Delete(aside, true);
        }
    }

    static class Releases {
        /// The newest published release (pre-releases included: the mod's releases are pre-releases for now), or null.
        public static JsonNode Newest(string releasesJson) =>
            (JsonNode.Parse(releasesJson) as JsonArray)?.FirstOrDefault(r => r != null && !((bool?)r["draft"] ?? false));
    }
}
