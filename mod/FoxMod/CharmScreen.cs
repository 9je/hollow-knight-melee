using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Hollow Knight's charm screen at a bench is the character select: when the inventory opens while the Knight sits,
    /// Melee's select opens over it (the inventory, muted, stays open under it) with the row of charms that are still
    /// charms under the characters; closing the select closes the inventory. The charms equip and
    /// unequip as Hollow Knight's own screen does (PlayerData's equippedCharm_N and list, the notches, CharmUpdate and the
    /// charm events), without overcharming.
    static class CharmScreen {
        /// Hollow Knight's inventory closes (its INVENTORY CANCEL: the charm equip check and the Knight's control back).
        public static void CloseInventory(GameManager gm) {
            var inv = gm != null ? gm.inventoryFSM : null;
            if (inv != null) inv.SendEvent("INVENTORY CANCEL");
        }

        /// The save's charms that stay charms, in Hollow Knight's order.
        public static List<int> Owned() {
            var pd = PlayerData.instance;
            return pd == null ? new List<int>() : CharmRules.Normal(id => pd.GetBool("gotCharm_" + id));
        }

        public struct Slot { public int Id; public bool Equipped; public int Cost; public Texture2D Tex; public Rect Uv; public string Name; }

        public static Slot View(int id) {
            var pd = PlayerData.instance;
            var s = new Slot { Id = id, Equipped = pd.GetBool("equippedCharm_" + id), Cost = pd.GetInt("charmCost_" + id) };
            try {
                var sp = CharmIconList.Instance != null ? CharmIconList.Instance.GetSprite(id) : null;
                if (sp != null && sp.texture != null) {
                    var tr = sp.textureRect; var t = sp.texture;
                    s.Tex = t; s.Uv = new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height);
                }
                s.Name = Language.Language.Get("CHARM_NAME_" + id, "UI");
            } catch (System.Exception) { }
            return s;
        }

        public static (int filled, int slots) Notches() {
            var pd = PlayerData.instance;
            pd.CalculateNotchesUsed();
            return (pd.GetInt("charmSlotsFilled"), pd.GetInt("charmSlots"));
        }

        /// Equips or unequips the charm, Hollow Knight's way; what happened, for the log.
        public static string Toggle(int id) {
            var pd = PlayerData.instance;
            var hero = HeroController.instance;
            bool on = pd.GetBool("equippedCharm_" + id);
            int cost = pd.GetInt("charmCost_" + id);
            pd.CalculateNotchesUsed();
            int slots = pd.GetInt("charmSlots"), filled = pd.GetInt("charmSlotsFilled");
            if (!on) {
                if (!CharmRules.CanEquip(cost, slots, filled)) return $"charm {id}: no room ({filled} of {slots} notches used, it costs {cost})";
                pd.SetBool("equippedCharm_" + id, true);
                pd.EquipCharm(id);
            } else {
                pd.SetBool("equippedCharm_" + id, false);
                pd.UnequipCharm(id);
            }
            pd.CalculateNotchesUsed();
            if (hero != null) hero.CharmUpdate();
            PlayMakerFSM.BroadcastEvent("CHARM EQUIP CHECK");
            PlayMakerFSM.BroadcastEvent("CHARM INDICATOR CHECK");
            return $"charm {id} {(on ? "unequipped" : "equipped")} ({pd.GetInt("charmSlotsFilled")} of {slots} notches)";
        }
    }
}
