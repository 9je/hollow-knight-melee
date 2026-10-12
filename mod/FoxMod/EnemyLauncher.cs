using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Enemies launched with Melee's knockback (MeleeKnockback): after the hitlag the enemy flies at Melee's launch speed,
    /// decaying as Melee's does (its own gravity and walls still apply), and its state machines wait for the hitstun, as a
    /// Melee victim can't act. Then it carries on. Kinematic or bodiless enemies keep Hollow Knight's recoil.
    sealed class EnemyLauncher {
        sealed class Flying {
            public Rigidbody2D Body; public KnockbackFlight Flight; public Vector2 Kb; public float PerMeleeUnit;
            public readonly List<(PlayMakerFSM fsm, bool manual)> Fsms = new List<(PlayMakerFSM, bool)>();
        }
        readonly Dictionary<GameObject, Flying> flying = new Dictionary<GameObject, Flying>();
        readonly List<GameObject> done = new List<GameObject>();

        public static bool CanLaunch(GameObject go) { var b = go.GetComponent<Rigidbody2D>(); return b != null && !b.isKinematic; }

        /// speed: Melee units per frame; angleDeg: Hollow Knight's direction (0 right, counter-clockwise).
        public void Launch(GameObject go, RoomTransform room, double speed, double angleDeg, int hitstun) {
            var body = go.GetComponent<Rigidbody2D>();
            if (body == null) return;
            if (!flying.TryGetValue(go, out var f)) {
                f = new Flying();
                foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>()) { f.Fsms.Add((fsm, fsm.Fsm.ManualUpdate)); fsm.Fsm.ManualUpdate = true; }
                flying[go] = f;
            }
            foreach (var r in go.GetComponentsInChildren<Recoil>()) r.CancelRecoil();
            f.Body = body;
            f.PerMeleeUnit = (float)(60.0 / room.Scale);   // Melee units per frame -> HK units per second
            f.Flight = new KnockbackFlight(speed, angleDeg, hitstun);
            f.Kb = new Vector2((float)f.Flight.Vx, (float)f.Flight.Vy) * f.PerMeleeUnit;
            body.velocity = f.Kb;   // Melee replaces the victim's momentum with the launch
        }

        /// Each Melee frame: knockback decays; hitstun ends.
        public void Frame() {
            done.Clear();
            foreach (var kv in flying) {
                var f = kv.Value;
                if (kv.Key == null || f.Body == null) { done.Add(kv.Key); continue; }
                bool stunned = f.Flight.Step();
                var kb = new Vector2((float)f.Flight.Vx, (float)f.Flight.Vy) * f.PerMeleeUnit;
                f.Body.velocity += kb - f.Kb;   // only the knockback part decays: gravity and collisions stay the body's
                f.Kb = kb;
                if (!stunned) done.Add(kv.Key);
            }
            foreach (var go in done) Stop(go);
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
