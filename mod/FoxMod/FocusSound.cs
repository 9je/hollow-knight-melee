using UnityEngine;

namespace FoxMelee {
    /// The Knight's own Focus sounds (his spell state machine plays them; the clips are found by name once loaded):
    /// the charge loops while Fox focuses, the heal plays for each mask.
    sealed class FocusSound {
        AudioSource source;
        AudioClip charge, heal;
        bool looked, playing;

        void Find() {
            if (looked) return;
            looked = true;
            foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>()) {
                if (c.name == "focus_health_heal") heal = c;
                else if (c.name == "focus_health_charging") charge = c;
            }
            FoxMod.Instance.Log($"focus sounds: charge {(charge ? charge.name : "none")}, heal {(heal ? heal.name : "none")}");
            var go = new GameObject("FoxFocusSound");
            Object.DontDestroyOnLoad(go);
            source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
        }

        public void Charging(bool on) {
            if (on == playing) return;
            Find();
            playing = on;
            if (charge == null) return;
            if (on) { source.clip = charge; source.loop = true; source.Play(); }
            else if (source.clip == charge) source.Stop();
        }

        public void Heal() {
            Find();
            if (heal != null) source.PlayOneShot(heal);
        }
    }
}
