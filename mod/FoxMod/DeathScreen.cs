using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Hollow Knight's death while a Melee character plays: what Hollow Knight shows and plays for it (the Knight's mask
    /// cracking and falling, the shade leaving, its death sound) is not shown; Melee's KO is. Its death spawns objects
    /// outside the Knight's own death object too, so for a few seconds after the death starts, renderers and sounds
    /// that weren't there before are hidden and muted near where the Knight died. Each one is logged once.
    sealed class DeathScreen {
        const float Seconds = 4f, Radius = 8f;   // Hollow Knight units
        readonly HashSet<int> before = new HashSet<int>();
        readonly HashSet<int> handled = new HashSet<int>();
        readonly List<Renderer> hidden = new List<Renderer>();   // put back by End: pooled objects are reused later
        readonly List<AudioSource> muted = new List<AudioSource>();
        float started = -1;
        Vector2 at;

        public void Begin(Vector2 where) {
            End();
            before.Clear(); handled.Clear();
            foreach (var r in Object.FindObjectsOfType<Renderer>()) before.Add(r.GetInstanceID());
            foreach (var a in Object.FindObjectsOfType<AudioSource>()) before.Add(a.GetInstanceID());
            started = Time.unscaledTime;
            at = where;
        }

        /// The death is over: what was hidden or muted is put back (Hollow Knight pools and reuses these objects).
        public void End() {
            if (started < 0) return;
            started = -1;
            foreach (var r in hidden) if (r != null) r.forceRenderingOff = false;
            foreach (var a in muted) if (a != null) a.mute = false;
            hidden.Clear(); muted.Clear();
        }

        /// Each frame of a death.
        public void Frame(System.Action<string> log) {
            if (started < 0 || Time.unscaledTime - started > Seconds) return;
            foreach (var r in Object.FindObjectsOfType<Renderer>()) {
                int id = r.GetInstanceID();
                if (before.Contains(id) || handled.Contains(id) || Ours(r.gameObject)) continue;
                if (!r.enabled || ((Vector2)r.bounds.center - at).magnitude > Radius) continue;
                handled.Add(id);
                r.forceRenderingOff = true;
                hidden.Add(r);
                log("death: hid " + Path(r.transform));
            }
            foreach (var a in Object.FindObjectsOfType<AudioSource>()) {
                int id = a.GetInstanceID();
                if (before.Contains(id) || handled.Contains(id) || Ours(a.gameObject) || !a.isPlaying) continue;
                if (((Vector2)a.transform.position - at).magnitude > Radius) continue;
                handled.Add(id);
                a.mute = true;
                muted.Add(a);
                log("death: muted " + Path(a.transform) + (a.clip != null ? " (" + a.clip.name + ")" : ""));
            }
        }

        static bool Ours(GameObject go) => go.transform.root.name.StartsWith("Fox");

        static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
    }
}
