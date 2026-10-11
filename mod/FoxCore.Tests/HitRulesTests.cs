using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class HitRulesTests {
        static McHitbox Box(uint group, uint slot = 0, bool fresh = true, float damage = 4) =>
            new McHitbox { Group = group, Slot = slot, Fresh = fresh ? 1u : 0u, Damage = damage, Radius = 3 };

        [Fact]
        public void DamageScalesWithTheNail() {
            Assert.Equal(4, HitRules.HkDamage(4, 5));      // old nail: Melee % as is
            Assert.Equal(17, HitRules.HkDamage(8.5f, 10));  // 17
            Assert.Equal(1, HitRules.HkDamage(0.3f, 5));    // any hit does at least 1
            Assert.Equal(3, HitRules.HkDamage(2.5f, 5));    // halves round away from zero
        }

        [Fact]
        public void DirectionIsTheLaunchAngleMirroredWhenFacingLeft() {
            Assert.Equal(80f, HitRules.Direction(80, 1));
            Assert.Equal(100f, HitRules.Direction(80, -1));
            Assert.Equal(270f, HitRules.Direction(270, -1));   // straight down stays down
            Assert.Equal(180f, HitRules.Direction(0, -1));
            Assert.Equal(40f, HitRules.Direction(361, 1));     // Sakurai angle: forward
            Assert.Equal(140f, HitRules.Direction(361, -1));
        }

        [Fact]
        public void AHitboxHitsATargetOncePerInstance() {
            var t = new HitTracker(); var enemy = new object();
            Assert.Single(t.Frame(new[] { Box(0) }, _ => new[] { enemy }));
            // Melee now remembers a victim (reported), so the hitbox is no longer fresh
            Assert.Empty(t.Frame(new[] { Box(0, fresh: false) }, _ => new[] { enemy }));
            Assert.Empty(t.Frame(new[] { Box(0, fresh: false) }, _ => new[] { enemy }));
        }

        [Fact]
        public void MeleeClearingItsVictimsLetsAMultiHitMoveHitAgain() {
            var t = new HitTracker(); var enemy = new object();
            Assert.Single(t.Frame(new[] { Box(0) }, _ => new[] { enemy }));
            Assert.Single(t.Frame(new[] { Box(0, fresh: true) }, _ => new[] { enemy }));
        }

        [Fact]
        public void AHitboxThatEndsAndReturnsHitsAgain() {
            var t = new HitTracker(); var enemy = new object();
            Assert.Single(t.Frame(new[] { Box(0) }, _ => new[] { enemy }));
            Assert.Empty(t.Frame(new McHitbox[0], _ => new[] { enemy }));
            Assert.Single(t.Frame(new[] { Box(0, fresh: false) }, _ => new[] { enemy }));
        }

        [Fact]
        public void HitboxesOfOneMoveHitATargetOnceTogether() {
            var t = new HitTracker(); var a = new object(); var b = new object();
            var hits = t.Frame(new[] { Box(0, 0), Box(0, 1) }, _ => new[] { a, b });
            Assert.Equal(2, hits.Count);
            Assert.Equal(new[] { a, b }, hits.Select(h => h.Target));
            Assert.All(hits, h => Assert.Equal(0u, h.Box.Slot));
        }

        [Fact]
        public void DifferentGroupsHitSeparately() {
            var t = new HitTracker(); var enemy = new object();
            Assert.Equal(2, t.Frame(new[] { Box(0, 0), Box(1, 1) }, _ => new[] { enemy }).Count);
        }

        [Fact]
        public void ReportsGiveEachGroupItsLargestDamage() {
            var hits = new List<HitEvent> {
                new HitEvent { Box = Box(0, 0, damage: 4) }, new HitEvent { Box = Box(0, 1, damage: 6) },
                new HitEvent { Box = Box(2, 2, damage: 3) },
            };
            Assert.Equal(new[] { (0u, 6f), (2u, 3f) }, HitTracker.Reports(hits).OrderBy(r => r.group));
        }
    }
}
