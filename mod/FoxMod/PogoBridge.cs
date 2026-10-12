using GlobalEnums;
using UnityEngine;

namespace FoxMelee {
    /// Spec §5.4: Fox's down-air touching spikes (Hollow Knight's hazard type 2: spikes, thorns, saws) bounces him; for the
    /// bounce's grace the spikes under him don't take the Knight. Acid, pits and enemies are never pogoed.
    sealed class PogoBridge {
        const int Spikes = 2;   // DamageHero.hazardType (HeroController.TakeDamage: 2 spikes, 3 acid, 5 pit)
        readonly PogoTuning tuning;
        readonly Pogo pogo;
        readonly System.Func<bool> foxDrives;
        readonly McHitbox[] boxes = new McHitbox[8];
        readonly Collider2D[] found = new Collider2D[16];

        public PogoBridge(PogoTuning tuning, System.Func<bool> foxDrives) {
            this.tuning = tuning; this.foxDrives = foxDrives;
            pogo = new Pogo(tuning);
            On.HeroController.TakeDamage += TakeDamage;
        }

        void TakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self, GameObject go, CollisionSide side, int amount, int hazardType) {
            if (hazardType == Spikes && pogo.SpikesIgnored && foxDrives()) return;   // the spikes Fox just bounced off
            orig(self, go, side, amount, hazardType);
        }

        /// After each Melee frame (its logic done): bounce if a down-air hitbox touches spikes. False if the core failed.
        public bool Frame(MeleeCore core, RoomTransform room, McFighter fox) {
            bool touching = false;
            if (fox.Action == 69) {   // AttackAirLw: only then are hitboxes looked at
                int n = core.ReadHitboxes(boxes);
                if (n < 0) return false;
                for (int i = 0; i < n && !touching; i++) {
                    if (HitRules.IsItem(boxes[i])) continue;
                    var p = room.ToHk(new V2(boxes[i].X, boxes[i].Y));
                    int k = Physics2D.OverlapCircleNonAlloc(new Vector2((float)p.X, (float)p.Y), (float)(boxes[i].Radius / room.Scale), found);
                    for (int j = 0; j < k && !touching; j++) {
                        var d = found[j].GetComponentInParent<DamageHero>();
                        touching = d != null && d.hazardType == Spikes;
                    }
                }
            }
            if (!pogo.Frame(fox.Action, fox.SelfVelY, touching)) return true;
            return core.Bounce(0, (float)tuning.Velocity) >= 0;
        }
    }
}
