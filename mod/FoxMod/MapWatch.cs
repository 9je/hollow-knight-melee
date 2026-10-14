using System.Text;
using UnityEngine;

namespace FoxMelee {
    /// The quick map, watched: while the mod holds the Knight for it, Hollow Knight's Map Control state machine (Inactive →
    /// Button Down Check → Button Still Down? → Can QuickMap? → Has Map? → Open Map; BUTTON UP, LEFT GROUND or INVENTORY
    /// OPENED close it) is logged at every state change, with everything it tests, so a map that fails to open says why in
    /// ModLog.txt: the button Hollow Knight sees, HeroController.CanQuickMap and each of its conditions, hasMap, atBench,
    /// disablePause, atMapPrompt.
    sealed class MapWatch {
        PlayMakerFSM fsm;
        string lastState, lastWhy;
        readonly System.Action<string> log;

        public MapWatch(System.Action<string> log) { this.log = log; }

        public void Begin(HeroController hero) {
            if (fsm == null)
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    if (f.FsmName == "Map Control") { fsm = f; break; }
            lastState = null; lastWhy = null;
            log($"quick map: {(fsm == null ? "no Map Control state machine found" : fsm.gameObject.name + " in " + fsm.ActiveStateName)}; {Why(hero)}");
        }

        /// Each frame of the hold: the state machine's state and the conditions, when either changes.
        public void Frame(HeroController hero) {
            if (fsm == null) return;
            string state = fsm.ActiveStateName, why = Why(hero);
            if (state == lastState && why == lastWhy) return;
            lastState = state; lastWhy = why;
            log($"quick map: {state}; {why}");
        }

        public void End() { if (fsm != null) log($"quick map: let go in {fsm.ActiveStateName}"); }

        static string Why(HeroController h) {
            var pd = PlayerData.instance;
            var ih = GameManager.instance?.inputHandler;
            var sb = new StringBuilder();
            sb.Append("button ").Append(ih != null && ih.inputActions.quickMap.IsPressed ? "down" : "up");
            sb.Append(", CanQuickMap ").Append(h.CanQuickMap());
            if (!h.CanQuickMap()) {
                sb.Append(" (");
                if (GameManager.instance.isPaused) sb.Append("paused ");
                if (h.controlReqlinquished) sb.Append("relinquished ");
                if (h.hero_state == GlobalEnums.ActorStates.no_input) sb.Append("no_input ");
                if (h.cState.onConveyor) sb.Append("conveyor ");
                if (h.cState.dashing || h.cState.backDashing) sb.Append("dashing ");
                if (h.cState.attacking) sb.Append("attacking ");
                if (h.cState.recoiling || h.cState.recoilFrozen) sb.Append("recoiling ");
                if (h.cState.transitioning) sb.Append("transitioning ");
                if (h.cState.hazardDeath || h.cState.hazardRespawning) sb.Append("hazard ");
                if (!h.cState.onGround) sb.Append("not on ground ");
                if (!h.CanInput()) sb.Append("not accepting input ");
                sb.Append(')');
            }
            sb.Append(", hasMap ").Append(pd.GetBool("hasMap")).Append(", atBench ").Append(pd.GetBool("atBench"));
            sb.Append(", disablePause ").Append(pd.GetBool("disablePause")).Append(", atMapPrompt ").Append(pd.GetBool("atMapPrompt"));
            return sb.ToString();
        }
    }
}
