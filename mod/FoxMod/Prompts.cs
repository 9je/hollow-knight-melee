using System.Reflection;
using GlobalEnums;
using InControl;

namespace FoxMelee {
    /// Hollow Knight's on-screen button prompts show what Fox uses (PromptButtons) while he plays: D-pad left to
    /// interact, D-pad down to Focus on the GameCube controller; E and F on the keyboard.
    static class Prompts {
        public static bool Active;              // FoxRunner: Fox plays this room
        static bool hooked;

        public static void Hook() {
            if (hooked) return;
            hooked = true;
            On.UIButtonSkins.GetButtonSkinFor_HeroActionButton += (orig, self, action) => Skin(self, action) ?? orig(self, action);
        }

        static ButtonSkin Skin(UIButtonSkins skins, HeroActionButton action) {
            if (!Active) return null;
            if (InputManager.ActiveDevice is GcInput.GcDevice && InputHandler.Instance?.lastActiveController == BindingSourceType.DeviceBindingSource) {
                var name = PromptButtons.ForGameCube(action.ToString());
                var sprite = name == null ? null : typeof(UIButtonSkins).GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(skins) as UnityEngine.Sprite;
                return sprite == null ? null : new ButtonSkin(sprite, "", ButtonSkinType.BLANK);
            }
            if (InputHandler.Instance?.lastActiveController == BindingSourceType.KeyBindingSource) {
                var key = PromptButtons.ForKeyboard(action.ToString());
                return key == null ? null : new ButtonSkin(skins.squareKey, key, ButtonSkinType.SQUARE);
            }
            return null;
        }
    }
}
