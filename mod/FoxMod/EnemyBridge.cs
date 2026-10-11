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
        readonly EnemyHits hits;
        readonly System.Func<bool> foxDrives;
        readonly List<Pending> pending = new List<Pending>(), landed = new List<Pending>(), later = new List<Pending>();
        bool passThrough;

        public EnemyBridge(EnemyHitTuning tuning, System.Func<bool> foxDrives) {
            this.tuning = tuning;
            this.foxDrives = foxDrives;
            hits = new EnemyHits(tuning.CooldownFrames);
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
        public bool Frame(MeleeCore core, RoomTransform room, Vector2 knight) {
            later.Clear();
            foreach (var p in pending) {
                var from = p.Go != null ? (Vector2)p.Go.transform.position : knight;
                var at = room.ToMelee(new V2(from.x, from.y));
                int r = core.ApplyHit(EnemyHits.ToMelee(tuning, p.Amount, (float)at.X, (float)at.Y, tuning.UnblockableFromMasks));
                if (r < 0) { pending.Clear(); return false; }
                if (r == McHit.Landed) landed.Add(p);
                else if (r == McHit.Later) later.Add(p);
            }
            pending.Clear();
            pending.AddRange(later);   // more hits than one frame takes: the next frame's pause
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
            hits.Tick();
        }

        /// Hollow Knight takes the Knight (transitions, death): nothing held back carries over.
        public void Clear() { pending.Clear(); landed.Clear(); hits.Clear(); }
    }
}
