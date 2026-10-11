using Xunit;

namespace FoxMelee.Tests {
    public class FoxSessionTests {
        static HeroFlags Idle => new HeroFlags { WaitingToTransition = true, AcceptingInput = true };

        [Fact]
        public void FoxDrivesOnlyInAConvertedRoomWhenHollowKnightIsNotBusy() {
            var s = new FoxSession();
            Assert.Equal(HeroOwner.Hollow, s.Owner(Idle));          // no room yet
            Assert.False(s.RoomConverted("Tutorial_01", true));
            Assert.Equal(HeroOwner.Fox, s.Owner(Idle));
            foreach (var busy in new[] {
                new HeroFlags { Transitioning = true, WaitingToTransition = true },
                new HeroFlags { WaitingToTransition = false },
                new HeroFlags { WaitingToTransition = true, HazardRespawning = true },
                new HeroFlags { WaitingToTransition = true, HazardDeath = true },
                new HeroFlags { WaitingToTransition = true, Dead = true },
                new HeroFlags { WaitingToTransition = true, Loading = true },
                new HeroFlags { WaitingToTransition = true, Respawning = true } })   // HK's Respawn/HazardRespawn still running   // a save loading: the Knight is parked off-room
                Assert.Equal(HeroOwner.Hollow, s.Owner(busy));
        }

        [Fact]
        public void TakingTheKnightBlocksHisInputEveryTimeHollowKnightReEnablesIt() {
            var s = new FoxSession();
            s.RoomConverted("Tutorial_01", true);
            var first = s.Take(new HeroFlags { WaitingToTransition = true, AcceptingInput = true, ControlRelinquished = false });
            Assert.True(first.IgnoreInput && first.Relinquish);
            // after a transition FinishedEnteringScene calls AcceptInput but leaves controlReqlinquished set
            var later = s.Take(new HeroFlags { WaitingToTransition = true, AcceptingInput = true, ControlRelinquished = true });
            Assert.True(later.IgnoreInput);
            Assert.False(later.Relinquish);
        }

        [Fact]
        public void AFailedRoomGivesTheKnightBackAndFoxWaitsForTheNextRoom() {
            var s = new FoxSession();
            Assert.False(s.RoomConverted("A", false));              // first room fails: nothing was taken
            Assert.Equal(HeroOwner.Hollow, s.Owner(Idle));
            Assert.True(s.KnightVisible(Idle));
            s.RoomConverted("B", true);
            s.Take(Idle);
            Assert.True(s.RoomConverted("C", false));               // a later room fails: release the Knight we took
            Assert.Equal(HeroOwner.Hollow, s.Owner(Idle));
            Assert.False(s.NeedsRoom("C"));                         // no retry every frame
            Assert.True(s.NeedsRoom("D"));
        }

        [Fact]
        public void TheKnightIsReleasedOnFailureOnlyIfFoxTookHim() {
            var s = new FoxSession();
            Assert.False(s.Fail());                                 // boot failed: never touch the Knight
            s.RoomConverted("A", true);
            s.Take(Idle);
            Assert.True(s.Fail());
            Assert.False(s.Fail());
        }

        [Fact]
        public void TheBodyGoesBackForTransitionsAndRespawnsButNotDeath() {
            var s = new FoxSession();
            s.RoomConverted("A", true);
            Assert.True(s.ReturnBody(new HeroFlags { Transitioning = true, WaitingToTransition = true }));
            Assert.True(s.ReturnBody(new HeroFlags { WaitingToTransition = true, HazardRespawning = true }));
            Assert.False(s.ReturnBody(new HeroFlags { WaitingToTransition = true, Dead = true }));   // Die keeps it kinematic
        }

        [Fact]
        public void TheKnightStaysHiddenWhileFoxOwnsTheRoom() {
            var s = new FoxSession();
            s.RoomConverted("A", true);
            Assert.False(s.KnightVisible(Idle));
            Assert.False(s.KnightVisible(new HeroFlags { Transitioning = true, WaitingToTransition = true }));
        }
    }
}

namespace FoxMelee.Tests {
    public class FoxSessionRespawnTests {
        static HeroFlags Playing => new HeroFlags { WaitingToTransition = true, AcceptingInput = true };

        [Xunit.Fact]
        public void SpikesAndDeathBringFoxBackStillButTransitionsKeepMomentum() {
            var s = new FoxSession();
            s.RoomConverted("A", true);
            Xunit.Assert.False(s.TakeRespawns());                          // first placement: keep (nothing to keep)
            var hazard = Playing; hazard.HazardDeath = true;
            s.Owner(hazard);
            Xunit.Assert.True(s.TakeRespawns());
            Xunit.Assert.False(s.TakeRespawns());                          // once
            var dead = Playing; dead.Dead = true;
            s.Owner(dead);
            Xunit.Assert.True(s.TakeRespawns());
            var trans = Playing; trans.Transitioning = true;
            s.Owner(trans);
            Xunit.Assert.False(s.TakeRespawns());                          // a door keeps Fox's run
            var respawning = Playing; respawning.HazardRespawning = true;
            s.Owner(respawning);
            Xunit.Assert.True(s.TakeRespawns());
        }

        [Fact]
        public void HollowKnightsRespawnRoutineCountsAsARespawn() {
            var s = new FoxSession();
            s.RoomConverted("Tutorial_01", true);
            Assert.Equal(HeroOwner.Hollow, s.Owner(new HeroFlags { WaitingToTransition = true, Respawning = true }));
            Assert.Equal(HeroOwner.Fox, s.Owner(Playing));
            Assert.True(s.TakeRespawns());
        }

        [Fact]
        public void AfterARespawnFoxWaitsForTheControllerToBeLetGo() {
            var hold = new RespawnHold();
            var right = new McInput { StickX = 100 };
            Assert.Equal(right, hold.Filter(right));                 // not armed: everything passes
            hold.Arm();
            Assert.Equal(new McInput(), hold.Filter(right));         // still holding right from before the hit
            Assert.Equal(new McInput(), hold.Filter(new McInput { Buttons = GcButtons.A }));
            var rest = new McInput { StickX = 5, StickY = -8, TriggerL = 20 };   // a real controller at rest
            Assert.Equal(rest, hold.Filter(rest));                   // let go: Fox plays again
            Assert.Equal(right, hold.Filter(right));
        }
    }
}
