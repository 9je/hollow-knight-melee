using System;
using System.Collections.Generic;

namespace FoxMelee {
    /// Spec §5.1: how a Melee hit becomes a Hollow Knight hit.
    public static class HitRules {
        const int Sakurai = 361;
        const float SakuraiForward = 40f;   // Hollow Knight only uses the cardinal direction: forward

        const uint Catch = 8, Inert = 11;
        const uint HitsFighters = 4;   // MC_HIT_FIGHTERS

        /// Melee lets this hitbox damage a fighter (not a grab, not inert, not one that skips fighters, has damage).
        public static bool HurtsEnemies(McHitbox b) =>
            b.Damage > 0 && b.Element != Catch && b.Element != Inert && (b.Flags & HitsFighters) != 0;

        /// A hitbox on one of Fox's items (a laser): its group is the item (core mc_read_hitboxes).
        /// Fox's hitboxes hurt Hollow Knight enemies, except the pummel's: it lands through the stand-in (plan 7, GrabBridge).
        public static bool FoxActionHurtsEnemies(uint action) => action != 0xD9;

        public static bool IsItem(McHitbox b) => b.Group >= 0x80000000u;

        /// melee_damage × (nail damage / 5), at least 1 for any damaging hit.
        public static int HkDamage(float meleeDamage, int nailDamage) =>
            meleeDamage <= 0 ? 0 : Math.Max(1, (int)Math.Round(meleeDamage * nailDamage / 5.0, MidpointRounding.AwayFromZero));

        /// The launch angle as Hollow Knight's attack direction (degrees, 0 = right, counter-clockwise). As in Melee
        /// (ftColl_8007A06C) the victim goes away from Fox: mirrored when it is left of him (or level with him).
        public static float Direction(int angle, bool victimLeftOfFox) {
            float a = angle == Sakurai ? SakuraiForward : angle;
            if (victimLeftOfFox) a = 180f - a;
            a %= 360f;
            return a < 0 ? a + 360f : a;
        }
    }

    public struct HitEvent { public McHitbox Box; public object Target; }

    /// Melee's hit-victim rules for targets outside Melee: a hitbox group hits a target once until Melee clears the
    /// group's victims (a fresh hitbox) or the group's hitboxes end.
    public sealed class HitTracker {
        readonly Dictionary<uint, HashSet<object>> victims = new Dictionary<uint, HashSet<object>>();
        readonly HashSet<uint> active = new HashSet<uint>();
        readonly List<uint> stale = new List<uint>();

        /// One Melee frame: the hits to land, in hitbox order.
        public List<HitEvent> Frame(IReadOnlyList<McHitbox> boxes, Func<McHitbox, IEnumerable<object>> overlaps) {
            active.Clear();
            foreach (var b in boxes) active.Add(b.Group);
            stale.Clear();
            foreach (var g in victims.Keys) if (!active.Contains(g)) stale.Add(g);
            foreach (var g in stale) victims.Remove(g);
            foreach (var b in boxes) if (b.Fresh != 0 && victims.TryGetValue(b.Group, out var v)) v.Clear();
            var hits = new List<HitEvent>();
            foreach (var b in boxes) {
                if (!victims.TryGetValue(b.Group, out var v)) victims[b.Group] = v = new HashSet<object>();
                foreach (var target in overlaps(b))
                    if (v.Add(target)) hits.Add(new HitEvent { Box = b, Target = target });
            }
            return hits;
        }

        /// What to tell Melee: each group that landed, with its largest damage (Melee's hitlag uses the largest).
        public static IEnumerable<(uint group, float damage)> Reports(IEnumerable<HitEvent> hits) {
            var best = new Dictionary<uint, float>();
            foreach (var h in hits)
                if (!best.TryGetValue(h.Box.Group, out var d) || h.Box.Damage > d) best[h.Box.Group] = h.Box.Damage;
            foreach (var kv in best) yield return (kv.Key, kv.Value);
        }
    }
}

namespace FoxMelee {
    /// Melee's hitlag for victims outside Melee: a hit target stays frozen for the frames Melee gave Fox (attacker and
    /// victim share the formula), counted in Melee frames; a new hit restarts the count, as Melee overwrites it.
    public sealed class HitlagTimers {
        readonly Dictionary<object, int> left = new Dictionary<object, int>();
        readonly List<object> released = new List<object>();

        /// True when the target was not frozen yet (the caller freezes it now).
        public bool Freeze(object target, int frames) {
            if (frames <= 0) return false;
            bool fresh = !left.ContainsKey(target);
            left[target] = frames;
            return fresh;
        }

        public bool IsFrozen(object target) => left.ContainsKey(target);

        /// After each Melee frame: the targets whose hitlag just ended.
        public List<object> Tick() {
            released.Clear();
            foreach (var k in new List<object>(left.Keys))
                if (--left[k] <= 0) { left.Remove(k); released.Add(k); }
            return released;
        }

        public IEnumerable<object> ReleaseAll() {
            var all = new List<object>(left.Keys);
            left.Clear();
            return all;
        }
    }
}
