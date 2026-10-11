using System.Collections.Generic;
using GlobalEnums;
using UnityEngine;

namespace FoxMelee {
    /// Spec §5.2: Hollow Knight enemies hitting Fox. While Fox plays, an enemy hit on the Knight (HeroController.TakeDamage,
    /// hazard type 1) is held back; at the next Melee frame's hit pause it is applied to Fox through Melee
    /// (mc_apply_hit), and only a hit Melee lands reaches Hollow Knight (masks, HUD, death). Each enemy then waits its
    /// cooldown; the Knight's long invulnerability does not apply. Spikes and other hazards stay Hollow Knight's.
    sealed class EnemyBridge {
        struct Pending { public GameObject Go; public CollisionSide Side; public int Amount, Hazard; }

        readonly EnemyHitTuning tuning;
        readonly EnemyHits hits;
        readonly System.Func<bool> foxDrives;
        readonly List<Pending> pending = new List<Pending>(), landed = new List<Pending>();
        bool passThrough;

        public EnemyBridge(EnemyHitTuning tuning, System.Func<bool> foxDrives) {
            this.tuning = tuning;
            this.foxDrives = foxDrives;
            hits = new EnemyHits(tuning.CooldownFrames);
            On.HeroController.TakeDamage += TakeDamage;
        }

        void TakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self, GameObject go, CollisionSide side, int amount, int hazardType) {
            if (passThrough || hazardType != 1 || amount <= 0 || !foxDrives()) { orig(self, go, side, amount, hazardType); return; }
            object source = go != null ? (object)go : self;
            if (!hits.Accept(source)) return;   // this enemy hit Fox moments ago
            pending.Add(new Pending { Go = go, Side = side, Amount = amount, Hazard = hazardType });
        }

        /// At the hit pause (mc_step_to_hits): Melee resolves each held-back hit.
        public bool Frame(MeleeCore core, RoomTransform room, Vector2 knight) {
            foreach (var p in pending) {
                var from = p.Go != null ? (Vector2)p.Go.transform.position : knight;
                var at = room.ToMelee(new V2(from.x, from.y));
                int r = core.ApplyHit(EnemyHits.ToMelee(tuning, p.Amount, (float)at.X, (float)at.Y, tuning.UnblockableFromMasks));
                if (r < 0) { pending.Clear(); return false; }
                if (r == McHit.Landed) landed.Add(p);
            }
            pending.Clear();
            return true;
        }

        /// After the frame: landed hits cost masks as Hollow Knight's own hits do; cooldowns count down.
        public void AfterFrame(HeroController hero) {
            foreach (var p in landed) {
                passThrough = true;
                try { hero.TakeDamage(p.Go, p.Side, p.Amount, p.Hazard); }
                finally { passThrough = false; }
                hero.cState.invulnerable = false;   // the next enemy's landed hit counts too (cooldowns are per enemy)
                hero.cState.recoiling = false;
            }
            landed.Clear();
            hits.Tick();
        }

        /// Hollow Knight takes the Knight (transitions, death): nothing held back carries over.
        public void Clear() { pending.Clear(); landed.Clear(); hits.Clear(); }
    }
}
