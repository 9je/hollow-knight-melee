using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace HkMelee.Installer {
    /// Hollow Knight: Melee's installer: one file that installs, updates and uninstalls the mod. Everything Melee-derived is
    /// built here from the player's own ISO (spec 2026-10-12-public-release-design.md); the game is never started.
    static class Program {
        const string Repo = "9je/hollow-knight-melee";
        const string ModFolder = "FoxMelee";                       // Managed/Mods/<this>
        static readonly bool Win = OperatingSystem.IsWindows();
        static readonly HttpClient Http = MakeHttp();
        static string version;
        static string cache;

        static HttpClient MakeHttp() {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("HollowKnightMeleeInstaller");
            return h;
        }

        static int Main(string[] args) {
            version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
            cache = Environment.GetEnvironmentVariable("HKM_CACHE")   // test overrides: HKM_CACHE, HKM_SAVES, HKM_PAYLOAD
                ?? (Win ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowKnightMelee")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "hollow-knight-melee"));
            Directory.CreateDirectory(cache);
            Console.WriteLine($"Hollow Knight: Melee installer {version}");
            Console.WriteLine();
            try {
                CleanOldSelf();
                if (!args.Contains("--no-update") && SelfUpdate(args)) return 0;
                var hk = FindHollowKnight();
                if (hk == null) return Done(1);
                string choice = args.Contains("--install") ? "i" : args.Contains("--uninstall") ? "u"
                    : Ask("[I]nstall or update, [U]ninstall, or [Q]uit? (Enter = install) ").Trim().ToLowerInvariant();
                if (choice == "q") return 0;
                if (choice == "u") { Uninstall(hk); return Done(0); }
                Install(hk, args.Contains("--install"));
                return Done(0);
            } catch (Exception e) {
                Console.WriteLine();
                Console.WriteLine("Something went wrong: " + e.Message);
                Console.WriteLine($"(details in {Path.Combine(cache, "installer.log")})");
                File.AppendAllText(Path.Combine(cache, "installer.log"), $"{DateTime.Now:s} {version}\n{e}\n\n");
                return Done(1);
            }
        }

        static int Done(int rc) {
            Console.WriteLine();
            if (!Console.IsInputRedirected) Ask("Press Enter to close.");
            return rc;
        }

        static string Ask(string prompt) {
            Console.Write(prompt);
            return Console.ReadLine() ?? "";
        }

        static void Step(string s) => Console.WriteLine("* " + s);

        // ---- self-update: the newest release's installer replaces this one (a running exe can be renamed) ----
        static void CleanOldSelf() {
            var me = Environment.ProcessPath;
            if (me != null && File.Exists(me + ".old")) try { File.Delete(me + ".old"); } catch (Exception) { }
        }

        static bool SelfUpdate(string[] args) {
            JsonNode rel;
            try { rel = JsonNode.Parse(Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").Result); }
            catch (Exception) { Console.WriteLine("(could not check for updates; going on)"); return false; }
            var tag = (string)rel?["tag_name"];
            if (tag == null || !Versions.IsNewer(tag, version)) return false;
            var name = Win ? "HollowKnightMeleeInstaller.exe" : "HollowKnightMeleeInstaller-linux";
            var asset = rel["assets"]?.AsArray().FirstOrDefault(a => (string)a?["name"] == name);
            var me = Environment.ProcessPath;
            if (asset == null || me == null) return false;
            Step($"updating the installer to {tag}...");
            var bytes = Http.GetByteArrayAsync((string)asset["browser_download_url"]).Result;
            var digest = (string)asset["digest"];
            if (digest != null && digest.StartsWith("sha256:") && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != digest[7..])
                throw new Exception("the downloaded installer's checksum is wrong; try again later");
            File.Move(me, me + ".old", true);
            File.WriteAllBytes(me, bytes);
            if (!Win) File.SetUnixFileMode(me, (UnixFileMode)0b111_101_101);
            Process.Start(new ProcessStartInfo(me) { UseShellExecute = false, ArgumentList = { "--no-update" } }.WithArgs(args))?.WaitForExit();
            return true;
        }

        static ProcessStartInfo WithArgs(this ProcessStartInfo p, string[] args) {
            foreach (var a in args.Where(a => a != "--no-update")) p.ArgumentList.Add(a);
            return p;
        }

        // ---- finding things ----
        static string FindHollowKnight() {
            var saved = State("hk");
            if (saved != null && Steam.IsHollowKnight(saved)) { Step("Hollow Knight: " + saved); return saved; }
            var found = Steam.HollowKnightFolders().ToList();
            if (found.Count > 0) { Step("Hollow Knight: " + found[0]); SetState("hk", found[0]); return found[0]; }
            Console.WriteLine("Hollow Knight wasn't found in your Steam libraries.");
            for (;;) {
                var p = Unquote(Ask("Drag your Hollow Knight folder (the one with hollow_knight_Data in it) here and press Enter (or Enter to quit): "));
                if (p == "") return null;
                if (Steam.IsHollowKnight(p)) { SetState("hk", p); return p; }
                Console.WriteLine("That isn't a Hollow Knight folder.");
            }
        }

        static string FindIso() {
            var saved = State("iso");
            if (saved != null && File.Exists(saved)) { Step("Melee ISO: " + saved); return saved; }
            Step("looking for your Melee ISO...");
            var c = IsoFinder.Candidates(IsoFinder.UsualPlaces()).FirstOrDefault();
            if (c != null) { Step("Melee ISO: " + c); SetState("iso", c); return c; }
            for (;;) {
                var p = Unquote(Ask("Drag your Super Smash Bros. Melee NTSC 1.02 ISO into this window and press Enter: "));
                if (File.Exists(p)) { SetState("iso", p); return p; }
                Console.WriteLine("No file there.");
            }
        }

        static string Unquote(string s) => s.Trim().Trim('"', '\'').Trim();

        static string StateFile => Path.Combine(cache, "installer.json");
        static string State(string key) {
            try { return (string)JsonNode.Parse(File.ReadAllText(StateFile))?[key]; } catch (Exception) { return null; }
        }
        static void SetState(string key, string value) {
            JsonObject o;
            try { o = JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? new JsonObject(); } catch (Exception) { o = new JsonObject(); }
            o[key] = value;
            File.WriteAllText(StateFile, o.ToJsonString());
        }

        static string SavesDir => Environment.GetEnvironmentVariable("HKM_SAVES") ?? (Win
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Team Cherry", "Hollow Knight")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "unity3d", "Team Cherry", "Hollow Knight"));

        // ---- install ----
        static void Install(string hk, bool unattended) {
            if (Process.GetProcessesByName("hollow_knight").Length > 0) throw new Exception("Hollow Knight is running: close it first");
            var iso = FindIso();
            var managed = Steam.Managed(hk);
            // backups, each made once and kept
            if (!Directory.Exists(managed + ".vanilla")) { Step("backing up the game's Managed folder (once)"); CopyDir(managed, managed + ".vanilla"); }
            var backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "hollow-knight-save-backup");
            if (Directory.Exists(SavesDir) && !Directory.Exists(backup)) { Step("backing up your saves to " + backup + " (once)"); CopyDir(SavesDir, backup); }
            // the mod (its code, the core's sources and the build tools) from this installer's release
            var payload = Payload();
            // tools: Python and a C compiler, downloaded once
            var (python, zig) = Toolchain();
            Step("building the Melee core from your ISO (5-10 minutes the first time; quick after)");
            var outDir = Path.Combine(cache, "build-out");
            var cc = $"\"{zig.Replace('\\', '/')}\" cc -target " + (Win ? "x86_64-windows-gnu" : "x86_64-linux-gnu");
            Run(python, new[] { "-m", "ssbmhk_tools.buildcore", iso, "--core", Path.Combine(payload, "core"), "--out", outDir,
                                "--cache", Path.Combine(cache, "build"), "--target", Win ? "windows" : "linux", "--cc", cc },
                Win ? null : Path.Combine(payload, "tools", "src"));
            Step("installing the Modding API");
            Unzip(Download(Win ? Pins.ApiWindows : Pins.ApiLinux), managed);
            Step("installing Hollow Knight: Melee");
            var dest = Path.Combine(managed, "Mods", ModFolder);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(Path.Combine(payload, "mod"))) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            foreach (var f in Directory.GetFiles(outDir)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            Directory.CreateDirectory(SavesDir);
            var settings = Path.Combine(SavesDir, "FoxMod.GlobalSettings.json");
            File.WriteAllText(settings, SettingsFile.WithIso(File.Exists(settings) ? File.ReadAllText(settings) : null, iso));
            Console.WriteLine();
            Console.WriteLine("Done! Start Hollow Knight from Steam. Hollow Knight: Melee is in the mod list.");
            if (Win) Console.WriteLine("A GameCube controller adapter needs its WinUSB driver once (Zadig, as for Dolphin).");
        }

        static void Uninstall(string hk) {
            if (Process.GetProcessesByName("hollow_knight").Length > 0) throw new Exception("Hollow Knight is running: close it first");
            var managed = Steam.Managed(hk);
            if (!Directory.Exists(managed + ".vanilla")) { Console.WriteLine("Nothing to uninstall (no Managed.vanilla backup)."); return; }
            Step("restoring the game's own files");
            Directory.Delete(managed, true);
            CopyDir(managed + ".vanilla", managed);
            Console.WriteLine("Uninstalled. Your saves weren't touched.");
        }

        // the release's mod.zip: mod/ (the DLLs), core/ and tools/ (sources the build needs), kept per version
        static string Payload() {
            var local = Environment.GetEnvironmentVariable("HKM_PAYLOAD");
            if (local != null) return local;
            var dir = Path.Combine(cache, "payload-" + version);
            if (File.Exists(Path.Combine(dir, ".done"))) return dir;
            var tag = version == "0.0.0" ? null : "v" + version;
            var url = tag == null ? $"https://github.com/{Repo}/releases/latest/download/hollow-knight-melee-mod.zip"
                                  : $"https://github.com/{Repo}/releases/download/{tag}/hollow-knight-melee-mod.zip";
            Step("downloading the mod " + (tag ?? "(latest)"));
            var zip = Path.Combine(cache, "mod.zip");
            File.WriteAllBytes(zip, Http.GetByteArrayAsync(url).Result);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(zip, dir);
            File.Delete(zip);
            File.WriteAllText(Path.Combine(dir, ".done"), "");
            return dir;
        }

        static (string python, string zig) Toolchain() {
            var tools = Path.Combine(cache, "tools");
            var py = Path.Combine(tools, "python");
            var zig = Path.Combine(tools, "zig");
            if (!File.Exists(Path.Combine(py, ".done"))) {
                if (Directory.Exists(py)) Directory.Delete(py, true);
                if (Win) Unzip(Download(Pins.PythonWindows), py); else UntarGz(Download(Pins.PythonLinux), py);
                File.WriteAllText(Path.Combine(py, ".done"), "");
            }
            if (!File.Exists(Path.Combine(zig, ".done"))) {
                if (Directory.Exists(zig)) Directory.Delete(zig, true);
                if (Win) Unzip(Download(Pins.ZigWindows), zig); else UntarXz(Download(Pins.ZigLinux), zig);
                File.WriteAllText(Path.Combine(zig, ".done"), "");
            }
            var zigExe = Directory.GetFiles(zig, Win ? "zig.exe" : "zig", SearchOption.AllDirectories).First();
            string pyExe;
            if (Win) {
                pyExe = Path.Combine(py, "python.exe");
                // the embeddable Python reads its search path from python312._pth only: add the mod's tools
                var pth = Directory.GetFiles(py, "python3*._pth").First();
                File.WriteAllText(pth, "python312.zip\n.\n" + Path.Combine(Payload(), "tools", "src") + "\n");
            } else pyExe = Path.Combine(py, "python", "bin", "python3");
            return (pyExe, zigExe);
        }

        // ---- files ----
        static string Download(Pin p) {
            var dir = Path.Combine(cache, "downloads");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, p.Sha256[..16] + "-" + Path.GetFileName(new Uri(p.Url).LocalPath));
            if (File.Exists(file) && Sha(file) == p.Sha256) return file;
            Step("downloading " + p.Name);
            using (var s = Http.GetStreamAsync(p.Url).Result) using (var f = File.Create(file)) s.CopyTo(f);
            if (Sha(file) != p.Sha256) { File.Delete(file); throw new Exception($"{p.Name}: the download's checksum is wrong; try again later"); }
            return file;
        }

        static string Sha(string file) { using var f = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }

        static void Unzip(string zip, string dir) { Directory.CreateDirectory(dir); ZipFile.ExtractToDirectory(zip, dir, true); }

        static void UntarGz(string tgz, string dir) {
            Directory.CreateDirectory(dir);
            using var f = File.OpenRead(tgz);
            using var gz = new GZipStream(f, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dir, true);
        }

        static void UntarXz(string txz, string dir) {   // .NET has no xz: Linux's tar does
            Directory.CreateDirectory(dir);
            Run("tar", new[] { "-xJf", txz, "-C", dir }, null);
        }

        static void CopyDir(string from, string to) {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
        }

        static void Run(string exe, IEnumerable<string> args, string pythonPath) {
            var p = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) p.ArgumentList.Add(a);
            if (pythonPath != null) p.Environment["PYTHONPATH"] = pythonPath;
            using var proc = Process.Start(p) ?? throw new Exception("could not start " + exe);
            var err = new List<string>();
            var e = Task.Run(() => { string l; while ((l = proc.StandardError.ReadLine()) != null) lock (err) { err.Add(l); Console.WriteLine("  " + l); } });
            string line;
            while ((line = proc.StandardOutput.ReadLine()) != null) Console.WriteLine("  " + line);
            e.Wait();
            proc.WaitForExit();
            if (proc.ExitCode != 0) throw new Exception($"{Path.GetFileName(exe)} failed (exit {proc.ExitCode}): {string.Join(" / ", err.TakeLast(3))}");
        }
    }
}
