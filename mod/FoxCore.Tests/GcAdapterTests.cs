using Xunit;

namespace FoxMelee.Tests {
    public class GcAdapterTests {
        // one 37-byte adapter report: 0x21, then 9 bytes per port: status, buttons1, buttons2, stick x/y, c x/y, L, R
        static byte[] Report(int port, byte status, byte b1, byte b2, byte sx, byte sy, byte cx, byte cy, byte l, byte r) {
            var p = new byte[37]; p[0] = 0x21;
            int o = 1 + 9 * port;
            p[o] = status; p[o + 1] = b1; p[o + 2] = b2; p[o + 3] = sx; p[o + 4] = sy; p[o + 5] = cx; p[o + 6] = cy; p[o + 7] = l; p[o + 8] = r;
            return p;
        }

        [Fact]
        public void ParsesButtonsIntoGameCubeBits() {
            var r = Report(0, 0x10, 0x01 | 0x02 | 0x80, 0x01 | 0x02 | 0x04 | 0x08, 128, 128, 128, 128, 0, 0);
            Assert.True(GcAdapterReport.Parse(r, 0, out var raw));
            Assert.Equal(GcButtons.A | GcButtons.B | GcButtons.DUp | GcButtons.Start | GcButtons.Z | GcButtons.R | GcButtons.L, raw.Buttons);
        }

        [Fact]
        public void NoControllerOrABadReportIsNothing() {
            Assert.False(GcAdapterReport.Parse(Report(0, 0x00, 0, 0, 0, 0, 0, 0, 0, 0), 0, out _));     // empty port
            Assert.False(GcAdapterReport.Parse(Report(1, 0x10, 0, 0, 0, 0, 0, 0, 0, 0), 0, out _));     // only port 2 has one
            Assert.True(GcAdapterReport.Parse(Report(1, 0x10, 0, 0, 0, 0, 0, 0, 0, 0), 1, out _));
            Assert.True(GcAdapterReport.Parse(Report(2, 0x20, 0, 0, 0, 0, 0, 0, 0, 0), 2, out _));      // WaveBird
            var bad = Report(0, 0x10, 0, 0, 0, 0, 0, 0, 0, 0); bad[0] = 0x22;
            Assert.False(GcAdapterReport.Parse(bad, 0, out _));
            Assert.False(GcAdapterReport.Parse(new byte[10], 0, out _));
        }

        [Fact]
        public void FirstReportIsTheOriginLikeAConsole() {
            var pad = new GcPad();
            // plugged in slightly off-centre: that is its origin
            var a = pad.Update(Report(0, 0x10, 0, 0, 130, 125, 127, 129, 30, 20), 0);
            Assert.Equal((sbyte)0, a.Value.StickX); Assert.Equal((sbyte)0, a.Value.StickY);
            Assert.Equal((sbyte)0, a.Value.CStickX); Assert.Equal((byte)0, a.Value.TriggerL);
            var b = pad.Update(Report(0, 0x10, 0x01, 0, 255, 0, 127, 129, 255, 10), 0).Value;
            Assert.Equal((sbyte)125, b.StickX);       // 255 - 130
            Assert.Equal((sbyte)-125, b.StickY);      // 0 - 125
            Assert.Equal((byte)225, b.TriggerL);      // 255 - 30
            Assert.Equal((byte)0, b.TriggerR);        // below its origin: 0
            Assert.Equal(GcButtons.A, b.Buttons);
        }

        [Fact]
        public void ValuesClampToTheirRange() {
            var pad = new GcPad();
            pad.Update(Report(0, 0x10, 0, 0, 100, 160, 128, 128, 0, 0), 0);
            var v = pad.Update(Report(0, 0x10, 0, 0, 255, 0, 128, 128, 0, 0), 0).Value;
            Assert.Equal((sbyte)127, v.StickX);       // 155 clamps
            Assert.Equal((sbyte)-128, v.StickY);      // -160 clamps
        }

        [Fact]
        public void UnpluggingForgetsTheOrigin() {
            var pad = new GcPad();
            pad.Update(Report(0, 0x10, 0, 0, 140, 128, 128, 128, 0, 0), 0);
            Assert.Null(pad.Update(Report(0, 0x00, 0, 0, 0, 0, 0, 0, 0, 0), 0));
            var v = pad.Update(Report(0, 0x10, 0, 0, 128, 128, 128, 128, 0, 0), 0).Value;
            Assert.Equal((sbyte)0, v.StickX);         // a new origin
        }

        [Fact]
        public void TheChosenPortOrTheFirstControllerPlaysAndStartStaysWithHollowKnight() {
            var pads = new GcPads();
            var r = Report(2, 0x10, 0x01, 0x01, 128, 128, 128, 128, 0, 0);   // A + Start on port 3 only
            pads.Update(r);
            var auto = pads.Melee(0);
            Assert.Equal(GcButtons.A, auto.Value.Buttons);                  // Start stripped: it pauses Hollow Knight
            Assert.Equal(GcButtons.A | GcButtons.Start, pads.Current(0).Value.Buttons);
            Assert.Null(pads.Melee(1));                                      // port 1 chosen, nothing there
            Assert.NotNull(pads.Melee(3));
            pads.Update(null);                                              // adapter gone
            Assert.Null(pads.Melee(0));
        }
    }
}
