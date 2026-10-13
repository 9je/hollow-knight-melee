using System.Linq;
using Modding;
using UnityEngine;

namespace FoxMelee {
    /// Iselda's map shop in Dirtmouth sells Marth (spec 2026-10-12-melee-roster-design.md §1), as the randomizer adds shop
    /// items: a copy of her Quill item with its own save flag (kept in the mod's per-save data through the Modding API's
    /// bool hooks), name, description and price (through its language hook; prices are text in the "Prices" sheet).
    static class IselaShop {
        public const string Flag = "hkMeleeMarth";
        const string NameKey = "HKM_MARTH_NAME", DescKey = "HKM_MARTH_DESC", PriceKey = "HKM_MARTH_PRICE";
        public const int Price = 50;
        static GameObject holder;

        public static void Hook() {
            ModHooks.GetPlayerBoolHook += (name, orig) => name == Flag ? FoxMod.Instance.Save.MarthBought : orig;
            ModHooks.SetPlayerBoolHook += (name, value) => {
                if (name == Flag) {
                    if (value && !FoxMod.Instance.Save.MarthBought) FoxMod.Instance.Log("Marth bought from Iselda");
                    FoxMod.Instance.Save.MarthBought = value;
                }
                return value;
            };
            ModHooks.LanguageGetHook += (key, sheet, orig) =>
                key == NameKey ? "Marth"
                : key == DescKey ? "The Hero-King of Altea, from a land beyond the kingdom's edge. His sword reaches far, and hits hardest at its tip.\n\nChoose him at a bench."
                : key == PriceKey ? Price.ToString()
                : orig;
            On.ShopMenuStock.Start += (orig, self) => { AddMarth(self); orig(self); };
        }

        // Iselda's stock is the one selling her Quill: Marth goes first, ahead of the maps
        static void AddMarth(ShopMenuStock stock) {
            if (stock.stock == null) return;
            var quill = stock.stock.FirstOrDefault(g => g != null && g.GetComponent<ShopItemStats>()?.playerDataBoolName == "hasQuill");
            if (quill == null || stock.stock.Any(g => g != null && g.GetComponent<ShopItemStats>()?.playerDataBoolName == Flag)) return;
            // made under an inactive holder, so the copy's Awake (which reads its price) waits for the shop's own copies
            if (holder == null) { holder = new GameObject("Hollow Knight: Melee shop items"); holder.SetActive(false); Object.DontDestroyOnLoad(holder); }
            var marth = Object.Instantiate(quill, holder.transform);
            marth.name = "Marth";
            var item = marth.GetComponent<ShopItemStats>();
            item.playerDataBoolName = Flag;
            item.nameConvo = NameKey;
            item.descConvo = DescKey;
            item.priceConvo = PriceKey;
            item.requiredPlayerDataBool = "";
            item.removalPlayerDataBool = "";
            item.specialType = 0;
            item.relic = false;
            item.charmsRequired = 0;
            stock.stock = new[] { marth }.Concat(stock.stock).ToArray();
            if (stock.stockAlt != null && stock.stockAlt.Length > 0) stock.stockAlt = new[] { marth }.Concat(stock.stockAlt).ToArray();
            FoxMod.Instance.Log("Iselda's shop: Marth added");
        }
    }
}
