using Modding;

namespace FoxMelee {
    /// Spec 2026-10-12-melee-roster-design.md §1: a character's charm is that character, not a charm. Picking it up still
    /// sets gotCharm_N (the unlock, kept in the save as usual), but it never equips (PlayerData.EquipCharm refuses it and
    /// its equippedCharm_N stays false, so none of its Hollow Knight effects apply), costs no notches, and the charm menu
    /// names the character and says where to choose it. One equipped in an older save is taken off when the save loads.
    static class CharacterCharms {
        public static void Hook() {
            On.PlayerData.EquipCharm += (orig, self, num) => { if (Roster.ForCharm(num) == null) orig(self, num); };
            ModHooks.SetPlayerBoolHook += (name, value) => value && IsCharacterFlag(name, "equippedCharm_") ? false : value;
            ModHooks.GetPlayerBoolHook += (name, orig) => orig && IsCharacterFlag(name, "equippedCharm_") ? false : orig;
            ModHooks.GetPlayerIntHook += (name, orig) => IsCharacterFlag(name, "charmCost_") ? 0 : orig;
            ModHooks.LanguageGetHook += (key, sheet, orig) => {
                if (key.StartsWith("CHARM_NAME_") && int.TryParse(key.Substring(11), out int n) && Roster.ForCharm(n) is Character c)
                    return $"{c.Name} ({orig})";
                if (key.StartsWith("CHARM_DESC_") && int.TryParse(key.Substring(11), out n) && Roster.ForCharm(n) is Character d)
                    return $"{d.Name}, from Super Smash Bros. Melee.\n\nChoose your character at a bench (D-pad up). It isn't worn as a charm.";
                return orig;
            };
            ModHooks.AfterSavegameLoadHook += _ => TakeOff();
        }

        static bool IsCharacterFlag(string name, string prefix) =>
            name.StartsWith(prefix) && int.TryParse(name.Substring(prefix.Length), out int n) && Roster.ForCharm(n) != null;

        static void TakeOff() {
            var pd = PlayerData.instance;
            if (pd == null) return;
            bool changed = false;
            foreach (var c in Roster.All) {
                if (c.Charm == 0) continue;
                var f = typeof(PlayerData).GetField("equippedCharm_" + c.Charm);
                if (f != null && (bool)f.GetValue(pd)) { f.SetValue(pd, false); changed = true; }
                if (pd.equippedCharms != null && pd.equippedCharms.Remove(c.Charm)) changed = true;
            }
            if (changed) {
                pd.CalculateNotchesUsed();
                pd.overcharmed = pd.charmSlotsFilled > pd.charmSlots;   // a character charm may have been what overcharmed it
                FoxMod.Instance.Log("character charms taken off (they're characters now)");
            }
        }
    }
}
