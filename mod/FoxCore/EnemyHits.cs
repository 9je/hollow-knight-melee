using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FoxMelee {
    /// A hit from outside Melee (core McHit): Melee resolves it (mc_apply_hit).
    [StructLayout(LayoutKind.Sequential)]
    public struct McHit {
        public float Damage; public int Angle, Growth, SetKb, BaseKb; public uint Element;
        public float ShieldDamage; public uint Unblockable; public float FromX, FromY;
        public const int Landed = 0, Shielded = 1, Intangible = 2, Later = 3;
    }

    /// How a Hollow Knight attack becomes a Melee hit (spec §5.2: per enemy attack in the tuning file; these are the
    /// defaults every attack uses until it has its own entry). Damage in masks scales percent and shield damage.
    public sealed class EnemyHitTuning {
        public double PercentPerMask = 10;
        public int Angle = 361;                     // Sakurai angle
        public int BaseKnockback = 30;
        public int KnockbackGrowth = 90;
        public double ShieldDamagePerMask = 8;      // on top of the hit's damage: Hollow Knight hits are heavy on shields
        public int UnblockableFromMasks = 3;        // attacks this big go through the shield
        public int CooldownFrames = 30;             // per enemy, replacing the Knight's long invulnerability
        public int GraceFrames = 20;                // after Fox's attack lands on an enemy, its body can't hurt him (0: off)
        public double PercentAtFullHealth = 10;     // Fox's percent follows his masks: this at full health,
        public double PercentAtLastMask = 100;      // this on his last mask (PercentFor)
    }

    /// Enemies hitting Fox: each enemy (source) can hit again only after its cooldown, so one enemy's hit does not
    /// make Fox invulnerable to the others (spec §5.2).
    public sealed class EnemyHits {
        readonly int cooldown;
        readonly Dictionary<object, int> left = new Dictionary<object, int>();
        readonly List<object> done = new List<object>();

        public EnemyHits(int cooldownFrames) { cooldown = cooldownFrames; }

        /// True when `source` may hit now (and starts its cooldown).
        public bool Accept(object source) {
            if (left.ContainsKey(source)) return false;
            left[source] = cooldown;
            return true;
        }

        /// Fox's attack landed on `source`: it can't hit him for `frames` (as the nail's knockback keeps the Knight
        /// clear, so attacking through an enemy is not punished by touching it). Never shortens a running cooldown.
        public void Guard(object source, int frames) {
            if (frames <= 0) return;
            if (!left.TryGetValue(source, out int now) || now < frames) left[source] = frames;
        }

        /// After each Melee frame.
        public void Tick() {
            done.Clear();
            foreach (var k in new List<object>(left.Keys)) if (--left[k] <= 0) done.Add(k);
            foreach (var k in done) left.Remove(k);
        }

        public void Clear() => left.Clear();

        /// Fox's percent for his health: PercentAtFullHealth with every mask, PercentAtLastMask on the last one, in
        /// steps between. Losing masks raises it, healing lowers it; it is held there between hits.
        public static double PercentFor(EnemyHitTuning t, int health, int maxHealth) {
            if (maxHealth <= 1) return t.PercentAtFullHealth;
            double lost = System.Math.Max(0, System.Math.Min(maxHealth - 1, maxHealth - health));
            return t.PercentAtFullHealth + (t.PercentAtLastMask - t.PercentAtFullHealth) * lost / (maxHealth - 1);
        }

        public static McHit ToMelee(EnemyHitTuning t, int masks, float fromX, float fromY, int unblockableFromMasks = int.MaxValue) => new McHit {
            Damage = (float)(t.PercentPerMask * masks), Angle = t.Angle, Growth = t.KnockbackGrowth, BaseKb = t.BaseKnockback,
            ShieldDamage = (float)(t.ShieldDamagePerMask * masks), Unblockable = masks >= unblockableFromMasks ? 1u : 0u,
            FromX = fromX, FromY = fromY,
        };
    }
}
