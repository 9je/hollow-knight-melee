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
        readonly Collider2D[] found = new Collider2D[64];
        ContactFilter2D filter;   // every layer, triggers included (spikes are triggers on their own layers)
        const float FeetProbe = 4f;   // Melee units around Fox's feet: down-air frames without an active hitbox still pogo

        public PogoBridge(PogoTuning tuning, System.Func<bool> foxDrives) {
            this.tuning = tuning; this.foxDrives = foxDrives;
            pogo = new Pogo(tuning);
            filter = new ContactFilter2D();
            filter.NoFilter();
            filter.useTriggers = true;
            On.HeroController.TakeDamage += TakeDamage;
        }

        void TakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self, GameObject go, CollisionSide side, int amount, int hazardType) {
            if (hazardType == Spikes && pogo.SpikesIgnored && foxDrives()) return;   // the spikes Fox just bounced off
            orig(self, go, side, amount, hazardType);
        }

        /// After each Melee frame (its logic done): bounce if Fox's down-air touches spikes. False if the core failed.
        public bool Frame(MeleeCore core, RoomTransform room, McFighter fox) {
            bool touching = false;
            if (fox.Action == 69) {   // AttackAirLw: only then is anything looked at
                touching = TouchesSpikes(room, fox.X, fox.Y, FeetProbe);
                int n = touching ? 0 : core.ReadHitboxes(boxes);
                if (n < 0) return false;
                for (int i = 0; i < n && !touching; i++) {
                    if (HitRules.IsItem(boxes[i])) continue;
                    for (int k = 0; k <= 2 && !touching; k++)   // swept from last frame's centre, as CombatBridge does
                        touching = TouchesSpikes(room, boxes[i].PrevX + (boxes[i].X - boxes[i].PrevX) * k / 2f,
                                           boxes[i].PrevY + (boxes[i].Y - boxes[i].PrevY) * k / 2f, boxes[i].Radius);
                }
            }
            if (!pogo.Frame(fox.Action, fox.SelfVelY, touching)) return true;
            return core.Bounce(0, (float)(tuning.BounceHeight * room.Scale)) >= 0;
        }

        bool TouchesSpikes(RoomTransform room, float x, float y, float radius) {
            var p = room.ToHk(new V2(x, y));
            int k = Physics2D.OverlapCircle(new Vector2((float)p.X, (float)p.Y), (float)(radius / room.Scale), filter, found);
            for (int j = 0; j < k; j++) {
                var d = found[j].GetComponentInParent<DamageHero>();
                if (d != null && d.hazardType == Spikes) return true;
            }
            return false;
        }
    }
}
