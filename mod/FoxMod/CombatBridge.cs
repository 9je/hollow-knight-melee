using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FoxMelee {
    /// Spec §5.1: Fox's Melee hitboxes hit what Hollow Knight's nail hits, with Melee's hit-victim rules and Melee's
    /// hitlag both ways. Damage goes through HitTaker (IHitResponders, found on the nail's layers); a "Nail Attack"
    /// trigger at each hitbox lets tag-driven objects (breakable walls, vines, levers) react as they do to the nail.
    sealed class CombatBridge {
        const int AttackLayer = 17;            // the nail slash's layer
        const int MaxSweepSteps = 8;
        const float MaxSweep = 30f;            // Melee units; a longer jump means the hitbox was just placed
        static readonly FieldInfo Evasion =    // Hollow Knight ignores hits for 0.2 s after one; Melee's multi-hits must not be
            typeof(HealthManager).GetField("evasionByHitRemaining", BindingFlags.Instance | BindingFlags.NonPublic);

        readonly int mask;                     // what the nail can touch (DamageEnemies' exclusions removed too)
        readonly McHitbox[] boxes = new McHitbox[4];
        readonly List<McHitbox> hurting = new List<McHitbox>(4);
        readonly HitTracker tracker = new HitTracker();
        readonly Collider2D[] found = new Collider2D[64];
        readonly List<object> targets = new List<object>();
        readonly List<HitEvent> real = new List<HitEvent>();               // hits on live enemies: Melee hears of these
        /// Enemies Fox's own attacks (not lasers) landed on this frame (their HealthManager objects): EnemyBridge.Guard.
        public readonly List<GameObject> Struck = new List<GameObject>();
        readonly List<(GameObject go, int dir, int frames)> landed = new List<(GameObject, int, int)>();   // frames -1: Fox's hitlag
        MeleeCore core;
        readonly HitlagTimers hitlag = new HitlagTimers();
        readonly Dictionary<object, EnemyFreeze> frozen = new Dictionary<object, EnemyFreeze>();
        readonly GameObject strike;            // each hit's Source: Hollow Knight draws nail effects between it and the target
        readonly CircleCollider2D[] nailTriggers = new CircleCollider2D[4];
        readonly HutongGames.PlayMaker.FsmFloat[] nailDirections = new HutongGames.PlayMaker.FsmFloat[4];
        readonly PlayMakerFSM[] nailFsms = new PlayMakerFSM[4];
        RoomTransform room;
        float foxX;

        public CombatBridge() {
            mask = Physics2D.GetLayerCollisionMask(AttackLayer) & ~((1 << 9) | (1 << 20) | (1 << 26) | (1 << 31));
            strike = new GameObject("FoxStrike");
            UnityEngine.Object.DontDestroyOnLoad(strike);
            for (int i = 0; i < 4; i++) {
                var go = new GameObject("FoxNailAttack" + i) { tag = "Nail Attack", layer = AttackLayer };
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);
                var body = go.AddComponent<Rigidbody2D>();
                body.isKinematic = true;
                body.useFullKinematicContacts = true;   // a kinematic body otherwise ignores static colliders (geo rocks)
                nailTriggers[i] = go.AddComponent<CircleCollider2D>();
                nailTriggers[i].isTrigger = true;
                nailDirections[i] = DamagesEnemy(go, out nailFsms[i]);
            }
            if (Evasion == null) FoxMod.Instance.LogWarn("HealthManager.evasionByHitRemaining not found: rapid Melee hits may be ignored");
        }

        /// Checks one Melee frame's hitboxes at the core's hit pause (mc_step_to_hits), where a reported hit lands in
        /// this frame as a Melee victim's would; false if the core failed.
        public bool Frame(MeleeCore core, RoomTransform room) {
            int n = core.ReadHitboxes(boxes);
            Struck.Clear();
            if (n < 0) return false;
            hurting.Clear();
            for (int i = 0; i < n; i++) if (HitRules.HurtsEnemies(boxes[i])) hurting.Add(boxes[i]);
            this.room = room;
            if (hurting.Count > 0) {
                if (!core.ReadFighter(0, out var fox)) return false;
                foxX = Hk(fox.X, fox.Y).x;
            }
            PlaceNailTriggers();
            if (hurting.Count == 0) { tracker.Frame(hurting, null); return true; }
            var hits = tracker.Frame(hurting, Overlaps);
            if (hits.Count == 0) return true;
            int nail = PlayerData.instance.GetInt("nailDamage");
            this.core = core;
            real.Clear();
            foreach (var h in hits) Land(h, nail);
            foreach (var (group, damage) in HitTracker.Reports(real)) core.ReportHit(group, damage);   // 1 = it just ended: harmless
            return true;
        }

        /// After the frame finished: victims' hitlag counts down, and this frame's victims freeze for the hitlag Melee
        /// gave Fox (the attacker's and victim's hitlag share Melee's formula).
        public bool AfterFrame(MeleeCore core) {
            foreach (var k in hitlag.Tick()) Thaw(k);
            if (landed.Count == 0) return true;
            if (!core.ReadFighter(0, out var fox)) return false;
            foreach (var (go, dir, frames) in landed) {
                if (go == null) continue;
                if (hitlag.Freeze(go, frames >= 0 ? frames : (int)fox.Hitlag)) frozen[go] = new EnemyFreeze(go, dir);
                else if (frozen.TryGetValue(go, out var f)) f.Rehit(dir);
            }
            landed.Clear();
            return true;
        }

        /// Room changes, Hollow Knight taking over, failures: nothing stays frozen, no nail triggers linger.
        public void ReleaseAll() {
            foreach (var k in hitlag.ReleaseAll()) Thaw(k);
            landed.Clear();
            foreach (var t in nailTriggers) t.gameObject.SetActive(false);
        }

        void Thaw(object k) {
            if (frozen.TryGetValue(k, out var f)) { f.Release(); frozen.Remove(k); }
        }

        // Tag-driven objects (lifeblood cocoons, stalactites, geo rocks) read the hit's direction from the nail slash's
        // "damages_enemy" state machine; geo rocks also need its damageDealt above 0 (ReceivedDamage ignores hitters
        // without) and its attackType 0, the nail (their "Check If Nail" state). This one only carries those values: it has no states and damages nothing.
        static HutongGames.PlayMaker.FsmFloat DamagesEnemy(GameObject go, out PlayMakerFSM fsm) {
            fsm = null;
            try {
                fsm = go.AddComponent<PlayMakerFSM>();
                fsm.Reset();   // the object is inactive, so Awake (which would create the Fsm) has not run yet; InitFsm keeps ours
                fsm.FsmName = "damages_enemy";
                var dir = new HutongGames.PlayMaker.FsmFloat("direction");
                fsm.FsmVariables.FloatVariables = new[] { dir };
                fsm.FsmVariables.IntVariables = new[] {
                    new HutongGames.PlayMaker.FsmInt("attackType") { Value = (int)AttackTypes.Nail },
                    new HutongGames.PlayMaker.FsmInt("damageDealt") { Value = Math.Max(1, PlayerData.instance.GetInt("nailDamage")) },
                };
                return dir;
            } catch (Exception e) {
                FoxMod.Instance.LogWarn("no damages_enemy stand-in (" + e.Message + "): cocoons and rocks may not react to Fox");
                return null;
            }
        }

        void PlaceNailTriggers() {
            for (int i = 0; i < 4; i++) {
                var t = nailTriggers[i];
                if (i >= hurting.Count) { t.gameObject.SetActive(false); continue; }
                t.transform.position = Hk(hurting[i].X, hurting[i].Y);
                t.radius = (float)(hurting[i].Radius / room.Scale);
                if (nailDirections[i] != null) nailDirections[i].Value = HitRules.Direction(hurting[i].Angle, t.transform.position.x <= foxX);
                t.gameObject.SetActive(true);
            }
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
                int k = Physics2D.OverlapCircleNonAlloc(p, r, found, mask);
                for (int j = 0; j < k; j++) {
                    var responder = Responder(found[j].transform);
                    if (responder != null) { if (!targets.Contains(responder)) targets.Add(responder); continue; }
                    // no hit responder: an object run by its state machine (geo rocks) hears "TAKE DAMAGE" from the nail
                    var go = found[j].gameObject;
                    if (go.GetComponent<PlayMakerFSM>() != null && go.GetComponentInParent<HealthManager>() == null && !targets.Contains(go)) targets.Add(go);
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
            if (h.Target is GameObject fsmTarget) { TakeDamage(h, fsmTarget); return; }
            var target = (Component)h.Target;
            if (target == null) return;           // destroyed by an earlier hit this frame
            strike.transform.position = Hk(h.Box.X, h.Box.Y);
            var hm = target.GetComponentInParent<HealthManager>();
            bool enemy = hm != null && !hm.GetIsDead();   // grass, corpses and scenery: hit, but no Melee hitlag
            if (enemy && Evasion != null) Evasion.SetValue(hm, 0f);
            // away from the attacker (ftColl_8007A06C): Fox, or the laser itself
            float fromX = HitRules.IsItem(h.Box) ? Hk(h.Box.X, h.Box.Y).x : foxX;
            float direction = HitRules.Direction(h.Box.Angle, target.transform.position.x <= fromX);
            HitTaker.Hit(target.gameObject, new HitInstance {
                Source = strike,
                AttackType = AttackTypes.Nail,    // Soul and nail effects, as the nail gives
                DamageDealt = HitRules.HkDamage(h.Box.Damage, nail),
                Direction = direction,
                Multiplier = 1f,
                MagnitudeMultiplier = 1f,
            });
            if (!enemy) return;
            real.Add(h);
            if (!HitRules.IsItem(h.Box) && !Struck.Contains(hm.gameObject)) Struck.Add(hm.gameObject);
            int dir = DirectionUtils.GetCardinalDirection(direction);
            // a laser gives Fox no hitlag, so the victim's comes from Melee's formula for the laser's damage
            int damage = h.Box.Damage > 0 && (int)h.Box.Damage == 0 ? 1 : (int)h.Box.Damage;
            int frames = HitRules.IsItem(h.Box) ? core.HitlagFrames(damage) : -1;
            int at = landed.FindIndex(l => l.go == hm.gameObject);
            if (at < 0) landed.Add((hm.gameObject, dir, frames)); else landed[at] = (hm.gameObject, dir, frames);
        }

        // What the nail's own damages_enemy state machine does to objects it touches: send them "TAKE DAMAGE". The
        // sender is what they read the hit from (geo rocks: GetEventSender, then its attackType and direction), so it
        // is sent from Fox's stand-in with this hit's direction.
        void TakeDamage(HitEvent h, GameObject target) {
            if (target == null) return;
            var fsm = nailFsms[0];
            if (fsm == null || fsm.Fsm == null) return;
            float fromX = HitRules.IsItem(h.Box) ? Hk(h.Box.X, h.Box.Y).x : foxX;
            float direction = HitRules.Direction(h.Box.Angle, target.transform.position.x <= fromX);
            nailDirections[0].Value = direction;
            var to = new HutongGames.PlayMaker.FsmEventTarget {
                target = HutongGames.PlayMaker.FsmEventTarget.EventTarget.GameObject,
                gameObject = new HutongGames.PlayMaker.FsmOwnerDefault {
                    OwnerOption = HutongGames.PlayMaker.OwnerDefaultOption.SpecifyGameObject, GameObject = target },
                excludeSelf = false, sendToChildren = false,
            };
            // PlayMaker takes the sender from the state machine executing now, not the one sending: make it Fox's
            HutongGames.PlayMaker.FsmExecutionStack.PushFsm(fsm.Fsm);
            try { fsm.Fsm.Event(to, "TAKE DAMAGE"); }
            finally { HutongGames.PlayMaker.FsmExecutionStack.PopFsm(); }
        }

        Vector2 Hk(float x, float y) { var v = room.ToHk(new V2(x, y)); return new Vector2((float)v.X, (float)v.Y); }
    }
}
