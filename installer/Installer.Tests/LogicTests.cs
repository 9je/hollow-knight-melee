using System.IO;
using System.Linq;
using Xunit;

namespace HkMelee.Installer.Tests {
    public class LogicTests {
        [Fact]
        public void TheGamesEngineVersionFromItsHeader() {
            var head = new byte[48];
            System.Text.Encoding.ASCII.GetBytes("2020.2.2f1").CopyTo(head, 20);
            Assert.Equal("2020.2.2f1", Steam.EngineVersion(head));
            var six = new byte[48];
            System.Text.Encoding.ASCII.GetBytes("6000.0.61f1").CopyTo(six, 20);
            Assert.Equal("6000.0.61f1", Steam.EngineVersion(six));
            Assert.Null(Steam.EngineVersion(new byte[48]));
        }

        [Fact]
        public void SteamLibrariesFromLibraryFoldersVdf() {
            var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"label\"\t\t\"\"\n\t}\n" +
                      "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n";
            Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, Steam.LibraryPaths(vdf).ToArray());
        }

        [Theory]
        [InlineData("v0.2.10", "0.2.9", true)]
        [InlineData("v0.2.9", "0.2.9", false)]
        [InlineData("v0.2.8", "0.2.9", false)]
        [InlineData("v1.0.0", "0.9.99", true)]
        [InlineData("nonsense", "0.2.9", false)]
        public void NewerVersions(string tag, string mine, bool newer) => Assert.Equal(newer, Versions.IsNewer(tag, mine));

        [Fact]
        public void SettingsKeepEverythingButAMissingIsoPath() {
            var merged = SettingsFile.WithIso("{\n  \"IsoPath\": \"\",\n  \"Character\": \"Marth\"\n}", "/games/melee.iso");
            Assert.Contains("\"Character\": \"Marth\"", merged);
            Assert.Contains("\"IsoPath\": \"/games/melee.iso\"", merged);
            Assert.Contains("\"IsoPath\": \"/games/melee.iso\"", SettingsFile.WithIso(null, "/games/melee.iso"));
            Assert.Contains("\"IsoPath\": \"/games/melee.iso\"", SettingsFile.WithIso("not json", "/games/melee.iso"));
        }

        [Fact]
        public void PinsAreHttpsWithSha256() {
            foreach (var p in Pins.All) {
                Assert.StartsWith("https://", p.Url);
                Assert.Matches("^[0-9a-f]{64}$", p.Sha256);
            }
        }

        // the ISO is picked by the player (a file window, or a path dragged in), never searched for
        [Fact]
        public void TheIsoIsPickedInAFileWindow() {
            var win = FilePicker.Commands(windows: true, has: _ => false);
            Assert.Single(win);
            Assert.Equal("powershell", win[0].exe);
            Assert.Contains("OpenFileDialog", string.Join(" ", win[0].args));
            var linux = FilePicker.Commands(windows: false, has: t => t == "kdialog");
            Assert.Equal(new[] { "kdialog" }, linux.Select(c => c.exe));
            Assert.Empty(FilePicker.Commands(windows: false, has: _ => false));   // no picker: the path is dragged in
            Assert.Equal("/a b/Melee.iso", FilePicker.Chosen("/a b/Melee.iso\n"));
            Assert.Null(FilePicker.Chosen("  \n"));                              // cancelled
        }

        [Fact]
        public void OnlyVanillaMelee102IsAccepted() {
            var f = Path.GetTempFileName();
            File.WriteAllBytes(f, new byte[1000]);
            Assert.False(IsoFinder.IsMelee102(f));          // wrong size
            Assert.False(IsoFinder.IsMelee102(f + ".missing"));
            Assert.Equal("d4e70c064cc714ba8400a849cf299dbd1aa326fc", IsoFinder.RedumpSha1);
        }

        static string Dir(params (string name, string text)[] files) {
            var d = Directory.CreateTempSubdirectory().FullName;
            foreach (var (n, t) in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(d, n))); File.WriteAllText(Path.Combine(d, n), t); }
            return d;
        }

        [Fact]
        public void ABackupIsMadeOnceAndOnlyWhole() {
            var src = Dir(("a.dll", "1"), ("sub/b.dll", "2"));
            var dst = src + ".vanilla";
            Directory.CreateDirectory(dst + ".tmp");                          // a copy interrupted last time
            File.WriteAllText(Path.Combine(dst + ".tmp", "junk"), "x");
            Assert.True(Files.BackupOnce(src, dst));
            Assert.Equal("2", File.ReadAllText(Path.Combine(dst, "sub/b.dll")));
            Assert.False(File.Exists(Path.Combine(dst, "junk")));
            Assert.False(Directory.Exists(dst + ".tmp"));
            File.WriteAllText(Path.Combine(src, "a.dll"), "modded");
            Assert.False(Files.BackupOnce(src, dst));                        // never again: the first backup stays
            Assert.Equal("1", File.ReadAllText(Path.Combine(dst, "a.dll")));
        }

        [Fact]
        public void RestoringReplacesTheFolderOrLeavesItAsItWas() {
            var vanilla = Dir(("Assembly-CSharp.dll", "vanilla"));
            var managed = Dir(("Assembly-CSharp.dll", "api"), ("Mods/FoxMelee/FoxMod.dll", "mod"));
            Files.Restore(vanilla, managed);
            Assert.Equal("vanilla", File.ReadAllText(Path.Combine(managed, "Assembly-CSharp.dll")));
            Assert.False(Directory.Exists(Path.Combine(managed, "Mods")));
            Assert.False(Directory.Exists(managed + ".uninstalling"));
            Assert.Throws<DirectoryNotFoundException>(() => Files.Restore(vanilla + "-missing", managed));
            Assert.Equal("vanilla", File.ReadAllText(Path.Combine(managed, "Assembly-CSharp.dll")));   // untouched
        }

        [Fact]
        public void TheNewestReleaseIncludesPreReleasesButNotDrafts() {
            var json = "[{\"tag_name\":\"v0.2.9\",\"draft\":true},{\"tag_name\":\"v0.2.8\",\"prerelease\":true},{\"tag_name\":\"v0.2.7\"}]";
            Assert.Equal("v0.2.8", (string)Releases.Newest(json)?["tag_name"]);
            Assert.Null(Releases.Newest("[]"));
        }

        [Fact]
        public void OldModDownloadsAreRemovedButNotTheCurrentOne() {
            var cache = Dir(("payload-0.2.5/mod/FoxMod.dll", "old"), ("payload-0.2.9/mod/FoxMod.dll", "new"), ("tools/zig/zig", "z"), ("installer.json", "{}"));
            Files.CleanOldPayloads(cache, "payload-0.2.9");
            Assert.False(Directory.Exists(Path.Combine(cache, "payload-0.2.5")));
            Assert.True(File.Exists(Path.Combine(cache, "payload-0.2.9/mod/FoxMod.dll")));
            Assert.True(File.Exists(Path.Combine(cache, "tools/zig/zig")));
            Assert.True(File.Exists(Path.Combine(cache, "installer.json")));
        }
    }
}
