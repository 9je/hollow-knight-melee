using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FoxMelee {
    /// Spec §5.1: Fox's Melee hitboxes hit whatever Hollow Knight's nail can hit (anything with an IHitResponder,
    /// found the way HitTaker looks: the collider's object and its two parents), with Melee's hit-victim rules and
    /// Melee's own hitlag for Fox.
    sealed class CombatBridge {
        const int HeroLayer = 9;
        const int MaxSweepSteps = 8;
        const float MaxSweep = 30f;            // Melee units; a longer jump means the hitbox was just placed
        static readonly FieldInfo Evasion =    // Hollow Knight ignores hits for 0.2 s after one; Melee's multi-hits must not be
            typeof(HealthManager).GetField("evasionByHitRemaining", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly McHitbox[] boxes = new McHitbox[4];
        readonly HitTracker tracker = new HitTracker();
        readonly Collider2D[] found = new Collider2D[32];
        readonly List<object> targets = new List<object>();
        readonly List<GameObject> landed = new List<GameObject>();   // hit this frame: frozen for the hitlag after it
        readonly HitlagTimers hitlag = new HitlagTimers();
        readonly Dictionary<object, EnemyFreeze> frozen = new Dictionary<object, EnemyFreeze>();
        readonly GameObject strike;            // each hit's Source: Hollow Knight draws nail effects between it and the target
        RoomTransform room;
        float facing;

        public CombatBridge() {
            strike = new GameObject("FoxStrike");
            UnityEngine.Object.DontDestroyOnLoad(strike);
            if (Evasion == null) FoxMod.Instance.LogWarn("HealthManager.evasionByHitRemaining not found: rapid Melee hits may be ignored");
        }

        /// Checks one Melee frame's hitboxes at the core's hit pause (mc_step_to_hits), where a reported hit lands in
        /// this frame as a Melee victim's would; false if the core failed.
        public bool Frame(MeleeCore core, RoomTransform room) {
            int n = core.ReadHitboxes(boxes);
            if (n < 0) return false;
            if (n == 0) { tracker.Frame(Array.Empty<McHitbox>(), null); return true; }
            if (!core.ReadFighter(0, out var fox)) return false;
            this.room = room;
            facing = fox.Facing;
            var hits = tracker.Frame(new ArraySegment<McHitbox>(boxes, 0, n), Overlaps);
            if (hits.Count == 0) return true;
            int nail = PlayerData.instance.GetInt("nailDamage");
            foreach (var h in hits) Land(h, nail);
            foreach (var (group, damage) in HitTracker.Reports(hits)) core.ReportHit(group, damage);   // 1 = it just ended: harmless
            return true;
        }

        /// After the frame finished: victims' hitlag counts down, and this frame's victims freeze for the hitlag Melee
        /// gave Fox (the attacker's and victim's hitlag share Melee's formula).
        public bool AfterFrame(MeleeCore core) {
            foreach (var k in hitlag.Tick()) Thaw(k);
            if (landed.Count == 0) return true;
            if (!core.ReadFighter(0, out var fox)) return false;
            foreach (var go in landed)
                if (go != null && hitlag.Freeze(go, (int)fox.Hitlag)) frozen[go] = new EnemyFreeze(go);
            landed.Clear();
            return true;
        }

        /// Room changes, Hollow Knight taking over, failures: nothing stays frozen.
        public void ReleaseAll() {
            foreach (var k in hitlag.ReleaseAll()) Thaw(k);
            landed.Clear();
        }

        void Thaw(object k) {
            if (frozen.TryGetValue(k, out var f)) { f.Release(); frozen.Remove(k); }
        }

        IEnumerable<object> Overlaps(McHitbox b) {
            targets.Clear();
            var cur = Hk(b.X, b.Y);
            var prev = Hk(b.PrevX, b.PrevY);
            float dx = b.X - b.PrevX, dy = b.Y - b.PrevY, dist = Mathf.Sqrt(dx * dx + dy * dy);
            float r = (float)(b.Radius / room.Scale);
            int steps = dist > MaxSweep || b.Radius <= 0 ? 0 : Mathf.Min(MaxSweepSteps, Mathf.CeilToInt(dist / b.Radius));
            for (int i = 0; i <= steps; i++) {
                var p = steps == 0 ? cur : Vector2.Lerp(prev, cur, (float)i / steps);
                int k = Physics2D.OverlapCircleNonAlloc(p, r, found);
                for (int j = 0; j < k; j++) {
                    var c = found[j];
                    if (c.gameObject.layer == HeroLayer) continue;
                    var responder = Responder(c.transform);
                    if (responder != null && !targets.Contains(responder)) targets.Add(responder);
                }
            }
            return targets;
        }

        static Component Responder(Transform t) {
            for (int i = 0; i < 3 && t != null; i++, t = t.parent)
                foreach (var m in t.GetComponents<MonoBehaviour>()) if (m is IHitResponder) return m;
            return null;
        }

        void Land(HitEvent h, int nail) {
            var target = (Component)h.Target;
            if (target == null) return;           // destroyed by an earlier hit this frame
            strike.transform.position = Hk(h.Box.X, h.Box.Y);
            var hm = target.GetComponentInParent<HealthManager>();
            if (hm != null && Evasion != null) Evasion.SetValue(hm, 0f);
            var victim = hm != null ? hm.gameObject : target.gameObject;
            if (!landed.Contains(victim)) landed.Add(victim);
            HitTaker.Hit(target.gameObject, new HitInstance {
                Source = strike,
                AttackType = AttackTypes.Nail,    // Soul and nail effects, as the nail gives
                DamageDealt = HitRules.HkDamage(h.Box.Damage, nail),
                Direction = HitRules.Direction(h.Box.Angle, facing),
                Multiplier = 1f,
                MagnitudeMultiplier = 1f,
            });
        }

        Vector2 Hk(float x, float y) { var v = room.ToHk(new V2(x, y)); return new Vector2((float)v.X, (float)v.Y); }
    }
}
