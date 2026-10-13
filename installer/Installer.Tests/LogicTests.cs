using System.IO;
using System.Linq;
using Xunit;

namespace HkMelee.Installer.Tests {
    public class LogicTests {
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

        [Fact]
        public void IsoCandidatesHaveTheRedumpSize() {
            var dir = Directory.CreateTempSubdirectory().FullName;
            File.WriteAllBytes(Path.Combine(dir, "small.iso"), new byte[10]);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
            Assert.Empty(IsoFinder.Candidates(new[] { dir }));
        }
    }
}
