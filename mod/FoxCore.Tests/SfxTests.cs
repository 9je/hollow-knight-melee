using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace FoxMelee.Tests {
    public class SfxTests {
        // script commands: (op, value, wait)
        static readonly int[][] Laser = { new[] { 1, 546, 0 }, new[] { 0xFD, 0, 0 }, new[] { 16, 20, 0 }, new[] { 4, 15, 0 }, new[] { 6, 204, 0 }, new[] { 14, 0, 0 } };
        static readonly int[][] Panned = { new[] { 1, 10, 0 }, new[] { 8, 0, 0 }, new[] { 12, 1200, 0 }, new[] { 6, 255, 0 }, new[] { 14, 0, 0 } };
        static readonly int[][] Delayed = { new[] { 0, 4, 4 }, new[] { 1, 10, 0 }, new[] { 6, 100, 0 }, new[] { 14, 0, 0 } };
        // fade: vol 200, loop (count 2: three passes) { vol -50, wait 10 ticks }, key off
        static readonly int[][] Fade = { new[] { 1, 11, 0 }, new[] { 6, 200, 0 }, new[] { 2, 2, 0 }, new[] { 7, -50, 10 }, new[] { 3, 2, 0 }, new[] { 15, 0, 0 } };
        static readonly int[][] Hum = { new[] { 1, 11, 0 }, new[] { 14, 0, 0 } };   // a looping sample, until a key off
        static readonly int[][] NoSample = { new[] { 1, 999, 0 }, new[] { 14, 0, 0 } };

        static byte[] Bundle() {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            var scripts = new SortedDictionary<uint, int[][]> { [110103] = Laser, [1] = Panned, [2] = Delayed, [3] = Fade, [4] = NoSample, [5] = Hum };
            w.Write(new[] { (byte)'M', (byte)'S', (byte)'F', (byte)'X' });
            w.Write(1u); w.Write((uint)scripts.Count); w.Write(3u);
            foreach (var kv in scripts) {
                w.Write(kv.Key); w.Write((uint)kv.Value.Length);
                foreach (var c in kv.Value) { w.Write((byte)c[0]); w.Write(new byte[3]); w.Write(c[1]); w.Write((uint)c[2]); }
            }
            void Sample(uint id, uint rate, ushort ch, int frames, int loop) {
                w.Write(id); w.Write(rate); w.Write(ch); w.Write((ushort)0); w.Write((uint)frames); w.Write(loop);
                for (int i = 0; i < frames * ch; i++) w.Write((short)(i * 10));
            }
            Sample(10, 32000, 1, 3200, -1);     // 0.1 s mono
            Sample(11, 32000, 1, 32000, 100);   // looping
            Sample(546, 16000, 2, 3200, -1);    // 0.2 s stereo
            return ms.ToArray();
        }

        static SfxBank Bank() => SfxBank.Load(new MemoryStream(Bundle()));

        // one sound playing one sample (MSFX v1)
        static SfxBank OneSound(uint id, int sample) {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'M', (byte)'S', (byte)'F', (byte)'X' });
            w.Write(1u); w.Write(1u); w.Write(1u);
            w.Write(id); w.Write(1u); w.Write((byte)1); w.Write(new byte[3]); w.Write(sample); w.Write(0u);
            w.Write((uint)sample); w.Write(32000u); w.Write((ushort)1); w.Write((ushort)0); w.Write(4u); w.Write(-1);
            for (int i = 0; i < 4; i++) w.Write((short)i);
            ms.Position = 0;
            return SfxBank.Load(ms);
        }

        [Fact]
        public void AddMergesAndKeepsExisting() {
            var a = OneSound(1, 5);
            a.Add(OneSound(110000, 7));
            a.Add(OneSound(1, 9));                       // an id already there stays as it was
            Assert.Equal(2, a.ScriptCount);
            Assert.True(a.TryGetScript(110000, out _));
            Assert.True(a.TryGetSample(7, out _));
            Assert.True(a.TryGetScript(1, out var s1) && s1[0].Value == 5);
        }
        static McSfx Start(uint id, byte pan = 0x80, byte track = 0, int handle = 1, byte vol = 254) =>
            new McSfx { Kind = McSfx.Start, Id = id, Vol = vol, Pan = pan, Track = track, Channel = 7, Handle = handle };

        [Fact]
        public void BankLoadsScriptsAndSamples() {
            var b = Bank();
            Assert.Equal(6, b.ScriptCount);
            Assert.True(b.TryGetScript(110103, out var s));
            Assert.Equal(6, s.Length);
            Assert.Equal(new SfxCommand(1, 546, 0), s[0]);
            Assert.True(b.TryGetSample(546, out var smp));
            Assert.Equal(2, smp.Channels); Assert.Equal(16000, smp.Rate); Assert.Equal(3200, smp.Frames); Assert.Equal(-1, smp.LoopStart);
            Assert.Equal(6400, smp.Pcm.Length);
            Assert.Equal(0.2, smp.Seconds, 3);
            Assert.False(b.TryGetSample(999, out _));
            Assert.Throws<InvalidDataException>(() => SfxBank.Load(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));
        }

        [Fact]
        public void LaserPlaysItsSampleAtScriptAndCallVolumeCentred() {
            var m = new SfxMixer(Bank());
            var o = new List<SfxAction>();
            m.Handle(Start(110103), o);
            var a = Assert.Single(o);
            Assert.Equal(SfxActionKind.Play, a.Kind);
            Assert.Equal(546, a.Sample.Id);
            Assert.Equal(204f / 255f, a.Gain, 3);
            Assert.Equal(0f, a.Pan, 3);
            Assert.Equal(1f, a.Pitch, 3);
            Assert.Equal(1, m.ActiveVoices);
            o.Clear();
            m.Advance(0.1f, o);
            Assert.Empty(o);                                  // still playing (0.2 s)
            m.Advance(0.11f, o);
            Assert.Equal(SfxActionKind.Stop, Assert.Single(o).Kind);   // ended: the voice is freed
            Assert.Equal(0, m.ActiveVoices);
        }

        [Fact]
        public void CallerPanUnlessTheScriptSetsOne() {
            var m = new SfxMixer(Bank());
            var o = new List<SfxAction>();
            m.Handle(Start(110103, pan: 0), o);
            Assert.Equal(-1f, o[0].Pan, 3);                    // caller's pan, hard left
            m.Handle(Start(110103, pan: 254, vol: 127), o);
            Assert.Equal(1f, o[1].Pan, 2);
            Assert.Equal(204f / 255f * 127f / 254f, o[1].Gain, 3);
            m.Handle(Start(1, pan: 254), o);
            Assert.Equal(-1f, o[2].Pan, 3);                   // the script's own pan (0) wins
            Assert.Equal(2f, o[2].Pitch, 3);                  // +1200 cents
        }

        [Fact]
        public void AnInitialWaitDelaysTheStart() {
            var m = new SfxMixer(Bank());
            var o = new List<SfxAction>();
            m.Handle(Start(2), o);
            Assert.Empty(o);
            m.Advance(0.015f, o);                             // 3 ticks of 4
            Assert.Empty(o);
            m.Advance(0.006f, o);
            Assert.Equal(SfxActionKind.Play, Assert.Single(o).Kind);
            Assert.Equal(100f / 255f, o[0].Gain, 3);
        }

        [Fact]
        public void ScriptFadesThenKeysOff() {
            var m = new SfxMixer(Bank());
            var o = new List<SfxAction>();
            m.Handle(Start(3), o);
            Assert.Equal(200f / 255f, Assert.Single(o).Gain, 3);
            Assert.True(o[0].Sample.LoopStart >= 0);
            o.Clear();
            m.Advance(0.051f, o);                             // 10 ticks: the first -50 is heard
            Assert.Equal(SfxActionKind.Update, Assert.Single(o).Kind);
            Assert.Equal(150f / 255f, o[0].Gain, 3);
            o.Clear();
            m.Advance(0.1f, o);                               // the other two steps and the key off
            Assert.Equal(new[] { SfxActionKind.Update, SfxActionKind.Update, SfxActionKind.Stop }, o.Select(a => a.Kind));
            Assert.Equal(50f / 255f, o[1].Gain, 3);
            Assert.Equal(0, m.ActiveVoices);
        }

        [Fact]
        public void TrackCutsKeyOffsAndUnknownSounds() {
            var m = new SfxMixer(Bank());
            var o = new List<SfxAction>();
            m.Handle(Start(110103, track: 0x36, handle: 10), o);
            int first = o[0].Voice;
            m.Handle(Start(110103, track: 0x36, handle: 11), o);   // same nonzero track: the first is cut
            Assert.Equal(SfxActionKind.Stop, o[1].Kind); Assert.Equal(first, o[1].Voice);
            Assert.Equal(SfxActionKind.Play, o[2].Kind);
            int second = o[2].Voice;
            m.Handle(Start(110103, track: 0, handle: 12), o);
            m.Handle(Start(110103, track: 0, handle: 13), o);      // track 0 never cuts
            Assert.Equal(5, o.Count);
            Assert.Equal(3, m.ActiveVoices);
            o.Clear();
            m.Handle(new McSfx { Kind = McSfx.KeyOff, Handle = 12 }, o);
            Assert.Equal(SfxActionKind.Stop, Assert.Single(o).Kind);
            m.Handle(new McSfx { Kind = McSfx.KeyOff, Handle = -1 }, o);   // no handle: nothing
            Assert.Single(o);
            m.Handle(new McSfx { Kind = McSfx.KeyOffTrack, Track = 0x36 }, o);
            Assert.Equal(second, o[1].Voice);
            m.Handle(new McSfx { Kind = McSfx.KeyOffAll }, o);
            Assert.Equal(3, o.Count);
            Assert.Equal(0, m.ActiveVoices);
            o.Clear();
            m.Handle(Start(55555), o);                               // not in the bundle
            m.Handle(Start(4), o);                                   // its sample is not either
            m.Advance(0.1f, o);
            Assert.Empty(o);
            Assert.Equal(0, m.ActiveVoices);
        }

        [Fact]
        public void TheOldestVoiceIsStolenWhenAllAreBusy() {
            var m = new SfxMixer(Bank(), maxVoices: 2);
            var o = new List<SfxAction>();
            m.Handle(Start(110103, handle: 1), o);
            m.Handle(Start(110103, handle: 2), o);
            m.Handle(Start(110103, handle: 3), o);
            Assert.Equal(SfxActionKind.Stop, o[2].Kind); Assert.Equal(o[0].Voice, o[2].Voice);
            Assert.Equal(SfxActionKind.Play, o[3].Kind);
            Assert.Equal(2, m.ActiveVoices);
        }

        [Fact]
        public void LoopsWithoutAHandleStopOnTheirOwn() {
            // a looping sample plays until a key off by handle, which never comes once Melee's voices are used up
            // (handle -1): such a voice is capped
            var m = new SfxMixer(Bank());
            var o = new List<SfxAction>();
            m.Handle(Start(5, handle: 7), o);
            m.Handle(Start(5, handle: -1), o);
            for (int i = 0; i < 60 * 30; i++) m.Advance(1f / 60, o);
            Assert.Equal(1, m.ActiveVoices);
            Assert.Equal(SfxActionKind.Stop, o[2].Kind); Assert.Equal(o[1].Voice, o[2].Voice);
            m.Handle(new McSfx { Kind = McSfx.KeyOff, Handle = 7 }, o);
            Assert.Equal(0, m.ActiveVoices);
        }

        [Fact]
        public void LiveCoreReportsTheLaser() {
            if (!DevConfig.Available) return;   // needs make -C core shared and the ISO from dev.toml
            using var core = MeleeCore.Load(DevConfig.CoreSo);
            Assert.True(core.Init(DevConfig.Mem1, DevConfig.Iso), core.Error);
            Assert.True(core.StartSoloFox(), core.Error);
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) Assert.True(core.Step(), core.Error);
            var ev = new McSfx[64];
            while (core.ReadSfx(ev) > 0) { }
            core.SetInput(0, new McInput { Buttons = 0x0200 });   // B: blaster
            Assert.True(core.Step(), core.Error);
            core.SetInput(0, new McInput());
            bool laser = false;
            for (int i = 0; i < 30 && !laser; i++) {
                Assert.True(core.Step(), core.Error);
                int n = core.ReadSfx(ev);
                for (int k = 0; k < n; k++) laser |= ev[k].Kind == McSfx.Start && ev[k].Id == 110103 && ev[k].Handle >= 0 && ev[k].Pan == 0x80;
            }
            Assert.True(laser);
            Assert.Equal(0, core.ReadSfx(ev));
        }
    }
}
