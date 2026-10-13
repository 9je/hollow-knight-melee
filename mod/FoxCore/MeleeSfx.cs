using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FoxMelee {
    /// One of Melee's sound effect calls (core McSfx): Start(id, vol 0..254, pan 0..254 with 0x80 centre, track,
    /// mix channel) returning Handle (-1 once Melee's voices are used up), or a key off by handle, of everything, or
    /// of a track. Frame: the core frame when Melee made the call.
    [StructLayout(LayoutKind.Sequential)]
    public struct McSfx {
        public const uint Start = 0, KeyOff = 1, KeyOffAll = 2, KeyOffTrack = 3;
        public uint Kind, Id;
        public byte Vol, Pan, Track, Channel;
        public int Handle;
        public uint Frame;
    }

    /// One script command of melee.sfx: Melee's AXDriverInterp word as (op, value, wait ticks); see
    /// tools/src/ssbmhk_tools/meleesfx.py.
    public readonly struct SfxCommand : IEquatable<SfxCommand> {
        public readonly byte Op; public readonly int Value; public readonly uint Wait;
        public SfxCommand(byte op, int value, uint wait) { Op = op; Value = value; Wait = wait; }
        public bool Equals(SfxCommand o) => Op == o.Op && Value == o.Value && Wait == o.Wait;
        public override bool Equals(object o) => o is SfxCommand c && Equals(c);
        public override int GetHashCode() => (Op * 31 + Value) * 31 + (int)Wait;
        public override string ToString() => $"({Op}, {Value}, {Wait})";
    }

    /// A decoded Melee sample: 16-bit PCM, channels interleaved. LoopStart: first frame of the looped part, -1 none.
    public sealed class SfxSample {
        public int Id, Rate, Channels, Frames, LoopStart;
        public short[] Pcm;
        public double Seconds => (double)Frames / Rate;
    }

    /// melee.sfx (written by ssbmhk-extract-sfx from the player's ISO): Melee's sound scripts for the main and Fox
    /// banks and the samples they play.
    public sealed class SfxBank {
        readonly Dictionary<uint, SfxCommand[]> scripts = new Dictionary<uint, SfxCommand[]>();
        readonly Dictionary<int, SfxSample> samples = new Dictionary<int, SfxSample>();
        public int ScriptCount => scripts.Count;
        public IEnumerable<SfxSample> Samples => samples.Values;
        public bool TryGetScript(uint id, out SfxCommand[] s) => scripts.TryGetValue(id, out s);
        public bool TryGetSample(int id, out SfxSample s) => samples.TryGetValue(id, out s);

        /// Another bundle's sounds and samples (a character's bank next to the main one); existing ids are kept.
        public void Add(SfxBank other) {
            foreach (var kv in other.scripts) if (!scripts.ContainsKey(kv.Key)) scripts[kv.Key] = kv.Value;
            foreach (var kv in other.samples) if (!samples.ContainsKey(kv.Key)) samples[kv.Key] = kv.Value;
        }

        public static SfxBank Load(string path) { using var f = File.OpenRead(path); return Load(f); }
        public static SfxBank Load(Stream stream) {
            var r = new BinaryReader(stream);
            try {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "MSFX" || r.ReadUInt32() != 1)
                    throw new InvalidDataException("not a melee.sfx v1 bundle");
                var b = new SfxBank();
                uint ns = r.ReadUInt32(), nm = r.ReadUInt32();
                for (uint i = 0; i < ns; i++) {
                    uint id = r.ReadUInt32(), n = r.ReadUInt32();
                    if (n > 4096) throw new InvalidDataException($"sound {id}: {n} commands");
                    var cmds = new SfxCommand[n];
                    for (int k = 0; k < n; k++) {
                        byte op = r.ReadByte(); r.ReadBytes(3);
                        cmds[k] = new SfxCommand(op, r.ReadInt32(), r.ReadUInt32());
                    }
                    b.scripts[id] = cmds;
                }
                for (uint i = 0; i < nm; i++) {
                    var s = new SfxSample { Id = (int)r.ReadUInt32(), Rate = (int)r.ReadUInt32(), Channels = r.ReadUInt16() };
                    r.ReadUInt16();
                    s.Frames = (int)r.ReadUInt32(); s.LoopStart = r.ReadInt32();
                    if (s.Channels < 1 || s.Channels > 2 || s.Rate <= 0 || s.Frames < 0 || s.Frames > 1 << 24)
                        throw new InvalidDataException($"sample {s.Id}: {s.Channels} channels, {s.Rate} Hz, {s.Frames} frames");
                    var bytes = r.ReadBytes(2 * s.Frames * s.Channels);
                    if (bytes.Length != 2 * s.Frames * s.Channels) throw new EndOfStreamException();
                    s.Pcm = new short[s.Frames * s.Channels];
                    Buffer.BlockCopy(bytes, 0, s.Pcm, 0, bytes.Length);
                    if (!BitConverter.IsLittleEndian) for (int k = 0; k < s.Pcm.Length; k++) s.Pcm[k] = (short)((ushort)s.Pcm[k] >> 8 | (ushort)s.Pcm[k] << 8);
                    b.samples[s.Id] = s;
                }
                return b;
            } catch (EndOfStreamException) {
                throw new InvalidDataException("melee.sfx is truncated");
            }
        }
    }

    public enum SfxActionKind { Play, Update, Stop }

    /// What the player does to one voice: Play a sample (from the start), Update its gain/pan/pitch, or Stop it.
    /// Gain 0..1 (Melee's script volume times the call's), Pan -1 left .. 1 right, Pitch a playback-rate factor.
    public struct SfxAction {
        public SfxActionKind Kind;
        public int Voice;
        public SfxSample Sample;
        public float Gain, Pan, Pitch;
    }

    /// Melee's sound effect logic without the audio: runs each started sound's script as AXDriverInterp does (a
    /// command's value is heard when the voice next waits or ends; 1 tick = 5 ms), and Melee's voice rules: a new
    /// sound on a nonzero track cuts the last one on that track; key off by handle, track or everything.
    public sealed class SfxMixer {
        const double TicksPerSecond = 200;
        public const double LoopCapSeconds = 10;   // a looping sound no key off can reach (handle -1) stops by itself

        sealed class Voice {
            public int Id, Handle, Pc, LoopCount;
            public byte Track, CallPan;
            public float CallGain;
            public SfxCommand[] Script;
            public double WakeTick, StartTick, Position;   // Position: sample frames played
            public bool ScriptDone, Playing, LoopForever, PanFromScript;
            public bool PendSample, PendVol, PendPan, PendPitch;
            public int SampleId = -1, Vol = 255, Pan = 0x80, Cents;
            public SfxSample Sample;
        }

        readonly SfxBank bank;
        readonly int maxVoices;
        readonly List<Voice> voices = new List<Voice>();
        double now;   // ticks
        int nextId = 1;

        public SfxMixer(SfxBank bank, int maxVoices = 16) { this.bank = bank; this.maxVoices = Math.Max(1, maxVoices); }
        public int ActiveVoices => voices.Count;

        public void Handle(McSfx e, List<SfxAction> output) {
            switch (e.Kind) {
            case McSfx.Start:
                if (!bank.TryGetScript(e.Id, out var script)) return;
                if (e.Track != 0)
                    for (int i = voices.Count - 1; i >= 0; i--) if (voices[i].Track == e.Track) Remove(i, output);
                if (voices.Count >= maxVoices) Remove(0, output);
                var v = new Voice { Id = nextId++, Handle = e.Handle, Track = e.Track, CallPan = e.Pan, CallGain = e.Vol / 254f,
                                    Script = script, WakeTick = now, StartTick = now };
                voices.Add(v);
                Run(v, output);
                if (Finished(v)) Remove(voices.IndexOf(v), output);
                break;
            case McSfx.KeyOff:
                if (e.Handle < 0) return;
                for (int i = voices.Count - 1; i >= 0; i--) if (voices[i].Handle == e.Handle) Remove(i, output);
                break;
            case McSfx.KeyOffAll:
                for (int i = voices.Count - 1; i >= 0; i--) Remove(i, output);
                break;
            case McSfx.KeyOffTrack:
                for (int i = voices.Count - 1; i >= 0; i--) if (voices[i].Track == e.Track) Remove(i, output);
                break;
            }
        }

        /// Moves time on: scripts run their waits, samples play out.
        public void Advance(float seconds, List<SfxAction> output) {
            double before = now;
            now += seconds * TicksPerSecond;
            for (int i = 0; i < voices.Count; i++) {
                var v = voices[i];
                if (v.Playing) v.Position += (now - Math.Max(before, v.StartTick)) / TicksPerSecond * v.Sample.Rate * PitchOf(v);
                Run(v, output);
                if (Finished(v)) Remove(i--, output);
            }
        }

        bool Finished(Voice v) {
            if (v.Playing) {
                if (v.Sample.LoopStart < 0) return v.Position >= v.Sample.Frames;
                return v.Handle < 0 && v.Track == 0 && now - v.StartTick >= LoopCapSeconds * TicksPerSecond;
            }
            return v.ScriptDone;
        }

        void Remove(int i, List<SfxAction> output) {
            var v = voices[i];
            voices.RemoveAt(i);
            if (v.Playing) output.Add(new SfxAction { Kind = SfxActionKind.Stop, Voice = v.Id });
        }

        static float PitchOf(Voice v) => (float)Math.Pow(2, v.Cents / 1200.0);

        // AXDriverInterp: runs commands while the voice is awake; a command that waits first applies what is pending.
        void Run(Voice v, List<SfxAction> output) {
            for (int guard = 0; !v.ScriptDone && v.WakeTick <= now && guard < 1000; guard++) {
                if (v.Pc >= v.Script.Length) { Exec(v, output); v.ScriptDone = true; break; }
                var c = v.Script[v.Pc];
                if (c.Wait != 0) Exec(v, output);
                v.WakeTick += c.Wait;
                switch (c.Op) {
                case 1: v.SampleId = c.Value; v.PendSample = true; break;
                case 2: v.LoopCount = c.Value; v.LoopForever = c.Value == 0; break;
                case 3:
                    if (v.LoopForever || v.LoopCount != 0) { v.Pc -= c.Value; v.LoopCount--; }
                    break;
                case 6: v.Vol = c.Value & 0xFF; v.PendVol = true; break;
                case 7: v.Vol = Clamp(v.Vol + c.Value, 0, 255); v.PendVol = true; break;
                case 8: v.Pan = c.Value & 0xFF; v.PendPan = true; break;
                case 9: v.Pan = Clamp(v.Pan + c.Value, 0, 255); v.PendPan = true; break;
                case 12: v.Cents = (short)c.Value; v.PendPitch = true; break;
                case 13: v.Cents = Clamp(v.Cents + c.Value, -0x2A30, 0x960); v.PendPitch = true; break;
                case 14: Exec(v, output); v.ScriptDone = true; return;
                case 15:
                    Exec(v, output); v.ScriptDone = true;
                    if (v.Playing) { v.Playing = false; output.Add(new SfxAction { Kind = SfxActionKind.Stop, Voice = v.Id }); }
                    return;
                }
                v.Pc++;
                if (v.Pc < 0) v.Pc = 0;
            }
        }

        // AXDriverExec: a pending sample starts (the call's pan, unless the script set one); other changes update.
        void Exec(Voice v, List<SfxAction> output) {
            if (v.PendSample) {
                if (v.Playing) output.Add(new SfxAction { Kind = SfxActionKind.Stop, Voice = v.Id });
                v.Playing = bank.TryGetSample(v.SampleId, out v.Sample);
                if (v.Playing) {
                    v.PanFromScript = v.PendPan;
                    v.StartTick = now; v.Position = 0;
                    output.Add(Action(SfxActionKind.Play, v));
                }
            } else if (v.Playing && (v.PendVol || v.PendPan || v.PendPitch)) {
                v.PanFromScript |= v.PendPan;
                output.Add(Action(SfxActionKind.Update, v));
            }
            v.PendSample = v.PendVol = v.PendPan = v.PendPitch = false;
        }

        static SfxAction Action(SfxActionKind k, Voice v) {
            int pan = v.PanFromScript ? v.Pan : v.CallPan;
            return new SfxAction {
                Kind = k, Voice = v.Id, Sample = v.Sample, Gain = v.Vol / 255f * v.CallGain,
                Pan = PanOf(pan), Pitch = PitchOf(v),
            };
        }

        /// Melee pan 0 left .. 0x80 centre .. 254 right -> -1 .. 1.
        public static float PanOf(int pan) => pan <= 0x80 ? (pan - 0x80) / 128f : Math.Min(1f, (pan - 0x80) / 126f);

        static int Clamp(int x, int lo, int hi) => x < lo ? lo : x > hi ? hi : x;
    }
}
