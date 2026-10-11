using UnityEngine;

namespace FoxMelee {
    /// The Knight's own Focus effects (his "Focus Effects" child: the gathering lines, the dust either side, the burst
    /// when a mask comes back). The Knight's body stands where Fox is, so they play around Fox.
    sealed class FocusEffects {
        Transform root;
        tk2dSpriteAnimator lines;
        MeshRenderer linesRenderer;
        ParticleSystem dustL, dustR;
        GameObject heal;
        bool on, looked;

        void Find(HeroController hero) {
            if (looked && root != null) return;
            looked = true;
            root = hero.transform.Find("Focus Effects");
            if (root == null) { FoxMod.Instance.LogWarn("the Knight has no Focus Effects: Focus shows only Fox's glow"); return; }
            var l = root.Find("Lines Anim");
            if (l != null) { lines = l.GetComponent<tk2dSpriteAnimator>(); linesRenderer = l.GetComponent<MeshRenderer>(); }
            dustL = root.Find("Dust L")?.GetComponent<ParticleSystem>();
            dustR = root.Find("Dust R")?.GetComponent<ParticleSystem>();
            heal = root.Find("Heal Anim")?.gameObject;
            FoxMod.Instance.Log($"focus effects: lines {(lines != null ? lines.DefaultClip?.name : "none")}, dust {dustL != null && dustR != null}, heal {heal != null}");
        }

        public void Focusing(HeroController hero, bool focusing) {
            if (focusing == on) return;
            Find(hero);
            on = focusing;
            if (root == null) return;
            if (lines != null) {
                if (focusing) { if (linesRenderer != null) linesRenderer.enabled = true; lines.Play(lines.DefaultClip); }
                else { lines.Stop(); if (linesRenderer != null) linesRenderer.enabled = false; }
            }
            foreach (var d in new[] { dustL, dustR })
                if (d != null) { if (focusing) d.Play(); else d.Stop(); }
        }

        public void Healed(HeroController hero) {
            Find(hero);
            if (heal == null) return;
            heal.SetActive(false);
            heal.SetActive(true);   // its animation plays from the start on enable
        }
    }
}
