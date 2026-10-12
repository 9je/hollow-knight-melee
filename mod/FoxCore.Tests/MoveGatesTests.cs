using Xunit;

namespace FoxMelee.Tests {
    public class MoveGatesTests {
        [Fact]
        public void StartLocksEveryUnlockableMove() =>
            Assert.Equal(MoveGates.DoubleJump | MoveGates.WallJump | MoveGates.Blaster | MoveGates.Illusion | MoveGates.FireFox | MoveGates.Shine,
                         MoveGates.Locked(new HkUnlocks()));

        [Fact]
        public void EachUnlockFreesItsMove() {
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks {
                VengefulSpirit = true, MothwingCloak = true, MantisClaw = true, CrystalHeart = true, DesolateDive = true, MonarchWings = true }));
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks { VengefulSpirit = true }) & MoveGates.Shine);
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks { MothwingCloak = true }) & MoveGates.Illusion);
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks { MantisClaw = true }) & MoveGates.WallJump);
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks { CrystalHeart = true }) & MoveGates.FireFox);
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks { DesolateDive = true }) & MoveGates.Blaster);
            Assert.Equal(0u, MoveGates.Locked(new HkUnlocks { MonarchWings = true }) & MoveGates.DoubleJump);
        }
    }
}
