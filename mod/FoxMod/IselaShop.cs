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
        static GameObject holder, marthItem;   // one Marth item, made once and reused by every visit

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

        // Marth's stock icon (stocks.bin, from the player's ISO) in place of the Quill's picture, at the Quill's size
        static void SetIcon(GameObject item) {
            try {
                var sr = item.transform.Find("Item Sprite")?.GetComponent<SpriteRenderer>();
                if (sr == null || sr.sprite == null) return;
                var s = StockIcons.Parse(System.IO.File.ReadAllBytes(System.IO.Path.Combine(FoxMod.ModDir, "stocks.bin")));
                var tex = CssArt.Texture(s.For(Roster.Find("Marth", out _)), s.Width, s.Height);
                float units = Mathf.Max(sr.sprite.bounds.size.x, sr.sprite.bounds.size.y);   // the Quill picture's size
                sr.sprite = Sprite.Create(tex, new Rect(0, 0, s.Width, s.Height), new Vector2(0.5f, 0.5f), s.Width / Mathf.Max(0.1f, units));
            } catch (System.Exception e) { FoxMod.Instance.Log("Iselda's Marth keeps the Quill's picture (" + e.Message + ")"); }
        }

        // Iselda's stock is the one selling her Quill: Marth goes first, ahead of the maps
        static void AddMarth(ShopMenuStock stock) {
            if (stock.stock == null || stock.masterList != null) return;   // a sub-list shares its master's (Marth is added there)
            var quill = stock.stock.FirstOrDefault(g => g != null && g.GetComponent<ShopItemStats>()?.playerDataBoolName == "hasQuill");
            if (quill == null || stock.stock.Any(g => g != null && g.GetComponent<ShopItemStats>()?.playerDataBoolName == Flag)) return;
            // made under an inactive holder, so the copy's Awake (which reads its price) waits for the shop's own copies
            if (holder == null) { holder = new GameObject("Hollow Knight: Melee shop items"); holder.SetActive(false); Object.DontDestroyOnLoad(holder); }
            if (marthItem != null) { Prepend(stock, marthItem); return; }
            var marth = marthItem = Object.Instantiate(quill, holder.transform);
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
            SetIcon(marth);
            Prepend(stock, marth);
            FoxMod.Instance.Log("Iselda's shop: Marth added");
        }

        static void Prepend(ShopMenuStock stock, GameObject marth) {
            stock.stock = new[] { marth }.Concat(stock.stock).ToArray();
            if (stock.stockAlt != null && stock.stockAlt.Length > 0) stock.stockAlt = new[] { marth }.Concat(stock.stockAlt).ToArray();
        }
    }
}
