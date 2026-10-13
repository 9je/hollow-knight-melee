using Xunit;

namespace FoxMelee.Tests {
    public class MoveGatesTests {
        [Theory]
        [InlineData("Fox", false, MoveGates.WallJump)]
        [InlineData("Fox", true, 0u)]
        [InlineData("Zelda", true, MoveGates.SpecialLw)]
        [InlineData("Sheik", false, MoveGates.WallJump | MoveGates.SpecialLw)]
        public void ForCharacter(string name, bool mantisClaw, uint locked) =>
            Assert.Equal(locked, MoveGates.ForCharacter(Roster.Find(name, out _), mantisClaw));
    }
}
