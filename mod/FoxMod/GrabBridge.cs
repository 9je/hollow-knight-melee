using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FoxMelee {
    /// Plan 7 (spec §5 Grab): the stand-in fighter (port 1) follows the nearest small enemy, so Melee's push and Fox's
    /// grab act on it; what Melee does to it (held, pummelled, thrown) is done to the enemy. No rule of the mod's own:
    /// Melee decides every grab, pummel, throw and push.
    sealed class GrabBridge {
        public const int Port = 1;
        const int EnemyLayer = 11;
        static readonly FieldInfo Evasion =
            typeof(HealthManager).GetField("evasionByHitRemaining", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly GrabTuning tuning;
        readonly EnemyBridge enemies;
        readonly GrabMachine machine = new GrabMachine();
        readonly Collider2D[] found = new Collider2D[32];
        readonly List<GrabCandidate> candidates = new List<GrabCandidate>();
        readonly Dictionary<int, HealthManager> byId = new Dictionary<int, HealthManager>();
        readonly Dictionary<HealthManager, int> maxHp = new Dictionary<HealthManager, int>();
        readonly GameObject strike;   // the pummel's and throw's Source, as CombatBridge's
        HealthManager held, target;
        EnemyFreeze freeze;
        float feetOffset;             // HK units from the held enemy's transform down to its feet
        float foxX;

        public GrabBridge(GrabTuning tuning, EnemyBridge enemies) {
            this.tuning = tuning; this.enemies = enemies;
            strike = new GameObject("FoxGrab");
            Object.DontDestroyOnLoad(strike);
        }

        /// Before each Melee frame: follow, wake or sleep the stand-in. foxFeet: the Knight's feet, HK units.
        public bool Before(MeleeCore core, RoomTransform room, Vector2 foxFeet) {
            foxX = foxFeet.x;
            Gather(foxFeet);
            int id = GrabTargets.Pick(candidates, foxFeet.x, foxFeet.y, machine.TargetId, tuning);
            var cmd = machine.Before(id);
            if (cmd == GrabCommand.Sleep) { target = null; return core.StandInSleep(); }
            if (cmd != GrabCommand.Wake && cmd != GrabCommand.Place) return true;
            var c = candidates.Find(e => e.Id == id);
            byId.TryGetValue(id, out target);
            var at = room.ToMelee(new V2(c.X, c.Y));
            float facing = c.X <= foxFeet.x ? 1f : -1f;   // towards Fox
            bool ok = cmd == GrabCommand.Wake ? core.StandInWake((float)at.X, (float)at.Y, facing) : core.PlaceFighter(Port, (float)at.X, (float)at.Y);
            return ok && core.SetPercent(Port, (float)GrabTargets.Percent(c, tuning));
        }

        /// After each Melee frame: mirror Melee's grab onto the enemy.
        public bool After(MeleeCore core, RoomTransform room) {
            if (machine.State == GrabMachine.Phase.Asleep) return true;
            if (!core.ReadFighter(0, out var fox) || !core.ReadFighter(Port, out var s)) return false;
            int h = core.StandInHeld();
            if (h < 0) return false;
            var hm = held ?? target;
            bool gone = hm == null || hm.GetIsDead();
            var ev = machine.After(new GrabSeen { Held = h == 1, TargetGone = gone, FoxAction = fox.Action, Percent = s.Percent, KbX = s.KbVelX, KbY = s.KbVelY }, out float damage);
            switch (ev) {
                case GrabEvent.Grabbed:
                    if (hm == null) break;
                    held = hm;
                    feetOffset = held.transform.position.y - Feet(held);
                    freeze = new EnemyFreeze(held.gameObject, AwayFromFox(held));
                    break;
                case GrabEvent.Pummel: Damage(held, damage, fox.Facing > 0 ? 0f : 180f); break;
                case GrabEvent.Thrown: {
                    var thrown = held;
                    float dir = Mathf.Repeat(Mathf.Atan2(s.KbVelY, s.KbVelX) * Mathf.Rad2Deg, 360f);
                    freeze?.Rehit(DirectionUtils.GetCardinalDirection(dir));
                    Unfreeze();
                    if (thrown == null) break;
                    Damage(thrown, damage, dir);
                    var body = thrown.GetComponent<Rigidbody2D>();
                    if (body != null && !thrown.GetIsDead())   // Melee units per frame -> HK units per second
                        body.velocity = new Vector2(s.KbVelX, s.KbVelY) * (float)(60.0 / room.Scale * tuning.LaunchScale);
                    break;
                }
                case GrabEvent.Escaped: Unfreeze(); break;
            }
            if (held != null && machine.State == GrabMachine.Phase.Held) {   // in Fox's hand: where Melee holds the stand-in
                var p = room.ToHk(new V2(s.X, s.Y));
                held.transform.position = new Vector3((float)p.X, (float)p.Y + feetOffset, held.transform.position.z);
                enemies?.Guard(held.gameObject);   // its body doesn't hurt Fox while he holds it
            }
            return true;
        }

        /// Hollow Knight takes the Knight, a room changes: unfreeze, and the stand-in sleeps next frame.
        public void Release() { Unfreeze(); target = null; maxHp.Clear(); machine.Reset(); }

        void Unfreeze() { freeze?.Release(); freeze = null; held = null; }

        int AwayFromFox(HealthManager hm) => hm.transform.position.x >= foxX ? 0 : 2;   // Hollow Knight cardinal: 0 right, 2 left

        void Damage(HealthManager hm, float percent, float direction) {
            if (hm == null || hm.GetIsDead() || percent <= 0) return;
            if (Evasion != null) Evasion.SetValue(hm, 0f);
            strike.transform.position = new Vector3(foxX, hm.transform.position.y, 0f);
            HitTaker.Hit(hm.gameObject, new HitInstance {
                Source = strike, AttackType = AttackTypes.Nail,
                DamageDealt = HitRules.HkDamage(percent, PlayerData.instance.GetInt("nailDamage")),
                Direction = direction, Multiplier = 1f, MagnitudeMultiplier = 1f,
            });
        }

        void Gather(Vector2 fox) {
            candidates.Clear(); byId.Clear();
            var size = new Vector2((float)tuning.WakeRangeX * 2 + 2, (float)tuning.WakeRangeY * 2 + 2);
            int n = Physics2D.OverlapBoxNonAlloc(fox, size, 0f, found, 1 << EnemyLayer);
            for (int i = 0; i < n; i++) {
                var hm = found[i].GetComponentInParent<HealthManager>();
                if (hm == null || hm.GetIsDead() || byId.ContainsKey(hm.GetInstanceID())) continue;
                var col = hm.GetComponent<Collider2D>();
                if (col == null) col = found[i];
                if (!maxHp.TryGetValue(hm, out int max)) maxHp[hm] = max = Mathf.Max(1, hm.hp);
                var b = col.bounds;
                byId[hm.GetInstanceID()] = hm;
                candidates.Add(new GrabCandidate { Id = hm.GetInstanceID(), X = b.center.x, Y = b.min.y, Width = b.size.x, Height = b.size.y, Hp = hm.hp, MaxHp = max });
            }
        }

        static float Feet(HealthManager hm) { var c = hm.GetComponent<Collider2D>(); return c != null ? c.bounds.min.y : hm.transform.position.y; }
    }
}
