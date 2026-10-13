using Xunit;

namespace FoxMelee.Tests {
    public class HkControlsTests {
        [Fact]
        public void TheDPadIsHollowKnightsAndTheTaunt() {
            var gc = new McInput { Buttons = (ushort)(GcButtons.DUp | GcButtons.DLeft | GcButtons.DDown | GcButtons.A) };
            var hk = HkControls.FromGameCube(gc);
            Assert.True(hk.Interact); Assert.True(hk.Focus);
            Assert.Equal(GcButtons.A, HkControls.ForMelee(gc).Buttons);   // D-pad up: DPadUp decides (a tap taunts, a hold Dream Nails)
            var right = HkControls.FromGameCube(new McInput { Buttons = GcButtons.DRight });
            Assert.False(right.Interact); Assert.True(right.Inventory);   // no Select on a GameCube pad: D-pad right opens it
            Assert.False(HkControls.FromGameCube(new McInput { Buttons = GcButtons.DUp }).Interact);
            // a modern pad: same D-pad split
            var pad = new PadState { DPadUp = true, DPadRight = true };
            Assert.Equal(GcButtons.DUp, InputMapper.Map(pad).Buttons);
            Assert.True(HkControls.FromPad(pad).Interact);
            Assert.True(HkControls.FromPad(new PadState { DPadDown = true }).Focus);
        }

        [Fact]
        public void FocusDrainsSoulOverTheFocusTimeThenHealsAMask() {
            var f = new FocusRules(frames: 10, cost: 33);
            int drained = 0, heals = 0;
            for (int i = 0; i < 10; i++) {
                var s = f.Tick(held: true, canFocus: true, soul: 99 - drained, health: 3, maxHealth: 5);
                Assert.True(s.Focusing);
                drained += s.Drain; if (s.Heal) heals++;
            }
            Assert.Equal(33, drained);
            Assert.Equal(1, heals);
            // keeps going while held: a second mask
            for (int i = 0; i < 10; i++) { var s = f.Tick(true, true, 99 - drained, 4, 5); drained += s.Drain; if (s.Heal) heals++; }
            Assert.Equal(66, drained); Assert.Equal(2, heals);
        }

        [Fact]
        public void FocusNeedsSoulAMissingMaskAndAStillFox() {
            var f = new FocusRules(10, 33);
            Assert.False(f.Tick(true, true, soul: 32, health: 3, maxHealth: 5).Focusing);
            Assert.False(f.Tick(true, true, 99, 5, 5).Focusing);                // full health
            Assert.False(f.Tick(true, canFocus: false, 99, 3, 5).Focusing);     // airborne or busy
            Assert.False(f.Tick(false, true, 99, 3, 5).Focusing);
        }

        [Fact]
        public void LettingGoEarlyLosesTheSoulSpent() {
            var f = new FocusRules(10, 33);
            int drained = 0;
            for (int i = 0; i < 5; i++) drained += f.Tick(true, true, 99, 3, 5).Drain;
            var s = f.Tick(false, true, 99, 3, 5);
            Assert.False(s.Focusing); Assert.False(s.Heal);
            Assert.InRange(drained, 15, 18);
            // starting again starts over
            int again = 0; bool healed = false;
            for (int i = 0; i < 10; i++) { var t = f.Tick(true, true, 99, 3, 5); again += t.Drain; healed |= t.Heal; }
            Assert.Equal(33, again); Assert.True(healed);
        }
    }

    public class FoxSessionInteractTests {
        static HeroFlags Playing => new HeroFlags { WaitingToTransition = true, AcceptingInput = true };

        [Fact]
        public void AnInteractionKeepsTheKnightWithHollowKnightUntilItHandsControlBack() {
            var s = new FoxSession();
            s.RoomConverted("A", true);
            Assert.Equal(HeroOwner.Fox, s.Owner(Playing));
            s.BeginInteract();
            Assert.True(s.Interacting);
            Assert.Equal(HeroOwner.Hollow, s.Owner(Playing));                // HK gets a chance to react
            var reading = Playing; reading.ControlRelinquished = true; reading.AcceptingInput = false;
            for (int i = 0; i < 100; i++) Assert.Equal(HeroOwner.Hollow, s.Owner(reading));   // a tablet, a bench...
            Assert.Equal(HeroOwner.Fox, s.Owner(Playing));                   // control back: Fox again
            Assert.False(s.Interacting);
            Assert.False(s.TakeRespawns());
        }

        // the waits are time, not frames: at 250 fps Hollow Knight gets as long to react as at 60
        [Theory]
        [InlineData(60)]
        [InlineData(250)]
        public void NothingToInteractWithGivesFoxBackAfterTheSameTime(int fps) {
            var s = new FoxSession();
            s.RoomConverted("A", true);
            s.BeginInteract();
            float t = 0;
            while (s.Owner(Playing, 1f / fps) == HeroOwner.Hollow) { t += 1f / fps; Assert.True(t < 2); }
            Assert.InRange(t, FoxSession.InteractWait - 0.02f, FoxSession.InteractWait + 0.02f);
        }

        // the quick map: held, the Knight stays Hollow Knight's; once the map opens (Hollow Knight takes control) it stays
        // so until the map closes, however long it was held
        [Fact]
        public void AHeldQuickMapKeepsTheKnightUntilTheMapCloses() {
            var s = new FoxSession();
            s.RoomConverted("A", true);
            const float dt = 1f / 250;
            s.BeginInteract();
            for (int i = 0; i < 250; i++) { s.HoldHollow(0.1f); Assert.Equal(HeroOwner.Hollow, s.Owner(Playing, dt)); }   // held a second
            var open = Playing; open.ControlRelinquished = true;
            for (int i = 0; i < 25; i++) { s.HoldHollow(0.1f); s.Owner(open, dt); }   // the map opens while held
            for (int i = 0; i < 500; i++) Assert.Equal(HeroOwner.Hollow, s.Owner(open, dt));   // let go: open until it closes
            Assert.Equal(HeroOwner.Fox, s.Owner(Playing, dt));
        }
    }
}

namespace FoxMelee.Tests {
    public class DPadRightTests {
        const float F = 1f / 60f;

        [Fact]
        public void ATapOpensTheInventoryAHoldShowsTheQuickMap() {
            var d = new DPadRight();
            var s = d.Update(true, F, false);
            Assert.False(s.Inventory || s.QuickMap);                 // undecided while it is short
            s = d.Update(false, F, false);
            Assert.True(s.Inventory);                                 // released quickly: a tap
            Assert.False(d.Update(false, F, false).Inventory);        // one press, not held
            for (int i = 0; i < 30; i++) s = d.Update(true, F, false);
            Assert.True(s.QuickMap && !s.Inventory);                  // held: the quick map, while held
            s = d.Update(false, F, false);
            Assert.False(s.QuickMap || s.Inventory);                  // let go after a hold: nothing opens
        }

        [Fact]
        public void InTheInventoryItMovesRight() {
            var d = new DPadRight();
            var s = d.Update(true, F, true);
            Assert.True(s.Right && !s.Inventory && !s.QuickMap);
            for (int i = 0; i < 30; i++) s = d.Update(true, F, true);
            Assert.True(s.Right && !s.QuickMap);
            Assert.False(d.Update(false, F, true).Inventory);
        }

        [Fact]
        public void TheMapButtonComesAfterTheHandOver() {
            // Hollow Knight opens the quick map on the button's press, and only for a Knight it controls: the hand-over
            // (QuickMap) comes first, the button (MapButton) a little later, so its press reaches Hollow Knight
            var d = new DPadRight();
            DPadRight.State s = default;
            int handOver = -1, button = -1;
            for (int i = 0; i < 60; i++) {
                s = d.Update(true, F, false);
                if (s.QuickMap && handOver < 0) handOver = i;
                if (s.MapButton && button < 0) button = i;
            }
            Assert.True(handOver >= 0 && button > handOver + 2);
            Assert.True(s.QuickMap && s.MapButton);
            s = d.Update(false, F, false);
            Assert.False(s.MapButton || s.QuickMap);
        }

        [Fact]
        public void DPadUpTappedTauntsHeldDreamNails() {
            var d = new DPadUp();
            var s = d.Update(true, F);
            Assert.False(s.Taunt || s.DreamNail);                 // undecided while short
            s = d.Update(false, F);
            Assert.True(s.Taunt);                                  // a tap: Melee's taunt, once
            Assert.False(d.Update(false, F).Taunt);
            int handOver = -1, button = -1;
            for (int i = 0; i < 60; i++) {
                s = d.Update(true, F);
                Assert.False(s.Taunt);
                if (s.DreamNail && handOver < 0) handOver = i;
                if (s.DreamNailButton && button < 0) button = i;
            }
            Assert.True(handOver >= 0 && button > handOver + 2);    // the Knight first, then Hollow Knight's Dream Nail button
            s = d.Update(false, F);
            Assert.False(s.Taunt || s.DreamNail || s.DreamNailButton);   // let go after a hold: no taunt
        }
    }
}
