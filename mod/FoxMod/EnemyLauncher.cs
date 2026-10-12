using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Enemies launched with Melee's knockback (MeleeKnockback): after the hitlag the enemy flies at Melee's launch speed,
    /// decaying as Melee's does (its own gravity and walls still apply), and its state machines wait for the hitstun, as a
    /// Melee victim can't act. Then it carries on. Kinematic or bodiless enemies keep Hollow Knight's recoil.
    sealed class EnemyLauncher {
        sealed class Flying {
            public Rigidbody2D Body; public Collider2D Col; public KnockbackFlight Flight; public Vector2 Kb; public float PerMeleeUnit;
            public bool OwnGravity;   // false: the enemy's own code keeps it up (gravity scale 0); it falls as a Melee victim does
            public readonly List<(PlayMakerFSM fsm, bool manual)> Fsms = new List<(PlayMakerFSM, bool)>();
        }
        readonly Dictionary<GameObject, Flying> flying = new Dictionary<GameObject, Flying>();
        readonly List<GameObject> done = new List<GameObject>();

        public static bool CanLaunch(GameObject go) { var b = go.GetComponent<Rigidbody2D>(); return b != null && !b.isKinematic; }

        /// speed: Melee units per frame; angleDeg: Hollow Knight's direction (0 right, counter-clockwise).
        public void Launch(GameObject go, RoomTransform room, double speed, double angleDeg, int hitstun, bool tumble) {
            var body = go.GetComponent<Rigidbody2D>();
            if (body == null) return;
            if (!flying.TryGetValue(go, out var f)) {
                f = new Flying();
                foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>()) { f.Fsms.Add((fsm, fsm.Fsm.ManualUpdate)); fsm.Fsm.ManualUpdate = true; }
                flying[go] = f;
            }
            foreach (var r in go.GetComponentsInChildren<Recoil>()) r.CancelRecoil();
            f.Body = body;
            f.Col = go.GetComponent<Collider2D>();
            f.OwnGravity = body.gravityScale > 0;
            f.PerMeleeUnit = (float)(60.0 / room.Scale);   // Melee units per frame -> HK units per second
            f.Flight = new KnockbackFlight(speed, angleDeg, hitstun, tumble);
            f.Kb = new Vector2((float)f.Flight.Vx, (float)f.Flight.Vy) * f.PerMeleeUnit;
            body.velocity = f.Kb;   // Melee replaces the victim's momentum with the launch
        }

        /// Each Melee frame: knockback decays; hitstun ends.
        public void Frame() {
            done.Clear();
            foreach (var kv in flying) {
                var f = kv.Value;
                if (kv.Key == null || f.Body == null) { done.Add(kv.Key); continue; }
                bool grounded = Grounded(f);
                bool stunned = f.Flight.Step(grounded);
                var kb = new Vector2((float)f.Flight.Vx, (float)f.Flight.Vy) * f.PerMeleeUnit;
                var v = f.Body.velocity + (kb - f.Kb);   // only the knockback part decays: gravity and collisions stay the body's
                f.Kb = kb;
                if (!f.OwnGravity && !grounded) v.y = Mathf.Max(v.y - Gravity * f.PerMeleeUnit, -MaxFall * f.PerMeleeUnit);
                if (f.Flight.Down) v.x = 0;           // lying down
                f.Body.velocity = v;
                if (!stunned) done.Add(kv.Key);
            }
            foreach (var go in done) Stop(go);
        }

        const float Gravity = 0.1f, MaxFall = 1.7f;   // Melee units per frame (Mario's): for enemies without gravity of their own

        static bool Grounded(Flying f) {
            if (f.Col == null) return false;
            var b = f.Col.bounds;
            return f.Body.velocity.y <= 0.01f && Physics2D.Raycast(new Vector2(b.center.x, b.min.y + 0.05f), Vector2.down, 0.2f, 1 << 8).collider != null;
        }

        /// A new hit (its hitlag freezes the enemy) or the room ends: the enemy's state machines run again.
        public void Stop(GameObject go) {
            if (go == null || !flying.TryGetValue(go, out var f)) { if (go != null) flying.Remove(go); else Prune(); return; }
            foreach (var (fsm, manual) in f.Fsms) if (fsm != null) fsm.Fsm.ManualUpdate = manual;
            flying.Remove(go);
        }

        public void StopAll() { foreach (var go in new List<GameObject>(flying.Keys)) Stop(go); Prune(); }

        void Prune() {   // destroyed enemies (Unity's == null)
            done.Clear();
            foreach (var k in flying.Keys) if (k == null) done.Add(k);
            foreach (var k in done) flying.Remove(k);
        }
    }
}
