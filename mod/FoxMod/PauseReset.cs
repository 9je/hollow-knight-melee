using System;
using System.Collections;
using GlobalEnums;
using UnityEngine;
using UnityEngine.UI;

namespace FoxMelee {
    /// The pause menu's "Reset character" (spec 2026-10-12-melee-roster-design.md §4): back to the last bench, as Hollow
    /// Knight respawns you there after a death but without dying (no shade, no Geo lost). The way out of anywhere a
    /// character can't leave: a spot it's stuck in, a tunnel too small for it (a bench lets you swap character).
    /// The button is a copy of the menu's Options button, below Quit; anything unexpected leaves the menu as it was.
    static class PauseReset {
        const PauseMenuButton.PauseButtonType ResetType = (PauseMenuButton.PauseButtonType)77;
        static bool hooked;
        static PauseMenuButton button;

        public static void Hook() {
            if (hooked) return;
            hooked = true;
            On.UnityEngine.UI.PauseMenuButton.OnSubmit += (orig, self, data) => {
                if (self.pauseButtonType != ResetType) { orig(self, data); return; }
                try { self.ForceDeselect(); } catch (Exception) { }
                GameManager.instance.StartCoroutine(Reset());
            };
            On.UIManager.GoToPauseMenu += (orig, self) => { Add(self); return orig(self); };
        }

        static void Add(UIManager ui) {
            if (button != null) return;
            try {
                var buttons = ui.pauseMenuScreen.GetComponentsInChildren<PauseMenuButton>(true);
                PauseMenuButton options = null, quit = null, cont = null;
                foreach (var b in buttons) {
                    if (b.pauseButtonType == PauseMenuButton.PauseButtonType.Options) options = b;
                    if (b.pauseButtonType == PauseMenuButton.PauseButtonType.Quit) quit = b;
                    if (b.pauseButtonType == PauseMenuButton.PauseButtonType.Continue) cont = b;
                }
                if (options == null || quit == null) { FoxMod.Instance.LogWarn("pause menu: no Options/Quit buttons; no Reset character"); return; }
                var go = UnityEngine.Object.Instantiate(options.gameObject, options.transform.parent);
                go.name = "Reset Character Button";
                var rt = go.GetComponent<RectTransform>();
                var ro = options.GetComponent<RectTransform>();
                var rq = quit.GetComponent<RectTransform>();
                rt.anchoredPosition = rq.anchoredPosition + (rq.anchoredPosition - ro.anchoredPosition);   // one step below Quit
                foreach (var loc in go.GetComponentsInChildren<AutoLocalizeTextUI>(true)) UnityEngine.Object.Destroy(loc);
                foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = "Reset character";
                button = go.GetComponent<PauseMenuButton>();
                button.pauseButtonType = ResetType;
                // the controller moves Quit -> Reset character -> (Continue). The menu's MenuButtonList lays its buttons'
                // navigation out itself when the screen first shows (Start) and would skip a button it doesn't list, so the
                // new one is added to its list (the Modding API's AddSelectable) and the layout redone if it already ran.
                var list = ui.pauseMenuScreen.GetComponent<MenuButtonList>() ?? ui.pauseMenuScreen.GetComponentInChildren<MenuButtonList>(true);
                if (list != null) {
                    list.AddSelectable(button);
                    bool started = false;
                    try { started = Modding.ReflectionHelper.GetField<MenuButtonList, bool>(list, "started"); } catch (Exception) { }
                    if (started) list.RecalculateNavigation();
                }
                var nq = quit.navigation; nq.mode = Navigation.Mode.Explicit; nq.selectOnDown = button; quit.navigation = nq;
                var nr = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = quit, selectOnDown = cont != null ? cont : quit };
                button.navigation = nr;
                if (cont != null && cont.navigation.selectOnUp == quit) { var nc = cont.navigation; nc.selectOnUp = button; cont.navigation = nc; }
                // the menu's bottom ornament (its fleur) sat where the new button goes: one step down with it
                var fleur = ui.pauseMenuScreen.bottomFleur != null ? ui.pauseMenuScreen.bottomFleur.GetComponent<RectTransform>() : null;
                if (fleur != null) fleur.anchoredPosition += rq.anchoredPosition - ro.anchoredPosition;
                FoxMod.Instance.Log("pause menu: Reset character added" + (fleur == null ? " (no bottom fleur found)" : "") + (list == null ? " (no button list: navigation set by hand)" : " (in the menu's button list)"));
            } catch (Exception e) {
                FoxMod.Instance.LogWarn("pause menu: Reset character not added (" + e.Message + ")");
                if (button != null) { UnityEngine.Object.Destroy(button.gameObject); button = null; }
            }
        }

        // Benchwarp's way: unpause, fade out, then Hollow Knight's own respawn at the bench (GameManager.ReadyForRespawn),
        // and once the scene is in, fade back in ourselves. The fade-out used here (the level transition's) leaves the
        // camera's fade state machine waiting for a level load, where the RESPAWN that Hollow Knight's respawn sends to
        // fade a death back in means nothing to it; the screen stayed black with the game playing under it.
        static IEnumerator Reset() {
            var gm = GameManager.instance;
            var hero = HeroController.instance;
            if (gm == null || hero == null) yield break;
            FoxMod.Instance.Log($"Reset character: to the bench in {gm.playerData.GetString("respawnScene")}");
            UIManager.instance.TogglePauseGame();
            yield return new WaitWhile(() => gm.isPaused);
            yield return null;
            hero.RelinquishControl();
            hero.AffectedByGravity(false);
            hero.transitionState = HeroTransitionState.EXITING_SCENE;
            gm.cameraCtrl.FadeOut(CameraFadeType.LEVEL_TRANSITION);
            yield return new WaitForSecondsRealtime(0.5f);
            gm.playerData.SetBool("atBench", false);
            hero.cState.superDashing = false;
            hero.cState.spellQuake = false;
            gm.ReadyForRespawn(false);
            yield return null;
            yield return new WaitWhile(() => gm.IsInSceneTransition);
            Time.timeScale = 1f;
            gm.FadeSceneIn();
            FoxMod.Instance.Log("Reset character: scene in, fading in");
            // and if the camera is still dark a moment later, once more (the failsafe Hollow Knight's own camera has)
            yield return new WaitForSecondsRealtime(2f);
            var fade = GameCameras.instance != null ? GameCameras.instance.cameraFadeFSM : null;
            if (fade != null && fade.ActiveStateName != "Normal") {
                FoxMod.Instance.Log($"Reset character: camera fade still in {fade.ActiveStateName}: fading in again");
                gm.FadeSceneIn();
            }
        }
    }
}
