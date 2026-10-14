using System.Collections.Generic;
using System.Reflection;
using GlobalEnums;
using UnityEngine;

namespace FoxMelee {
    /// Spec §5.2: Hollow Knight enemies hitting Fox. While Fox plays, an enemy hit on the Knight (HeroController.TakeDamage,
    /// hazard type 1) that Hollow Knight would accept is held back; at the next Melee frame's hit pause it is applied to
    /// Fox through Melee (mc_apply_hit), and only a hit Melee lands costs masks (HeroController.TakeHealth: masks and HUD;
    /// a lethal one takes Hollow Knight's own path to death). No Knight recoil, damage freeze or long invulnerability:
    /// Melee's hitlag and knockback are Fox's, and each enemy waits its cooldown. Spikes and hazards stay Hollow Knight's.
    sealed class EnemyBridge {
        struct Pending { public GameObject Go; public CollisionSide Side; public int Amount, Hazard; }

        readonly EnemyHitTuning tuning;
        readonly EnemyHits hits, nanaHits;
        readonly System.Func<bool> foxDrives;
        readonly List<Pending> pending = new List<Pending>(), landed = new List<Pending>(), later = new List<Pending>();
        bool passThrough;
        /// The Ice Climbers: Nana is hit on her own (NanaFrame), her percent and knockback hers, no masks of the Knight's.
        public bool Nana;
        readonly McHurtbox[] nanaHurt = new McHurtbox[16];
        readonly Collider2D[] found = new Collider2D[32];
        ContactFilter2D filter;

        public EnemyBridge(EnemyHitTuning tuning, System.Func<bool> foxDrives) {
            this.tuning = tuning;
            this.foxDrives = foxDrives;
            hits = new EnemyHits(tuning.CooldownFrames);
            nanaHits = new EnemyHits(tuning.CooldownFrames);
            filter = new ContactFilter2D();
            filter.NoFilter();
            filter.useTriggers = true;   // enemies' DamageHero colliders are triggers
            On.HeroController.TakeDamage += TakeDamage;
        }

        static readonly MethodInfo CanTakeDamage = typeof(HeroController).GetMethod("CanTakeDamage", BindingFlags.Instance | BindingFlags.NonPublic);

        /// Hollow Knight's own gate for an enemy hit (TakeDamage): respawn invulnerability, invincibility, transitions,
        /// hazard-only mode, parries, shadow dashing.
        static bool HollowWouldAccept(HeroController h) =>
            (CanTakeDamage == null || (bool)CanTakeDamage.Invoke(h, null))
            && h.damageMode != DamageMode.HAZARD_ONLY && !h.cState.shadowDashing && h.parryInvulnTimer <= 0f;

        void TakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self, GameObject go, CollisionSide side, int amount, int hazardType) {
            if (passThrough || hazardType != 1 || amount <= 0 || !foxDrives()) { orig(self, go, side, amount, hazardType); return; }
            if (!HollowWouldAccept(self)) return;
            if (!hits.Accept(Enemy(go, self))) return;   // this enemy hit Fox moments ago
            pending.Add(new Pending { Go = go, Side = side, Amount = amount, Hazard = hazardType });
        }

        /// One cooldown per enemy, not per collider (an enemy's body and its attack are different colliders).
        static object Enemy(GameObject go, HeroController self) {
            if (go == null) return self;
            var hm = go.GetComponentInParent<HealthManager>();
            return hm != null ? (object)hm.gameObject : go;
        }

        /// Fox's attack landed on this enemy (a HealthManager object): its body can't hurt him for the grace.
        public void Guard(GameObject enemy) => hits.Guard(enemy, tuning.GraceFrames);

        /// At the hit pause (mc_step_to_hits): Melee resolves each held-back hit.
        public bool Frame(MeleeCore core, RoomTransform room, Vector2 knight, HeroController hero) {
            later.Clear();
            foreach (var p in pending) {
                var from = p.Go != null ? (Vector2)p.Go.transform.position : knight;
                var at = room.ToMelee(new V2(from.x, from.y));
                int r = core.ApplyHit(EnemyHits.ToMelee(tuning, p.Amount, (float)at.X, (float)at.Y, tuning.UnblockableFromMasks));
                if (r < 0) { pending.Clear(); return false; }
                if (r == McHit.Landed) { landed.Add(p); hits.Invulnerable(tuning.InvulnerableFrames); }   // the Knight's moment of invincibility
                else if (r == McHit.Later) later.Add(p);
            }
            pending.Clear();
            pending.AddRange(later);   // more hits than one frame takes: the next frame's pause
            return Nana ? NanaFrame(core, room, hero) : true;
        }

        /// Nana, at the hit pause: Hollow Knight tests its enemies against the Knight's box only, which stands at Popo, so
        /// an enemy's DamageHero collider touching her hurtboxes is her hit, applied to her through Melee. Her own cooldown
        /// per enemy and invulnerability; the Knight's gate (respawn invulnerability and the rest) holds for her too.
        /// Only what the Knight's own HeroBox would take: a collider on a layer that meets his, with the DamageHero on the
        /// collider's own object (an enemy's wide, invisible detection trigger is a child of the enemy and used to count as
        /// its hit: Nana was knocked about by nothing).
        bool nanaFilterSet;
        bool NanaFrame(MeleeCore core, RoomTransform room, HeroController hero) {
            int n = core.ReadHurtboxes(nanaHurt, 1);
            if (n <= 0 || !HollowWouldAccept(hero)) return true;
            if (!nanaFilterSet) {
                nanaFilterSet = true;
                var box = hero.GetComponentInChildren<HeroBox>(true);
                if (box != null) { filter.SetLayerMask(Physics2D.GetLayerCollisionMask(box.gameObject.layer)); filter.useLayerMask = true; }
                FoxMod.Instance.Log($"Nana's hits: the Knight's box layer {(box != null ? LayerMask.LayerToName(box.gameObject.layer) : "not found")}");
            }
            for (int i = 0; i < n; i++) {
                var b = nanaHurt[i];
                if (b.State != 0) continue;
                var a = room.ToHk(new V2(b.Ax, b.Ay)); var z = room.ToHk(new V2(b.Bx, b.By));
                float r = (float)(b.Radius / room.Scale);
                int k = Physics2D.OverlapCapsule(new Vector2((float)(a.X + z.X) / 2, (float)(a.Y + z.Y) / 2),
                                                 new Vector2(2 * r, Vector2.Distance(new Vector2((float)a.X, (float)a.Y), new Vector2((float)z.X, (float)z.Y)) + 2 * r),
                                                 CapsuleDirection2D.Vertical, Mathf.Atan2((float)(z.X - a.X), (float)(z.Y - a.Y)) * -Mathf.Rad2Deg, filter, found);
                for (int j = 0; j < k; j++) {
                    var d = found[j].GetComponent<DamageHero>();   // on the collider's own object, as HeroBox.CheckForDamage reads it
                    if (d == null || d.hazardType != 1 || d.damageDealt <= 0 || !found[j].enabled) continue;
                    var enemy = Enemy(found[j].gameObject, hero);
                    if (!nanaHits.Accept(enemy)) continue;
                    var from = room.ToMelee(new V2(found[j].transform.position.x, found[j].transform.position.y));
                    int rc = core.ApplyHitToFollower(EnemyHits.ToMelee(tuning, d.damageDealt, (float)from.X, (float)from.Y, tuning.UnblockableFromMasks));
                    if (rc < 0) return false;
                    if (rc == McHit.Landed) nanaHits.Invulnerable(tuning.InvulnerableFrames);
                    FoxMod.Instance.Log($"Nana hit by {(enemy as GameObject)?.name ?? "?"} ({found[j].name}, {d.damageDealt}): {(rc == McHit.Landed ? "landed" : rc == McHit.Shielded ? "shielded" : "missed")}");
                }
            }
            return true;
        }

        /// After the frame: landed hits cost masks as Hollow Knight's own hits do; cooldowns count down.
        public void AfterFrame(HeroController hero) {
            var pd = PlayerData.instance;
            foreach (var p in landed) {
                int masks = pd.GetBool("overcharmed") ? 2 * p.Amount : p.Amount;
                if (pd.GetInt("health") + pd.GetInt("healthBlue") > masks) { hero.TakeHealth(masks); continue; }
                passThrough = true;   // the last mask: Hollow Knight's own damage, death and shade
                try { hero.TakeDamage(p.Go, p.Side, p.Amount, p.Hazard); }
                finally { passThrough = false; }
            }
            landed.Clear();
            hits.Tick(); nanaHits.Tick();
        }

        /// Hollow Knight takes the Knight (transitions, death): nothing held back carries over.
        public void Clear() { pending.Clear(); landed.Clear(); hits.Clear(); nanaHits.Clear(); }
    }
}
