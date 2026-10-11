using Xunit;

namespace FoxMelee.Tests {
    public class InputMapperTests {
        [Fact]
        public void NeutralIsAllZero() {
            var m = InputMapper.Map(new PadState());
            Assert.Equal(new McInput(), m);
        }

        [Fact]
        public void SticksScaleToMeleeFullTilt() {
            var m = InputMapper.Map(new PadState { LeftX = 1, LeftY = -1, RightX = -0.5f, RightY = 0.25f });
            Assert.Equal(80, m.StickX); Assert.Equal(-80, m.StickY);
            Assert.Equal(-40, m.CStickX); Assert.Equal(20, m.CStickY);
        }

        [Fact]
        public void OutOfRangeAxesAreClamped() {
            var m = InputMapper.Map(new PadState { LeftX = 3, LeftY = -3 });
            Assert.Equal(127, m.StickX); Assert.Equal(-127, m.StickY);
        }

        [Fact]
        public void TriggersAreAnalogAndClickPastNinetyPercent() {
            var half = InputMapper.Map(new PadState { LeftTrigger = 0.5f });
            Assert.Equal(70, half.TriggerL);
            Assert.Equal(0, half.Buttons & GcButtons.L);
            var full = InputMapper.Map(new PadState { RightTrigger = 1 });
            Assert.Equal(140, full.TriggerR);
            Assert.NotEqual(0, full.Buttons & GcButtons.R);
        }

        [Fact]
        public void FaceButtonsMapToMeleeButtons() {
            var m = InputMapper.Map(new PadState { South = true, East = true, West = true, North = true, RightBumper = true });
            Assert.Equal(GcButtons.A | GcButtons.B | GcButtons.X | GcButtons.Y | GcButtons.Z, m.Buttons);
        }

        [Fact]
        public void LeftBumperIsAFullShield() {
            var m = InputMapper.Map(new PadState { LeftBumper = true });
            Assert.Equal(140, m.TriggerR);
            Assert.NotEqual(0, m.Buttons & GcButtons.R);
        }

        [Fact]
        public void DpadAndStartNeverReachMelee() {
            var m = InputMapper.Map(new PadState());
            Assert.Equal(0, m.Buttons & (GcButtons.DLeft | GcButtons.DRight | GcButtons.DUp | GcButtons.DDown | GcButtons.Start));
        }
    }
}
