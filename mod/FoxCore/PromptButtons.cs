namespace FoxMelee {
    /// Hollow Knight's on-screen button prompts (UIButtonSkins, by HeroActionButton name) for what Fox uses instead:
    /// a UIButtonSkins sprite name for the GameCube controller, a key for the keyboard; null keeps Hollow Knight's.
    public static class PromptButtons {
        public static string ForGameCube(string action) {
            switch (action) {
                case "UP": return "dpadLeft";            // interact: D-pad left (HkControls)
                case "INVENTORY": case "QUICK_MAP": return "dpadRight";   // tap / hold (DPadRight)
                case "CAST": case "QUICK_CAST": return "dpadDown";   // Focus
                case "ATTACK": return "a";
                case "JUMP": return "x";
                default: return null;
            }
        }

        public static string ForKeyboard(string action) {
            switch (action) {
                case "UP": return "E";
                case "CAST": case "QUICK_CAST": return "F";
                case "ATTACK": return "J";
                default: return null;
            }
        }
    }
}
