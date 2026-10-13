using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Audio;

namespace FoxMelee {
    /// Plays Melee's sound effects: drains the core's sound calls after each Melee frame, runs them through
    /// SfxMixer (Melee's scripts and voice rules) and plays the samples from melee.sfx (extracted from the player's
    /// ISO at install) on a small pool of 2D AudioSources. Without melee.sfx the character is silent.
    sealed class SfxPlayer {
        const int Voices = 16;
        const float FrameSeconds = 1f / 60f;

        readonly SfxMixer mixer;
        readonly float volume;
        readonly AudioSource[] pool = new AudioSource[Voices];
        readonly Dictionary<int, AudioSource> playing = new Dictionary<int, AudioSource>();
        readonly Dictionary<int, AudioClip> clips = new Dictionary<int, AudioClip>();
        readonly McSfx[] events = new McSfx[256];
        readonly List<SfxAction> actions = new List<SfxAction>();

        /// Null (silent) when MeleeSfx is off or melee.sfx is missing or unreadable; says why once.
        public static SfxPlayer Create(MeleeCore core, Character character, Action<string> log) {
            var s = FoxMod.Instance.Settings;
            if (!s.MeleeSfx) { log("Melee sounds off (MeleeSfx)"); return null; }
            var path = Path.Combine(FoxMod.ModDir, "melee.sfx");
            if (!File.Exists(path)) { log("melee.sfx not found (extracted from your ISO by mod/install.sh): the character is silent"); return null; }
            SfxBank bank;
            try { bank = SfxBank.Load(path); } catch (Exception e) { log($"melee.sfx not used ({e.Message}): the character is silent"); return null; }
            foreach (int b in character.Banks) {
                var own = Path.Combine(FoxMod.ModDir, $"melee-{b}.sfx");
                if (!File.Exists(own)) { log($"melee-{b}.sfx not found (run mod/install.sh): {character.Name}'s own sounds are silent"); continue; }
                try { bank.Add(SfxBank.Load(own)); } catch (Exception e) { log($"melee-{b}.sfx not used ({e.Message})"); }
            }
            var p = new SfxPlayer(bank, Mathf.Clamp01((float)s.SfxVolume));
            p.Drain(core, play: false);   // the boot and countdown's calls
            log($"Melee sounds: {bank.ScriptCount} sounds");
            return p;
        }

        SfxPlayer(SfxBank bank, float volume) {
            mixer = new SfxMixer(bank, Voices);
            this.volume = volume;
            var go = root = new GameObject("FoxMelee SFX");
            UnityEngine.Object.DontDestroyOnLoad(go);
            // Hollow Knight's own sound effects group, so its sound volume setting applies too
            AudioMixerGroup group = null;
            var hero = HeroController.instance;
            if (hero != null) { var own = hero.GetComponent<AudioSource>(); if (own != null) group = own.outputAudioMixerGroup; }
            for (int i = 0; i < Voices; i++) {
                var a = go.AddComponent<AudioSource>();
                a.playOnAwake = false;
                a.spatialBlend = 0;
                a.loop = false;
                if (group != null) a.outputAudioMixerGroup = group;
                pool[i] = a;
            }
        }

        /// After each Melee frame: this frame's sound calls, then one frame of time.
        public void Frame(MeleeCore core) {
            Drain(core, play: true);
            mixer.Advance(FrameSeconds, actions);
            Apply();
        }

        void Drain(MeleeCore core, bool play) {
            int n;
            while ((n = core.ReadSfx(events)) > 0) {
                if (!play) continue;
                for (int i = 0; i < n; i++) mixer.Handle(events[i], actions);
                Apply();
            }
        }

        readonly GameObject root;
        /// Gone for good (a character change makes a new player with that character's sounds).
        public void Destroy() { StopAll(); if (root) UnityEngine.Object.Destroy(root); }

        public void StopAll() {
            foreach (var a in pool) if (a) a.Stop();
            playing.Clear();
            mixer.Handle(new McSfx { Kind = McSfx.KeyOffAll }, actions);
            actions.Clear();
        }

        void Apply() {
            foreach (var a in actions) {
                switch (a.Kind) {
                case SfxActionKind.Play:
                    var src = Free();
                    if (src == null) break;
                    src.clip = Clip(a.Sample);
                    Set(src, a);
                    src.Play();
                    playing[a.Voice] = src;
                    break;
                case SfxActionKind.Update:
                    if (playing.TryGetValue(a.Voice, out var u) && u) Set(u, a);
                    break;
                case SfxActionKind.Stop:
                    if (playing.TryGetValue(a.Voice, out var s)) { if (s) s.Stop(); playing.Remove(a.Voice); }
                    break;
                }
            }
            actions.Clear();
        }

        AudioSource Free() {
            foreach (var a in pool) if (a && !playing.ContainsValue(a)) return a;
            return null;
        }

        void Set(AudioSource src, SfxAction a) {
            src.volume = Mathf.Clamp01(a.Gain * volume);
            src.panStereo = a.Pan;
            src.pitch = a.Pitch;
        }

        // A looping sample's loop is unrolled up to SfxMixer.LoopCapSeconds (an AudioSource loops whole clips only).
        AudioClip Clip(SfxSample s) {
            if (clips.TryGetValue(s.Id, out var c)) return c;
            int frames = s.Frames;
            if (s.LoopStart >= 0 && s.LoopStart < s.Frames) {
                int loop = s.Frames - s.LoopStart, cap = (int)(SfxMixer.LoopCapSeconds * s.Rate);
                while (frames + loop <= Math.Max(cap, s.Frames)) frames += loop;
            }
            var data = new float[frames * s.Channels];
            for (int f = 0, src = 0; f < frames; f++, src = src + 1 < s.Frames ? src + 1 : s.LoopStart)
                for (int ch = 0; ch < s.Channels; ch++) data[f * s.Channels + ch] = s.Pcm[src * s.Channels + ch] / 32768f;
            c = AudioClip.Create($"melee_{s.Id}", frames, s.Channels, s.Rate, false);
            c.SetData(data, 0);
            clips[s.Id] = c;
            return c;
        }
    }
}
