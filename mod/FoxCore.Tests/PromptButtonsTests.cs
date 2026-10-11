using Xunit;

namespace FoxMelee.Tests {
    public class PromptButtonsTests {
        [Theory]
        [InlineData("UP", "dpadLeft")]          // interact (Listen, Rest, Enter): D-pad left
        [InlineData("CAST", "dpadDown")]        // Focus
        [InlineData("QUICK_CAST", "dpadDown")]
        [InlineData("ATTACK", "a")]             // Fox's attacks and jump, as Melee has them
        [InlineData("JUMP", "x")]
        [InlineData("INVENTORY", "dpadRight")]
        [InlineData("QUICK_MAP", null)]         // Hollow Knight's own buttons stay as they are
        [InlineData("MENU_SUBMIT", null)]
        public void GameCubePromptsShowFoxsControls(string action, string button) =>
            Assert.Equal(button, PromptButtons.ForGameCube(action));

        [Theory]
        [InlineData("UP", "E")]
        [InlineData("CAST", "F")]
        [InlineData("QUICK_CAST", "F")]
        [InlineData("ATTACK", "J")]
        [InlineData("JUMP", null)]
        public void KeyboardPromptsShowFoxsKeys(string action, string key) =>
            Assert.Equal(key, PromptButtons.ForKeyboard(action));

        [Theory]
        [InlineData(true, 33, 3, 5, null)]
        [InlineData(true, 20, 3, 5, "needs 33 soul (has 20)")]
        [InlineData(true, 99, 5, 5, "masks are full")]
        [InlineData(false, 99, 3, 5, "Fox must stand still on the ground")]
        public void FocusSaysWhyItCannotStart(bool still, int soul, int health, int max, string why) =>
            Assert.Equal(why, FocusRules.WhyNot(still, soul, 33, health, max));
    }
}
