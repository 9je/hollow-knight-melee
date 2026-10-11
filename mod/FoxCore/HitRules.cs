using System;
using System.Collections.Generic;

namespace FoxMelee {
    /// Spec §5.1: how a Melee hit becomes a Hollow Knight hit.
    public static class HitRules {
        const int Sakurai = 361;
        const float SakuraiForward = 40f;   // Hollow Knight only uses the cardinal direction: forward

        /// melee_damage × (nail damage / 5), at least 1.
        public static int HkDamage(float meleeDamage, int nailDamage) =>
            Math.Max(1, (int)Math.Round(meleeDamage * nailDamage / 5.0, MidpointRounding.AwayFromZero));

        /// The launch angle as Hollow Knight's attack direction (degrees, 0 = right, counter-clockwise),
        /// mirrored when Fox faces left.
        public static float Direction(int angle, float facing) {
            float a = angle == Sakurai ? SakuraiForward : angle;
            if (facing < 0) a = 180f - a;
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
