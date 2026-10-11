using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Holds a Hollow Knight object still for Melee's victim hitlag: its body (colliders stay on, so it can still be
    /// hit), its PlayMaker state machines, sprite and Animator animation, and Hollow Knight's knockback (Recoil runs on
    /// its own timer, so the knockback plays after the hitlag, as Melee launches after it).
    sealed class EnemyFreeze {
        readonly Rigidbody2D body;
        readonly RigidbodyConstraints2D constraints;
        readonly Vector2 velocity;
        readonly float angular;
        readonly List<(PlayMakerFSM fsm, bool manual)> fsms = new List<(PlayMakerFSM, bool)>();
        readonly List<(tk2dSpriteAnimator anim, bool paused)> sprites = new List<(tk2dSpriteAnimator, bool)>();
        readonly List<(Animator anim, float speed)> animators = new List<(Animator, float)>();
        readonly List<(Recoil recoil, bool enabled)> recoils = new List<(Recoil, bool)>();

        public EnemyFreeze(GameObject go) {
            body = go.GetComponent<Rigidbody2D>();
            if (body != null) {
                constraints = body.constraints; velocity = body.velocity; angular = body.angularVelocity;
                body.constraints = RigidbodyConstraints2D.FreezeAll;
            }
            foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>()) { fsms.Add((f, f.Fsm.ManualUpdate)); f.Fsm.ManualUpdate = true; }
            foreach (var a in go.GetComponentsInChildren<tk2dSpriteAnimator>()) { sprites.Add((a, a.Paused)); a.Paused = true; }
            foreach (var a in go.GetComponentsInChildren<Animator>()) { animators.Add((a, a.speed)); a.speed = 0f; }
            foreach (var r in go.GetComponentsInChildren<Recoil>()) { recoils.Add((r, r.enabled)); r.enabled = false; }
        }

        /// Puts everything back; parts destroyed meanwhile (a killed enemy) are skipped.
        public void Release() {
            if (body != null) { body.constraints = constraints; body.velocity = velocity; body.angularVelocity = angular; }
            foreach (var (f, manual) in fsms) if (f != null) f.Fsm.ManualUpdate = manual;
            foreach (var (a, paused) in sprites) if (a != null) a.Paused = paused;
            foreach (var (a, speed) in animators) if (a != null) a.speed = speed;
            foreach (var (r, enabled) in recoils) if (r != null) r.enabled = enabled;
        }
    }
}
