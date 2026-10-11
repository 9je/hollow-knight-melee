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
                new HeroFlags { WaitingToTransition = true, Dead = true } })
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
