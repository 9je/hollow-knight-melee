using System;
using System.Collections.Generic;
using System.IO;
using InControl;
using UnityEngine;

namespace FoxMelee {
    /// Runs Fox: boots the core once a save is in gameplay, takes over the Knight, steps Melee at 60 Hz,
    /// rebuilds collision after every room change, and draws a placeholder. Any failure logs one error,
    /// gives the Knight back and stops (the game stays playable).
    public class FoxRunner : MonoBehaviour {
        enum State { Waiting, Running, Failed }
        const int TerrainLayer = 8;
        const float Frame = 1f / 60f;
        const float FarBlast = 100000f;          // Melee units: Hollow Knight's own pits and hazards handle falls

        State state = State.Waiting;
        MeleeCore core;
        RoomTransform room;
        readonly FoxSession session = new FoxSession();   // who moves the Knight (pure rules, unit-tested)
        bool holdingBody;                       // the Knight's body is kinematic because Fox drives it
        bool needPlace = true;                  // Fox starts where the Knight is once Fox drives again
        float accumulator;
        GameObject placeholder;
        EffectsRenderer effects;                // shield, shine and lasers (stand-ins when Melee's frame is not drawn)
        GxRenderer gx;                          // Melee's own frame: its models, effects and art
        EnemyBridge enemies;                    // enemies hitting Fox through Melee
        RoomTransform gxRoom;                   // the room gx last drew in
        CombatBridge combat;                    // Fox hitting enemies; null when FoxHits is off
        GrabBridge grab;                        // Fox's grab and Melee's push against small enemies; null when Grabs is off
        PogoBridge pogo;                        // spec §5.4 hazard pogo; null when Pogo is off
        EnemyLauncher launcher;                 // Melee's knockback on enemies; null when Knockback is off
        FoxRenderer model;                      // Fox's Melee model; null = the placeholder box
        SfxPlayer sfx;                          // Melee's sound effects; null = silent
        readonly float[] bones = new float[128 * 12];
        McFighter fox;
        const uint ActionWait = 14;             // Melee's Wait: Fox standing
        FocusRules focus;
        bool focusing, interactHeld, focusHeld, mapHeld;
        int focusDrain;                         // soul Focus has used but not yet taken (taken 3 at a time)
        McInput rawInput;                       // the controller this frame, before the respawn hold
        float stillFor; int stillFrames; float lastX, lastY; bool stuckLogged;
        const int ScanFrames = 30;              // how often new terrain (a gate closing) is looked for
        List<Collider2D> terrain = new List<Collider2D>();   // the colliders Melee's collision was built from
        readonly TerrainSet terrainSet = new TerrainSet();
        List<RoomShape> roomShapes = new List<RoomShape>();   // Melee's collision, as built (StuckGuard)
        readonly StuckGuard stuck = new StuckGuard();
        int scanTimer;
        readonly Dictionary<Collider2D, Vector2> terrainAt = new Dictionary<Collider2D, Vector2>();   // where each was last frame
        bool movingLogged;
        readonly RespawnHold respawnHold = new RespawnHold();
        readonly FocusSound focusSound = new FocusSound();
        readonly FocusEffects focusEffects = new FocusEffects();   // after a respawn: wait for the controller to be let go
        string lastBusy = "";                   // Hollow Knight's busy flags last logged

        // Hollow Knight's respawn routines clear dead / hazardDeath first, then reload, place and wake the Knight:
        // Fox waits until they finish (10 s at most, in case one is stopped part way).
        static int respawnRunning;
        static float respawnSince;
        static bool hooked;
        static System.Collections.IEnumerator Track(System.Collections.IEnumerator inner) {
            respawnRunning++;
            respawnSince = Time.unscaledTime;
            try { while (inner.MoveNext()) yield return inner.Current; }
            finally { respawnRunning--; }
        }
        static bool Respawning => respawnRunning > 0 && Time.unscaledTime - respawnSince < 10f;

        static void Log(string m) => FoxMod.Instance.Log(m);
        void Fail(string why) {
            FoxMod.Instance.LogError(why);
            GcInput.HoldFromHollow = false; GcInput.DreamNailButton = false;   // the controller is Hollow Knight's again
            state = State.Failed;
            if (session.Fail()) ReleaseHero();  // only a Knight Fox took (a boot failure leaves him alone)
            holdingBody = false;
            if (placeholder) placeholder.SetActive(false);
            model?.Hide(); effects?.Hide(); gx?.Hide();
            combat?.ReleaseAll(); grab?.Release();
            sfx?.StopAll();
        }

        static HeroFlags Flags(HeroController h) => new HeroFlags {
            Transitioning = h.cState.transitioning,
            WaitingToTransition = h.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION,
            HazardRespawning = h.cState.hazardRespawning, HazardDeath = h.cState.hazardDeath, Dead = h.cState.dead,
            AcceptingInput = h.acceptingInput, ControlRelinquished = h.controlReqlinquished,
            Respawning = Respawning,
            Loading = GameManager.instance.gameState != GlobalEnums.GameState.PLAYING
                   && GameManager.instance.gameState != GlobalEnums.GameState.PAUSED,
        };

        // Keyboard: while the character select (or a change loading) is up, Hollow Knight's own input is off: its action set
        // is disabled, so every action reads as released (InControl: EnabledInHierarchy), the pause (Escape) and the bench's
        // own listeners (its state machine gets the Knight up on jump, attack or any arrow) included. Hollow Knight never
        // sets the set's Enabled itself. Asked every frame, so whatever closes the select gives it back.
        bool mutedHollow;
        void MuteHollowKeys(bool want) {
            var ih = GameManager.instance?.inputHandler;
            if (ih == null || ih.inputActions == null) { mutedHollow = false; return; }
            if (want != mutedHollow) { ih.inputActions.Enabled = !want; mutedHollow = want; }
        }
        void OnDestroy() => MuteHollowKeys(false);

        void Update() {
            // a character change stuck past SwitchTimeout gives up (Fail: Hollow Knight has the Knight and its input again)
            if (switching != null && !switching.IsCompleted && Time.unscaledTime - switchStarted > SwitchTimeout) {
                switching = null;
                Fail($"the change to {switchingTo?.Name} didn't finish in {SwitchTimeout} s");
            }
            MuteHollowKeys(state != State.Failed && (grid != null || switching != null));
            if (state == State.Failed) { GcInput.HoldFromHollow = false; GcInput.DreamNailButton = false; return; }
            var hero = HeroController.instance;
            var gm = GameManager.instance;
            var invState = gm != null && gm.inventoryFSM != null ? gm.inventoryFSM.ActiveStateName : null;
            GcInput.InventoryOpen = invState != null && invState != "Closed" && !invState.StartsWith("Init");
            GcInput.HideDPadSides = hero != null && gm != null && gm.IsGameplayScene() && gm.gameState == GlobalEnums.GameState.PLAYING && !GcInput.InventoryOpen;
            // menus (and quitting to them: Quit_To_Menu is a gameplay scene to Hollow Knight, but Fox holding the Knight
            // there stalled the quit on a black screen): nothing of Fox lingers
            if (hero == null || gm == null || !gm.IsGameplayScene() || gm.sceneName == "Quit_To_Menu") {
                grid = null;   // no character select outside gameplay (its keyboard mute with it)
                if (placeholder) placeholder.SetActive(false);
                model?.Hide(); effects?.Hide(); gx?.Hide();
                combat?.ReleaseAll(); grab?.Release();
                if (holdingBody && hero != null) { GiveBodyBack(hero); hero.AffectedByGravity(true); }   // Hollow Knight's body again
                holdingBody = false;
                needPlace = true;
                return;
            }
            if (state == State.Waiting && !Boot()) return;
            if (switching != null && !switching.IsCompleted) return;   // a character change is restarting the core: nothing else uses it
            var f = Flags(hero);
            LogBusy(f);
            // Loading a save or coming back from death, the Knight wakes on the ground: Fox lies there instead and gets
            // up when Hollow Knight hands over (at a bench the Knight sits; that stays Hollow Knight's)
            if (f.Respawning && !f.HazardRespawning && !f.Dead && PlayerData.instance.GetInt("respawnType") == 0 && Wake(hero, gm, f)) return;
            // A new room is converted once Hollow Knight has finished bringing the Knight in.
            if (!FoxSession.HollowBusy(f) && session.NeedsRoom(gm.sceneName) && !EnterRoom(gm.sceneName)) return;
            SetKnightVisible(hero, session.KnightVisible(f));
            HideKnightDeaths(hero, !session.KnightVisible(f));
            Prompts.Active = !session.KnightVisible(f);
            if (session.Owner(f, Time.unscaledDeltaTime) == HeroOwner.Hollow) {
                DreamNailHeld();
                QuickMapHeld();
                if (Ko(hero, gm, f)) return;
                enemies?.Clear();
                // Transitions, hazards, death and unconvertible rooms are Hollow Knight's: it needs the Knight's
                // normal physics (entering a room drops him with gravity onto the floor).
                if (holdingBody && session.ReturnBody(f)) GiveBodyBack(hero);
                holdingBody = false;
                needPlace = true;
                accumulator = 0;
                if (!session.Interacting || !PlayerData.instance.GetBool("atBench")) { grid = null; GcInput.HoldFromHollow = false; }
                if (!session.Interacting) {     // reading a tablet or sitting down: Fox stays drawn where he stood
                    if (placeholder) placeholder.SetActive(false);
                    model?.Hide(); effects?.Hide(); gx?.Hide();
                } else if (PlayerData.instance.GetBool("atBench")) { if (FinishSwitch(hero)) { BenchMenu(hero); if (state == State.Running) Rest(hero); } }
                combat?.ReleaseAll(); grab?.Release();
                return;
            }
            if (switching != null && !FinishSwitch(hero)) return;   // a character change still loading
            if (gatesKey >= 0 && AbilitiesKey(Abilities()) != gatesKey) { Log("abilities changed: the room's gates again"); session.ForgetRoom(); return; }
            if (!WatchTerrain(gm.sceneName)) return;
            grid = null; GcInput.HoldFromHollow = false;   // the character moves: no character select holds the controller
            if (needPlace && waking) {            // Fox already lies where the Knight woke: he gets up from there
                waking = false; needPlace = false; getUp = true;
                session.TakeRespawns();
            }
            if (needPlace) {
                if (!PlaceAtKnight(hero)) return;
                needPlace = false;
                session.Placed();
                accumulator = Frame;              // the placement lands at the next Melee frame: run one now
            }
            var take = session.Take(f);
            TakeHero(hero, take.IgnoreInput, take.Relinquish);
            holdingBody = true;
            // real time, stopped only by Hollow Knight's pause: its time scale can be left low or at 0 after a death
            accumulator += gm.gameState == GlobalEnums.GameState.PAUSED ? 0f : Time.unscaledDeltaTime;
            int frames = 0;
            profWatch.Restart();
            while (accumulator >= Frame && frames < 4) {
                accumulator -= Frame;
                frames++;
                // a GameCube controller on the adapter plays natively; otherwise the gamepad/keyboard mapping
                var gc = FoxMod.Instance.GameCube?.Melee();
                var pad = gc == null ? ReadPad() : default;
                var hkc = gc is McInput g ? HkControls.FromGameCube(g) : HkControls.FromPad(pad);
                if (GcInput.InventoryOpen) hkc = default;   // the D-pad navigates the inventory: no interact or Focus
                var input = gc is McInput g2 ? HkControls.ForMelee(g2) : InputMapper.Map(pad);
                if (gc is McInput g3) {   // D-pad up: a tap is Melee's taunt, a hold Hollow Knight's Dream Nail
                    var up = dpadUp.Update((g3.Buttons & GcButtons.DUp) != 0, Frame);
                    if (up.Taunt) input.Buttons |= GcButtons.DUp;
                    if (up.DreamNail && fox.Airborne == 0) { DreamNail(hero); return; }
                }
                if (hkc.Interact && !interactHeld && fox.Airborne == 0) { interactHeld = true; Interact(hero); return; }
                interactHeld = hkc.Interact;
                // the quick map (GameCube: D-pad right held; keyboard: Tab): Hollow Knight opens it only for a Knight it
                // controls (HeroController.CanQuickMap), so it gets him back as for an interact
                bool map = GcInput.QuickMapHeld || (gc == null && Input.GetKey(KeyCode.Tab));
                if (map && !mapHeld && fox.Airborne == 0) { mapHeld = true; QuickMap(hero); return; }
                mapHeld = map;
                if (Focus(hero, hkc.Focus)) input = new McInput();   // Fox stands still while focusing
                if (GcInput.InventoryOpen || GcInput.QuickMapHeld) input = new McInput();   // the controller is Hollow Knight's menu's
                rawInput = input;
                input = respawnHold.Filter(input);
                if (getUp) {                      // back from waking: Melee's get-up (stick up from DownWait) unless the
                    bool lying = fox.Action >= ActionDownFirst && fox.Action <= ActionDownLast;   // player picks
                    bool neutral = input.Buttons == 0 && Math.Abs((int)input.StickX) < 23 && Math.Abs((int)input.StickY) < 23;
                    if (!lying || !neutral) getUp = false;   // a roll, a get-up attack: Melee's own options
                    else if (fox.Action == ActionDownWaitU || fox.Action == ActionDownWaitD) { input = new McInput { StickY = 127 }; getUp = false; }
                }
                if (!Gate() || !Boosts()) return;
                if (grab != null && !grab.Before(core, room, (Vector2)hero.transform.position - new Vector2(0, HeroFeetOffset(hero)))) { Fail("stand-in: " + core.Error); return; }
                if (combat != null) combat.Held = grab?.Held;
                core.SetInput(0, input);
                int at = 0;
                bool pause = combat != null || enemies != null;
                if (pause) {
                    at = core.StepToHits();   // Melee's hit resolution, mid-frame: hits land this frame
                    if (at < 0) { Fail("libmelee_core stopped: " + core.Error); return; }
                    var bt = profWatch.Elapsed.TotalMilliseconds;
                    if (at == 0 && combat != null && !combat.Frame(core, room)) { Fail("reading Fox's hitboxes: " + core.Error); return; }
                    profBridgeMs += profWatch.Elapsed.TotalMilliseconds - bt;
                    if (at == 0 && combat != null && enemies != null) foreach (var go in combat.Struck) enemies.Guard(go);
                    if (at == 0 && enemies != null && !enemies.Frame(core, room, hero.transform.position)) { Fail("applying a hit to Fox: " + core.Error); return; }
                }
                // StepToHits returning 1 means that frame already ran to its end (no hit resolution for port 0). With
                // DrawOffThread (and Melee's graphics) the frame runs to Melee's drawing here; GxRenderer.Draw finishes it
                // on the core's thread, then decodes it there, while Unity goes on. A next frame finishes it first.
                bool split = gx != null && FoxMod.Instance.Settings.DrawOffThread;
                int ended = !pause || at == 0 ? (split ? core.StepToRender() : core.Step() ? 1 : -1) : 1;
                if (ended < 0) { Fail("libmelee_core stopped: " + core.Error); return; }
                session.Stepped();
                var st = profWatch.Elapsed.TotalMilliseconds;
                sfx?.Frame(core);
                profSfxMs += profWatch.Elapsed.TotalMilliseconds - st;
                st = profWatch.Elapsed.TotalMilliseconds;
                if (combat != null && !combat.AfterFrame(core)) { Fail("reading Fox: " + core.Error); return; }
                launcher?.Frame();
                profBridgeMs += profWatch.Elapsed.TotalMilliseconds - st;
                enemies?.AfterFrame(hero);
                if (grab != null && !grab.After(core, room)) { Fail("stand-in: " + core.Error); return; }
                if (!core.ReadFighter(0, out fox)) { Fail("cannot read Fox: " + core.Error); return; }
                if (pogo != null && !pogo.Frame(core, room, fox)) { Fail("pogo: " + core.Error); return; }
                DiveFloors(hero);
            }
            if (accumulator > Frame) accumulator = 0;   // a hitch: drop time rather than run fast
            profMeleeMs += profWatch.Elapsed.TotalMilliseconds; profMeleeFrames += frames;
            Profile();
            WatchStuck(frames);
            // Fox's percent follows his masks (EnemyHits.PercentFor): held there between hits, down again with a heal
            var pd = PlayerData.instance;
            float percent = (float)EnemyHits.PercentFor(FoxMod.Instance.Settings.EnemyHits ?? new EnemyHitTuning(), pd.GetInt("health"), pd.GetInt("maxHealth"));
            if (Math.Abs(fox.Percent - percent) > 0.01f && !core.SetPercent(percent)) { Fail("mc_set_percent: " + core.Error); return; }
            if (frames > 0 && session.KnightFollowsFox && Unstick()) return;
            if (gx != null) gx.Glow = focusing;
            if (!session.KnightFollowsFox) return;   // Melee has not placed Fox yet: he is still where he died
            var hk = room.ToHk(new V2(fox.X, fox.Y));
            hero.transform.position = new Vector3((float)hk.X, (float)hk.Y + HeroFeetOffset(hero), hero.transform.position.z);
            if (frames > 0 && outOfRoom.Frame(hk.X, hk.Y, gm.sceneWidth, gm.sceneHeight)) {
                Log($"out of the room at ({hk.X:F1}, {hk.Y:F1}): pit");
                hero.TakeDamage(null, GlobalEnums.CollisionSide.top, 1, 5);   // Hollow Knight's pit (hazard type 5)
            }
            // Melee's own frame (Fox, items, effects as Melee draws them); fox.model and the stand-in shapes otherwise
            if (gx != null && Input.GetKeyDown(KeyCode.F9)) Log("frame dumped: " + gx.Dump(Path.Combine(FoxMod.ModDir, "dump", DateTime.Now.ToString("HHmmss"))));
            bool fresh = frames > 0 || room != gxRoom || !(gx?.Shown ?? false);   // a new Melee frame (or room) to decode
            gxRoom = room;
            if (gx != null && gx.Draw(core, room, hero.GetComponent<MeshRenderer>(), hero.transform.position.z, fresh)) {
                model?.Hide(); effects?.Hide();
                if (placeholder) placeholder.SetActive(false);
                return;
            }
            if (model != null && !model.Draw(core, bones, room)) {
                FoxMod.Instance.LogError("Fox's skeleton does not match fox.model; drawing the placeholder");
                model.Hide();
                model = null;
            }
            if (model == null) DrawPlaceholder(hk);
            effects?.Draw(core, fox, room);
        }

        // Hollow Knight's death: Melee's KO (the blast and Fox's cry) where Fox stands, played out for KoFrames while Hollow
        // Knight runs its death; the next placement (the respawn) shows him again.
        const int KoFrames = 60, StarKoOneIn = 4;
        readonly System.Random koRandom = new System.Random();
        readonly DeathScreen deathScreen = new DeathScreen();
        readonly OutOfRoom outOfRoom = new OutOfRoom();
        int koLeft = -1;                          // Melee frames of the KO left to show; -1: none started this death
        bool Ko(HeroController hero, GameManager gm, HeroFlags f) {
            if (!f.Dead) { koLeft = -1; deathScreen.End(); return false; }
            deathScreen.Frame(Log);
            if (koLeft < 0) {
                bool star = koRandom.Next(StarKoOneIn) == 0;   // now and then the star KO's scream, as Melee's KOs vary
                if (state != State.Running || room == null || !core.Ko(0, star)) { koLeft = 0; return false; }
                deathScreen.Begin(hero.transform.position);   // only for a character's KO: the Knight's own death stays his
                if (star) Log("KO (star)");
                koLeft = KoFrames;
                accumulator = 0;
                combat?.ReleaseAll(); grab?.Release();
                Log("KO");
            }
            if (koLeft == 0) return false;
            accumulator += Time.unscaledDeltaTime;
            int frames = 0;
            while (accumulator >= Frame && koLeft > 0 && frames < 4) {
                accumulator -= Frame;
                frames++; koLeft--;
                core.SetInput(0, new McInput());
                if (!core.Step()) { Fail("libmelee_core stopped: " + core.Error); return true; }
                sfx?.Frame(core);
            }
            if (koLeft == 0) { gx?.Hide(); return false; }
            bool fresh = frames > 0 || !(gx?.Shown ?? false);
            if (gx == null || !gx.Draw(core, room, hero.GetComponent<MeshRenderer>(), hero.transform.position.z, fresh)) koLeft = 0;
            model?.Hide(); effects?.Hide();
            if (placeholder) placeholder.SetActive(false);
            return true;
        }

        // D-pad left/right: Hollow Knight gets the Knight back and presses "up" for him; FoxSession keeps him there
        // while a tablet, NPC or bench has him, then Fox carries on from where he stands.
        void Interact(HeroController hero) {
            session.BeginInteract();
            hero.RegainControl();
            hero.AcceptInput();
            hero.cState.onGround = true;
            int heard = PressUp(hero);
            Log($"interact ({heard} listening)");
        }

        void QuickMap(HeroController hero) {
            mapping = true;
            session.BeginInteract();
            session.HoldHollow(MapHold);   // renewed while held (QuickMapHeld); the map button comes 0.1 s after (DPadRight.MapButtonDelay)
            hero.RegainControl();
            hero.AcceptInput();
            hero.cState.onGround = true;
            Log($"quick map (can open: {hero.CanQuickMap()})");
        }

        // each Update while Hollow Knight has the Knight for the quick map: held, keep it (Hollow Knight opens the map only if
        // its button is still down a moment after the press, and closes it when it's let go or the Knight isn't its)
        bool mapping;
        const float MapHold = 0.15f;   // seconds the Knight stays Hollow Knight's after the last held frame
        void QuickMapHeld() {
            if (!mapping) return;
            if (GcInput.QuickMapHeld || Input.GetKey(KeyCode.Tab)) session.HoldHollow(MapHold);
            else mapping = false;
        }

        // D-pad up held: Hollow Knight's Dream Nail (the Dreamers, the Seer, dream bosses: a Melee character can't swing it). The
        // Knight is handed over, as for an interact, and kept while D-pad up is held and a little after (the slash finishing);
        // its Dream Nail button (Action4) is pressed from DPadUp.ButtonDelay on.
        readonly DPadUp dpadUp = new DPadUp();
        bool dreaming;
        void DreamNail(HeroController hero) {
            dreaming = true;
            session.HoldHollow(DreamNailAfter);
            hero.RegainControl();
            hero.AcceptInput();
            hero.cState.onGround = true;
            var rb = hero.GetComponent<Rigidbody2D>();
            if (rb != null) rb.velocity = Vector2.zero;
            Log($"dream nail (can: {hero.CanDreamNail()})");
        }
        const float DreamNailAfter = 0.6f;   // seconds Hollow Knight keeps the Knight after D-pad up is let go

        // each Update while Hollow Knight has the Knight for the Dream Nail: held, keep it and press the button
        void DreamNailHeld() {
            if (!dreaming) return;
            var gc = FoxMod.Instance.GameCube?.Current();
            var s = dpadUp.Update(((gc?.Buttons ?? 0) & GcButtons.DUp) != 0, Time.unscaledDeltaTime);
            GcInput.DreamNailButton = s.DreamNailButton;
            if (s.DreamNail) session.HoldHollow(DreamNailAfter);
            else { dreaming = false; GcInput.DreamNailButton = false; }
        }

        // What Hollow Knight's ListenForUp does when "up" is pressed: send its wasPressed event, for every state machine
        // listening right now (a tablet, an NPC or a bench the Knight stands at). No input device is involved, so
        // Hollow Knight's own controller (and keyboard prompts) stay as they are. A pickup the Knight couldn't reach yet with
        // his abilities (GateLogic.ItemOpen: a Melee character's recovery crosses gaps he dashes over) isn't taken.
        int PressUp(HeroController hero) {
            int n = 0;
            foreach (var fsm in new List<PlayMakerFSM>(PlayMakerFSM.FsmList)) {
                if (fsm == null || fsm.gameObject == hero.gameObject || fsm.Fsm?.ActiveState == null) continue;
                foreach (var a in fsm.Fsm.ActiveState.Actions)
                    if (a is HutongGames.PlayMaker.Actions.ListenForUp up && up.Enabled && up.wasPressed != null) {
                        if (!PickupOpen(fsm.gameObject)) break;
                        fsm.Fsm.Event(up.wasPressed);
                        n++;
                        break;
                    }
            }
            return n;
        }

        // false for a shiny (its "Shiny Control" state machine, on the listener, above or below it) whose PlayerData bool the
        // ability gates hold back from where you came in
        bool PickupOpen(GameObject listener) {
            if (gateLogic == null || !FoxMod.Instance.Settings.AbilityGates) return true;
            PlayMakerFSM shiny = null;
            for (var t = listener.transform; t != null && shiny == null; t = t.parent) shiny = ShinyControl(t.gameObject);
            if (shiny == null) foreach (var f in listener.GetComponentsInChildren<PlayMakerFSM>(true)) if (f.FsmName == "Shiny Control") { shiny = f; break; }
            var flag = shiny?.FsmVariables.FindFsmString("PD Bool Name")?.Value;
            if (string.IsNullOrEmpty(flag)) return true;
            var scene = GameManager.instance.sceneName;
            var entry = roomEntry;   // the door this room's gates were worked out from (a hazard respawn blanks Hollow Knight's)
            if (gateLogic.ItemOpen(scene, entry, Abilities(), flag)) return true;
            Log($"item: {flag} ({GateLogic.LocationOf(flag)}) in {scene} not taken, came in by {entry}: the Knight needs an ability you don't have yet to reach it");
            return false;
        }

        static PlayMakerFSM ShinyControl(GameObject o) {
            foreach (var f in o.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Shiny Control") return f;
            return null;
        }

        // D-pad down: Hollow Knight's Focus while Fox stands (spec §5.3). True while focusing.
        bool Focus(HeroController hero, bool held) {
            var pd = PlayerData.instance;
            bool still = fox.Airborne == 0 && (fox.Action == ActionWait || focusing);
            var step = focus.Tick(held, still, pd.GetInt("MPCharge"), pd.GetInt("health"), pd.GetInt("maxHealth"));
            if (held && !focusHeld && !step.Focusing)
                Log("focus: " + (FocusRules.WhyNot(still, pd.GetInt("MPCharge"), focus.Cost, pd.GetInt("health"), pd.GetInt("maxHealth")) ?? "not started"));
            focusHeld = held;
            // drained in chunks of 3 through HeroController.TakeMP, the path Hollow Knight's own spending takes (it tells
            // the soul orb, "MP LOSE", only for more than 1 at a time)
            focusDrain += step.Drain;
            if (focusDrain >= 3 || (focusDrain > 0 && (step.Heal || !step.Focusing))) {
                if (focusDrain > 1) hero.TakeMP(focusDrain);
                else { pd.TakeMP(focusDrain); GameCameras.instance.soulOrbFSM.SendEvent("MP SET"); }
                focusDrain = 0;
            }
            if (step.Heal) { hero.AddHealth(focusMasks); focusSound.Heal(); }   // Focus heals (Deep Focus: two masks); percent follows the masks
            focusSound.Charging(step.Focusing);
            focusEffects.Focusing(hero, step.Focusing);
            if (step.Heal) focusEffects.Healed(hero);
            focusing = step.Focusing;
            return focusing;
        }

        // the moves the core locks (MoveGates): wall jump until Mantis Claw, Zelda's and Sheik's transform; set when it changes
        uint gatesSet = uint.MaxValue;
        Character character = Roster.Fox;
        bool Gate() {
            uint locked = MoveGates.ForCharacter(character, PlayerData.instance.GetBool("hasWalljump"));
            if (locked == gatesSet) return true;
            if (!core.SetGates(0, locked)) { Fail("mc_set_gates: " + core.Error); return false; }
            gatesSet = locked;
            Log($"locked moves: {locked}");
            return true;
        }

        bool Boot() {
            var s = FoxMod.Instance.Settings;
            Prompts.Hook();
            PauseReset.Hook();
            if (!hooked) {
                hooked = true;
                On.HeroController.Respawn += (orig, self) => Track(orig(self));
                On.HeroController.HazardRespawn += (orig, self) => Track(orig(self));
            }
            string dir = FoxMod.ModDir, so = Path.Combine(dir, Environment.OSVersion.Platform == PlatformID.Win32NT ? "libmelee_core.dll" : "libmelee_core.so"), mem1 = Path.Combine(dir, "mem1.bin");
            if (string.IsNullOrEmpty(s.IsoPath) || !File.Exists(s.IsoPath)) { Fail($"set IsoPath in FoxMod.GlobalSettings.json to your Melee NTSC 1.02 ISO (now: '{s.IsoPath}')"); return false; }
            try { core = MeleeCore.Load(so); } catch (MeleeCoreException e) { Fail(e.Message); return false; }
            character = Roster.Find(s.Character, out bool known);
            if (!known) FoxMod.Instance.LogWarn($"Character \"{s.Character}\" is not a Melee character: playing Fox");
            bool grabs = s.Grabs?.Enabled ?? false;
            if (!StartCore(mem1, character)) return false;
            if (s.ShowModel) {
                var path = Path.Combine(dir, "fox.model");
                try { model = FoxRenderer.Create(FoxModel.Load(path)); Log("drawing Fox's model"); }
                catch (Exception e) { Log($"fox.model not used ({e.Message}); drawing the placeholder"); }
            }
            if (s.FoxHits) combat = new CombatBridge { Character = character };
            if (combat != null && (s.Knockback?.Enabled ?? false)) { launcher = new EnemyLauncher(); combat.Launcher = launcher; combat.Knockback = s.Knockback; }
            if (s.EnemiesHitFox) enemies = new EnemyBridge(s.EnemyHits ?? new EnemyHitTuning(), () => holdingBody && state == State.Running);
            if (grabs) grab = new GrabBridge(s.Grabs, enemies) { Launcher = launcher };
            if (s.Pogo?.Enabled ?? false) pogo = new PogoBridge(s.Pogo, () => holdingBody && state == State.Running);
            try { effects = new EffectsRenderer(); } catch (Exception e) { Log("effects not drawn: " + e.Message); }
            if (s.MeleeGraphics) {
                try {
                    if (!core.GxCapture(true)) throw new Exception(core.Error);
                    gx = new GxRenderer();
                    Log("drawing Melee's own graphics");
                } catch (Exception e) { Log("Melee's graphics not drawn (" + e.Message + "): fox.model instead"); gx = null; }
            }
            try { sfx = SfxPlayer.Create(core, character, Log); } catch (Exception e) { Log("Melee sounds off: " + e.Message); }
            focus = new FocusRules(Math.Max(1, (int)Math.Round(s.FocusSeconds * 60)), 33);
            state = State.Running;
            Log("libmelee_core running");
            return true;
        }

        /// A new Melee match with `c` (Melee's own countdown run through), as at boot and on a character change.
        bool StartCore(string mem1, Character c) {
            var why = StartCoreWork(mem1, c);
            if (why != null) { Fail(why); return false; }
            Log("character: " + c.Name);
            return true;
        }

        /// StartCore's work, touching only the core (safe off Unity's thread): null, or why it failed.
        string StartCoreWork(string mem1, Character c) {
            var s = FoxMod.Instance.Settings;
            bool grabs = s.Grabs?.Enabled ?? false;
            if (!core.Init(mem1, s.IsoPath)) return "mc_init: " + core.Error;
            if (!core.StartMatch(c.MeleeId, grabs)) return "mc_start_match: " + core.Error;
            core.SetInput(0, new McInput());
            if (grabs) core.SetInput(GrabBridge.Port, new McInput());
            for (int p = grabs ? 2 : 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < CountdownFrames; i++) if (!core.Step()) return "countdown: " + core.Error;
            if (!core.SetEdgeAssist(s.EdgeAssist)) return "mc_set_edge_assist: " + core.Error;
            if (grabs && !core.SetStandIn(GrabBridge.Port)) return "mc_standin_set: " + core.Error;
            // the attributes the boost charms change, as this character's match starts
            if (!core.GetAttribute(0, AttrDash, out float dash) || !core.GetAttribute(0, AttrRun, out float run) || !core.GetAttribute(0, AttrWeight, out float weight))
                return "mc_get_attribute: " + core.Error;
            fighterBase = new FighterBase(dash, run, weight);
            boostsSet = -1;
            if (!core.GetAttribute(0, AttrFastFall, out fastFallSpeed)) return "mc_get_attribute: " + core.Error;
            return null;
        }
        const int CountdownFrames = 130;   // Melee's "Ready... GO" is 123 frames; the character acts after it

        // ---- the boost charms (BoostCharms): set when what's equipped changes ----
        const uint AttrDash = 0x1C, AttrRun = 0x28, AttrWeight = 0x88, AttrFastFall = 0x74;
        FighterBase fighterBase;
        int boostsSet = -1, focusSet = -1;
        bool Boosts() {
            var pd = PlayerData.instance;
            bool sprint = pd.equippedCharm_37, steady = pd.equippedCharm_14, deep = pd.equippedCharm_34;
            int key = (sprint ? 1 : 0) | (steady ? 2 : 0);
            if (key != boostsSet) {
                var (dash, run, weight) = BoostCharms.Attributes(fighterBase, sprint, steady);
                if (!core.SetAttribute(0, AttrDash, dash) || !core.SetAttribute(0, AttrRun, run) || !core.SetAttribute(0, AttrWeight, weight)) { Fail("mc_set_attribute: " + core.Error); return false; }
                boostsSet = key;
                Log($"boost charms: Sprintmaster {sprint}, Steady Body {steady}");
            }
            if ((deep ? 1 : 0) != focusSet) {
                focusSet = deep ? 1 : 0;
                var (frames, masks) = BoostCharms.Focus(Math.Max(1, (int)Math.Round(FoxMod.Instance.Settings.FocusSeconds * 60)), deep);
                focus = new FocusRules(frames, 33);
                focusMasks = masks;
            }
            return true;
        }
        int focusMasks = 1;

        // ---- choosing a character at a bench: D-pad up (keyboard T) opens Melee's character select (CharacterSelect); stick or
        // D-pad moves, A (keyboard J or Enter) plays the character, B (keyboard K or Escape) closes. While it is open the
        // controller is the screen's: Hollow Knight sees it idle, until every button is let go after it closes. ----
        CharacterGrid grid;
        CharacterSelect select;
        System.Collections.Generic.HashSet<Character> ownedNow;
        bool selUp, selA, selB;
        int repeatDx, repeatDy; float repeatAt;

        void BenchMenu(HeroController hero) {
            var gc = FoxMod.Instance.GameCube?.Current();
            var b = gc?.Buttons ?? 0;
            int sx = gc?.StickX ?? 0, sy = gc?.StickY ?? 0;
            bool up = (b & GcButtons.DUp) != 0 || Input.GetKey(KeyCode.T);
            bool a = (b & GcButtons.A) != 0 || Input.GetKey(KeyCode.J) || Input.GetKey(KeyCode.Return);
            bool bk = (b & GcButtons.B) != 0 || Input.GetKey(KeyCode.K) || Input.GetKey(KeyCode.Escape);
            bool Pressed(bool now, ref bool was) { bool p = now && !was; was = now; return p; }
            bool pUp = Pressed(up, ref selUp), pA = Pressed(a, ref selA), pB = Pressed(bk, ref selB);
            if (grid == null) {
                if (GcInput.HoldFromHollow && b == 0 && Math.Abs(sx) < 30 && Math.Abs(sy) < 30) GcInput.HoldFromHollow = false;
                if (!pUp) return;
                var data = PlayerData.instance;
                ownedNow = new System.Collections.Generic.HashSet<Character>(Roster.Owned(n => data.GetBool("gotCharm_" + n),
                    FoxMod.Instance.Save.MarthBought, FoxMod.Instance.Settings.UnlockAllCharacters));
                grid = new CharacterGrid(character);
                select ??= new CharacterSelect(Log);
                GcInput.HoldFromHollow = true;
                repeatDx = repeatDy = 0;
                return;
            }
            int dx = ((b & GcButtons.DRight) != 0 || sx > 64 || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - ((b & GcButtons.DLeft) != 0 || sx < -64 || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            int dy = ((b & GcButtons.DDown) != 0 || sy < -64 || Input.GetKey(KeyCode.DownArrow) ? 1 : 0) - (up || sy > 64 || Input.GetKey(KeyCode.UpArrow) ? 1 : 0);
            if (dx != repeatDx || dy != repeatDy) { repeatDx = dx; repeatDy = dy; repeatAt = Time.unscaledTime + 0.35f; grid.Move(dx, dy); }
            else if ((dx != 0 || dy != 0) && Time.unscaledTime >= repeatAt) { repeatAt = Time.unscaledTime + 0.12f; grid.Move(dx, dy); }
            if (pB) { grid = null; return; }
            if (pA && ownedNow.Contains(grid.Current)) {
                var pick = grid.Current;
                grid = null;
                if (pick != character) SwitchCharacter(hero, pick);
            }
        }

        void OnGUI() {
            if (switching != null && select != null) select.DrawLoading(switchingTo);
            else if (grid != null && select != null) select.Draw(grid, c => ownedNow.Contains(c));
        }

        // A character change restarts the core off Unity's thread (about a second); meanwhile nothing else uses the core and
        // the select screen says it is loading. FinishSwitch then sets the new character up where the Knight sits.
        System.Threading.Tasks.Task<string> switching;
        Character switchingTo;
        float switchStarted;
        const float SwitchTimeout = 20f;   // seconds: a core restart takes about one

        void SwitchCharacter(HeroController hero, Character c) {
            if (switching != null) return;
            Log($"switching to {c.Name}");
            combat?.ReleaseAll(); grab?.Release(); launcher?.StopAll();
            sfx?.Destroy(); sfx = null;
            switchingTo = c;
            var mem1 = Path.Combine(FoxMod.ModDir, "mem1.bin");
            switchStarted = Time.unscaledTime;
            switching = System.Threading.Tasks.Task.Run(() => StartCoreWork(mem1, c));
        }

        /// While a switch runs: false (the core is busy). Once it has finished, the new character is set up.
        bool FinishSwitch(HeroController hero) {
            if (switching == null) return true;
            if (!switching.IsCompleted) return false;
            var why = switching.IsFaulted ? switching.Exception?.GetBaseException().Message : switching.Result;
            switching = null;
            var c = switchingTo;
            if (why != null) { Fail(why); return false; }
            Log("character: " + c.Name);
            character = c;
            FoxMod.Instance.Settings.Character = c.Name;   // remembered once it plays
            try { FoxMod.Instance.SaveSettings(); } catch (Exception e) { Log("settings not saved: " + e.Message); }
            if (combat != null) combat.Character = c;
            gatesSet = uint.MaxValue;
            if (gx != null) { if (!core.GxCapture(true)) { Fail("mc_gx_capture: " + core.Error); return false; } gx.Reset(); }
            try { sfx = SfxPlayer.Create(core, c, Log); } catch (Exception e) { Log("Melee sounds off: " + e.Message); }
            // the new match is on Melee's stage: this room's collision again, and the character where the Knight sits
            session.ForgetRoom();
            var gm = GameManager.instance;
            if (!EnterRoom(gm.sceneName) || !WatchTerrain(gm.sceneName)) return false;
            needPlace = !PlaceAtKnight(hero);
            if (!needPlace) session.Placed();
            return true;
        }

        /// Converts the scene's terrain into the core's collision. A room that cannot be converted is played
        /// as the Knight (FoxSession gives him back) until the next room converts.
        bool EnterRoom(string scene) {
            terrainAt.Clear();
            acidSeen.Clear();
            terrain = FindTerrain();
            terrainSet.Reset();
            terrainSet.Changed(Ids(terrain));
            var outlines = Outlines(terrain);
            if (outlines.Count == 0) { RoomFailed(scene, "no terrain colliders"); return true; }
            ComputeGates(scene);
            outlines.AddRange(gateWalls);
            quakeFloors.Clear();
            foreach (var fsm in FindObjectsOfType<PlayMakerFSM>()) if (fsm.FsmName == "quake_floor") quakeFloors.Add(fsm);
            var (min, max) = RoomGeometry.Bounds(outlines);
            var s = FoxMod.Instance.Settings;
            var t = RoomTransform.ForRoom(outlines, s.WorldScale);   // the rule RoomLab uses too
            var shapes = RoomGeometry.Build(outlines, t);
            if (!core.BuildCollision(shapes, out var col)) { RoomFailed(scene, core.Error); return true; }
            if (!core.LoadCollision(col)) { Fail($"{scene}: mc_load_collision: {core.Error}"); return false; }
            roomShapes = shapes;
            stuck.Reset();
            var lo = t.ToMelee(min); var hi = t.ToMelee(max);
            float m = (float)s.CameraMargin;
            var cam = new McBox { Left = (float)lo.X - m, Right = (float)hi.X + m, Top = (float)hi.Y + m, Bottom = (float)lo.Y - m };
            var blast = new McBox { Left = cam.Left - FarBlast, Right = cam.Right + FarBlast, Top = cam.Top + FarBlast, Bottom = cam.Bottom - FarBlast };
            if (!core.SetBounds(blast, cam)) { Fail($"{scene}: mc_set_bounds: {core.Error}"); return false; }
            room = t;
            combat?.ReleaseAll(); grab?.NewRoom();
            session.RoomConverted(scene, true);
            needPlace = true;
            Log($"{scene}: {shapes.Count} shapes from {outlines.Count} colliders");
            return true;
        }

        // ---- Desolate Dive's floors: Hollow Knight breaks them only for the Knight's dive (their quake_floor state machine waits
        // for QUAKE FALL START). With Desolate Dive owned, a character landing from a fast fall onto one breaks it the same way:
        // the events the dive sends, then the terrain watcher drops the floor from Melee's collision. ----
        float fastFallSpeed;
        bool slamming;
        readonly List<PlayMakerFSM> quakeFloors = new List<PlayMakerFSM>();   // this room's, found as it is converted
        uint lastAirborne;
        void DiveFloors(HeroController hero) {
            bool landed = lastAirborne != 0 && fox.Airborne == 0;
            lastAirborne = fox.Airborne;
            if (fox.Airborne != 0) {
                if (fastFallSpeed > 0 && fox.SelfVelY <= -0.95f * fastFallSpeed) slamming = true;
                return;
            }
            if (!landed || !slamming) { slamming = false; return; }
            slamming = false;
            if (PlayerData.instance.quakeLevel <= 0) return;
            var feet = (Vector2)hero.transform.position - new Vector2(0, HeroFeetOffset(hero));
            var probe = new Bounds(feet + new Vector2(0, -0.3f), new Vector3(1.2f, 0.8f, 1f));
            foreach (var fsm in quakeFloors) {
                if (fsm == null || fsm.FsmName != "quake_floor") continue;
                var col = fsm.GetComponent<Collider2D>();
                if (col == null || !col.enabled || !col.bounds.Intersects(probe)) continue;
                Log($"dive floor {fsm.gameObject.name} broken by a fast fall (Desolate Dive owned)");
                fsm.SendEvent("QUAKE FALL START");
                fsm.SendEvent("DESTROY");
            }
        }

        // ---- Hollow Knight's ability gates (GateLogic, spec §4): a door the Knight couldn't reach from the one you came in by,
        // with the abilities you have, is closed: a wall across an exit, a house door's trigger off. Setting AbilityGates. ----
        GateLogic gateLogic;
        bool gateLogicTried;
        readonly List<ColliderOutline> gateWalls = new List<ColliderOutline>();
        readonly List<Collider2D> gateDoorsOff = new List<Collider2D>();
        int gatesKey = -1;
        string roomEntry = "";

        static KnightAbilities Abilities() {
            var pd = PlayerData.instance;
            return new KnightAbilities {
                Claw = pd.hasWalljump, Wings = pd.hasDoubleJump, Dash = pd.hasDash, SuperDash = pd.hasSuperDash, Acid = pd.hasAcidArmour,
                ShadeCloak = pd.hasShadowDash, Fireball = pd.fireballLevel > 0, Quake = pd.quakeLevel > 0, Scream = pd.screamLevel > 0,
                DreamNail = pd.hasDreamNail, Lantern = pd.hasLantern,
            };
        }

        static int AbilitiesKey(KnightAbilities a) =>
            (a.Claw ? 1 : 0) | (a.Wings ? 2 : 0) | (a.Dash ? 4 : 0) | (a.SuperDash ? 8 : 0) | (a.Acid ? 16 : 0) | (a.ShadeCloak ? 32 : 0)
            | (a.Fireball ? 64 : 0) | (a.Quake ? 128 : 0) | (a.Scream ? 256 : 0) | (a.DreamNail ? 512 : 0) | (a.Lantern ? 1024 : 0);

        void ComputeGates(string scene) {
            gateWalls.Clear();
            foreach (var c in gateDoorsOff) if (c != null) c.enabled = true;
            gateDoorsOff.Clear();
            gatesKey = -1;
            if (!FoxMod.Instance.Settings.AbilityGates) return;
            if (!gateLogicTried) {
                gateLogicTried = true;
                try { gateLogic = GateLogic.Load(); } catch (Exception e) { FoxMod.Instance.LogWarn("ability gates off: " + e.Message); }
            }
            if (gateLogic == null) return;
            var a = Abilities();
            gatesKey = AbilitiesKey(a);
            var entry = roomEntry = GameManager.instance.entryGateName ?? "";
            var blocked = gateLogic.Blocked(scene, entry, a);
            if (blocked.Count == 0) return;
            var closed = new List<string>();
            foreach (var tp in FindObjectsOfType<TransitionPoint>()) {
                var name = tp.gameObject.name;
                if (!blocked.Contains(name)) continue;
                var col = tp.GetComponent<Collider2D>();
                if (col == null) continue;
                if (name.StartsWith("door")) { col.enabled = false; gateDoorsOff.Add(col); }   // entered by pressing up: no wall
                else {
                    var b = col.bounds;
                    gateWalls.Add(new ColliderOutline { Points = new[] {
                        new V2(b.min.x, b.min.y), new V2(b.max.x, b.min.y), new V2(b.max.x, b.max.y), new V2(b.min.x, b.max.y) } });
                }
                closed.Add(name);
                Log($"gate: {scene}[{name}] closed, came in by {(entry == "" ? "no door" : entry)}; needs: {gateLogic.LogicOf(scene, name)}");
            }
            if (closed.Count > 0) Log($"gates in {scene}: {string.Join(", ", closed)} closed (the way you came is always open)");
        }

        static bool LiveTerrain(Collider2D c) =>
            c != null && c.enabled && !c.isTrigger && c.gameObject.layer == TerrainLayer && c.gameObject.activeInHierarchy;

        List<Collider2D> FindTerrain() {
            var l = new List<Collider2D>();
            foreach (var c in FindObjectsOfType<Collider2D>()) if (Ground(c)) l.Add(c);
            return l;
        }

        // what a Melee character stands on and runs into: terrain, a Shade Cloak gate's barrier without Shade Cloak, an acid
        // pool's surface with Isma's Tear
        bool Ground(Collider2D c) {
            if (LiveTerrain(c)) return true;
            var pd = PlayerData.instance;
            return pd != null && ((!pd.hasShadowDash && ShadeGate(c)) || (pd.hasAcidArmour && IsAcid(c)));
        }

        // AcidSurface, asked once per collider (it reads names and components, which allocate); emptied with each room
        readonly Dictionary<Collider2D, bool> acidSeen = new Dictionary<Collider2D, bool>();
        bool IsAcid(Collider2D c) {
            if (c == null || !(c is BoxCollider2D)) return false;
            if (!acidSeen.TryGetValue(c, out var acid)) acidSeen[c] = acid = AcidSurface(c);
            return acid && c.gameObject.activeInHierarchy;
        }

        // an acid pool's swim region (Isma's Tear: the Knight swims across the top; a Melee character can't swim, so with Isma's
        // Tear the pool is solid to its surface and the character runs over it; without, the acid is Hollow Knight's hazard as
        // for the Knight). Read from the game's data: the pool's "Surface Water Region" trigger box carries an "Acid Armour
        // Check" state machine, beside its "Acid Box" hazard which that check turns off.
        static bool AcidSurface(Collider2D c) {
            if (c.gameObject.name != "Surface Water Region") return false;
            foreach (var f in c.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Acid Armour Check") return true;
            return false;
        }

        // a Shade Cloak gate's barrier: its "solid" box is on the Knight's own layer (13), not terrain, so without this a Melee
        // character would walk through; without Shade Cloak it is a wall, with it the character passes (as the Knight's dash)
        static bool ShadeGate(Collider2D c) =>
            c != null && c.enabled && c.gameObject.activeInHierarchy && c.gameObject.name == "solid"
            && c.transform.parent != null && c.transform.parent.name.StartsWith("shadow_gate");

        static List<int> Ids(List<Collider2D> cs) {
            var ids = new List<int>(cs.Count);
            foreach (var c in cs) ids.Add(c.GetInstanceID());
            return ids;
        }

        static List<ColliderOutline> Outlines(List<Collider2D> cs) {
            var outlines = new List<ColliderOutline>();
            foreach (var c in cs) AddOutlines(c, outlines);
            return outlines;
        }

        /// Breakable walls, gates and anything else that adds or removes terrain mid-room: a converted collider that
        /// stops being terrain is noticed the same frame, new terrain within ScanFrames. Melee's collision is then
        /// rebuilt with the room's own transform, so nothing moves.
        bool WatchTerrain(string scene) {
            bool gone = false, moved = false;
            foreach (var c in terrain) {
                if (!Ground(c)) { gone = true; break; }
                // moving terrain (falling and bobbing platforms): rebuilt every frame it moves
                var at = (Vector2)c.transform.position;
                if (terrainAt.TryGetValue(c, out var was) && (at - was).sqrMagnitude > 1e-6f) moved = true;
                terrainAt[c] = at;
            }
            if (!gone && !moved && ++scanTimer < ScanFrames) return true;
            scanTimer = 0;
            var now = FindTerrain();
            bool changed = terrainSet.Changed(Ids(now));
            if (!changed && !moved) { terrain = now; return true; }
            terrain = now;
            if (moved && !changed) {
                if (!movingLogged) { Log($"{scene}: terrain moving, collision follows it"); movingLogged = true; }
            } else movingLogged = false;
            var outlines = Outlines(terrain);
            if (outlines.Count == 0) return true;   // keep what Melee has
            outlines.AddRange(gateWalls);
            var shapes = RoomGeometry.Build(outlines, room);
            if (!core.BuildCollision(shapes, out var col)) { FoxMod.Instance.LogWarn($"{scene}: changed terrain not converted ({core.Error}); keeping the old collision"); return true; }
            if (!core.LoadCollision(col)) { Fail($"{scene}: mc_load_collision: {core.Error}"); return false; }
            roomShapes = shapes;
            if (changed) Log($"{scene}: terrain changed, collision rebuilt ({shapes.Count} shapes); Fox at Melee ({fox.X:F1}, {fox.Y:F1}), action {fox.Action}, airborne {fox.Airborne}");
            return true;
        }

        void RoomFailed(string scene, string why) {
            FoxMod.Instance.LogError($"{scene}: room not converted ({why}); play it as the Knight");
            if (session.RoomConverted(scene, false)) ReleaseHero();
            holdingBody = false;
        }

        // The Knight's death effects (shattering on spikes, acid, lava; the death animation) are their own objects:
        // while Fox plays they keep their logic (the death one leaves the shade) but draw nothing. The hazard ones are
        // Hollow Knight's own pooled objects, untouched but for their renderers (copies of them misbehaved: the acid
        // splash's screen-blend shader left the room's picture smeared until the next room).
        bool deathsHidden;
        readonly List<GameObject> spawnedDeaths = new List<GameObject>();
        readonly List<Renderer> deathRenderers = new List<Renderer>();
        HeroController deathsHiddenOn;
        void HideKnightDeaths(HeroController hero, bool hide) {
            // a new Knight (back from the main menu) has its own death object; and its death spawns into it as it plays
            if (hero != deathsHiddenOn || (hide && hero.cState.dead)) { deathsHiddenOn = hero; deathsHidden = !hide; }
            if (hide != deathsHidden) {
                deathsHidden = hide;
                if (hero.heroDeathPrefab != null)
                    foreach (var r in hero.heroDeathPrefab.GetComponentsInChildren<Renderer>(true)) r.forceRenderingOff = hide;
                if (!hide) {
                    foreach (var r in deathRenderers) if (r != null) r.enabled = true;
                    deathRenderers.Clear();
                }
            }
            if (!hide) return;
            foreach (var prefab in new[] { hero.spikeDeathPrefab, hero.acidDeathPrefab, hero.lavaDeathPrefab }) {
                if (prefab == null) continue;
                ObjectPool.GetSpawned(prefab, spawnedDeaths, false);
                foreach (var go in spawnedDeaths)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        if (r.enabled) { r.enabled = false; deathRenderers.Add(r); }
            }
        }

        // Sitting at a bench: Fox crouches where he stood (Melee keeps running with the stick held down), so resting
        // is plain to see; Hollow Knight has the Knight and the controller.
        float restClock;
        void Rest(HeroController hero) {
            if (gx == null) return;
            restClock += Time.unscaledDeltaTime;
            int frames = 0;
            while (restClock >= Frame && frames < 4) {
                restClock -= Frame;
                core.SetInput(0, new McInput { StickY = -127 });
                if (!core.Step()) { Fail("libmelee_core stopped: " + core.Error); return; }
                frames++;
            }
            if (restClock > Frame) restClock = 0;
            if (core.ReadFighter(0, out fox)) gx.Draw(core, room, hero.GetComponent<MeshRenderer>(), hero.transform.position.z, frames > 0);
        }

        // Fox not moving for 3 s while the controller is pushed: log what is in the way (once per standstill)
        float framelessFor; bool framelessLogged;
        void WatchStuck(int frames) {
            if (frames > 0 || GameManager.instance.gameState == GlobalEnums.GameState.PAUSED) { framelessFor = 0; framelessLogged = false; }
            else if ((framelessFor += Time.unscaledDeltaTime) > 2f && !framelessLogged) {
                framelessLogged = true;
                Log($"no Melee frame for 2 s: time scale {Time.timeScale}, game state {GameManager.instance.gameState}, accumulator {accumulator}");
            }
            bool pushed = rawInput.Buttons != 0 || Math.Abs((int)rawInput.StickX) > 40 || Math.Abs((int)rawInput.StickY) > 40;
            bool moved = Math.Abs(fox.X - lastX) > 0.01f || Math.Abs(fox.Y - lastY) > 0.01f;
            lastX = fox.X; lastY = fox.Y;
            if (moved || !pushed) { stillFor = 0; stillFrames = 0; stuckLogged = false; return; }
            stillFor += Time.unscaledDeltaTime; stillFrames += frames;
            if (stillFor < 3f || stuckLogged) return;
            stuckLogged = true;
            Log($"Fox not moving for 3 s: action {fox.Action}, airborne {fox.Airborne}, Melee frames run {stillFrames}, time scale {Time.timeScale}, " +
                $"game state {GameManager.instance.gameState}, respawn hold {respawnHold.Armed}, focusing {focusing}, interacting {session.Interacting}, " +
                $"stick ({rawInput.StickX}, {rawInput.StickY}), buttons {rawInput.Buttons:X4}");
        }

        // Every 5 s while Fox plays: where the time goes (frame rate, Melee's frames, decoding and drawing Melee's frame)
        readonly System.Diagnostics.Stopwatch profWatch = new System.Diagnostics.Stopwatch();
        double profMeleeMs, profBridgeMs, profSfxMs; int profMeleeFrames, profUnityFrames; float profClock, profWorst;
        void Profile() {
            profUnityFrames++;
            profClock += Time.unscaledDeltaTime;
            profWorst = Mathf.Max(profWorst, Time.unscaledDeltaTime);
            if (profClock < 5f) return;
            int n = Math.Max(1, profUnityFrames), m = Math.Max(1, profMeleeFrames);
            string g = gx == null ? "" : $", decode wait {gx.DecodeMs / n:F1} ms, mesh {gx.MeshMs / n:F1} ms, uploads {gx.Uploads} ({gx.UploadBytes / 1048576.0:F1} MB, {gx.UploadMs / n:F1} ms/frame), runs {gx.Runs / n}";
            Log($"perf: {n / profClock:F0} fps (worst frame {profWorst * 1000:F0} ms), Melee {(profMeleeMs - profBridgeMs - profSfxMs) / m:F1} ms/frame, hits {profBridgeMs / m:F1}, sound {profSfxMs / m:F1}{g}");
            profClock = 0; profWorst = 0; profMeleeMs = 0; profBridgeMs = 0; profSfxMs = 0; profMeleeFrames = 0; profUnityFrames = 0;
            if (gx != null) { gx.DecodeMs = gx.MeshMs = gx.UploadMs = 0; gx.Uploads = gx.Runs = 0; gx.UploadBytes = 0; }
        }

        // Waking: Fox, dropped just above where the Knight wakes, lands in a knockdown and lies there (Melee runs with no
        // input) while Hollow Knight plays the Knight's wake-up, hidden; false leaves it all to Hollow Knight.
        bool waking, getUp;
        const uint ActionDownFirst = 183, ActionDownLast = 198;   // DownBoundU .. DownStandD
        const uint ActionDownWaitU = 184, ActionDownWaitD = 192;
        bool Wake(HeroController hero, GameManager gm, HeroFlags f) {
            if (session.NeedsRoom(gm.sceneName) && !EnterRoom(gm.sceneName)) return true;
            if (session.KnightVisible(f) || gx == null) return false;   // no room (or no Melee graphics): the Knight's own
            if (!waking) {
                var feet = (Vector2)hero.transform.position - new Vector2(0, HeroFeetOffset(hero));
                var at = room.ToMelee(new V2(feet.x, feet.y));
                if (!core.TumbleFighter(0, (float)at.X, (float)at.Y + 3f)) { Fail($"mc_tumble_fighter: {core.Error}"); return true; }
                waking = true; needPlace = true; wakeClock = Frame;
                Log($"waking: Fox lies at ({feet.x:F2}, {feet.y:F2}) while the Knight wakes");
            }
            SetKnightVisible(hero, false);
            wakeClock += Time.unscaledDeltaTime;
            int frames = 0;
            while (wakeClock >= Frame && frames < 4) {
                wakeClock -= Frame;
                core.SetInput(0, new McInput());
                if (!core.Step()) { Fail("libmelee_core stopped: " + core.Error); return true; }
                frames++;
            }
            if (wakeClock > Frame) wakeClock = 0;
            if (core.ReadFighter(0, out fox)) gx.Draw(core, room, hero.GetComponent<MeshRenderer>(), hero.transform.position.z, frames > 0);
            return true;
        }
        float wakeClock;

        // What Hollow Knight is doing with the Knight, logged when it changes (deaths, spikes, respawns, loading).
        void LogBusy(HeroFlags f) {
            var b = (f.Dead ? "dead " : "") + (f.HazardDeath ? "hazard-death " : "") + (f.HazardRespawning ? "hazard-respawning " : "")
                  + (f.Respawning ? "respawn-routine " : "") + (f.Loading ? "loading " : "") + (f.Transitioning ? "transitioning " : "");
            if (b == lastBusy) return;
            lastBusy = b;
            Log($"Hollow Knight: {(b.Length == 0 ? "playing" : b.Trim())} (Fox action {fox.Action}, airborne {fox.Airborne})");
        }

        // Fox inside solid ground (StuckGuard): back to where he last stood, falling still, as after spikes
        bool Unstick() {
            const float Middle = 6f;   // Melee units above Fox's feet: inside him, clear of the floor he stands on
            var to = stuck.Frame(StuckGuard.InsideSolid(roomShapes, fox.X, fox.Y + Middle), fox.Airborne == 0, fox.X, fox.Y);
            if (to == null) return false;
            var hk = room.ToHk(new V2(fox.X, fox.Y));
            Log($"Fox stuck inside the terrain at ({hk.X:F2}, {hk.Y:F2}) (Melee ({fox.X:F1}, {fox.Y:F1}), action {fox.Action}): back to Melee ({to.Value.X:F1}, {to.Value.Y:F1})");
            if (!core.RespawnFighter(0, (float)to.Value.X, (float)to.Value.Y)) { Fail($"mc_respawn_fighter: {core.Error}"); return true; }
            session.Placed();
            return true;
        }

        // Fox keeps his action and velocity; he starts where Hollow Knight put the Knight (feet to feet).
        // After spikes or death he comes back still, as the Knight does (no momentum to carry him back in).
        bool PlaceAtKnight(HeroController hero) {
            var feet = (Vector2)hero.transform.position - new Vector2(0, HeroFeetOffset(hero));
            var at = room.ToMelee(new V2(feet.x, feet.y));
            bool respawn = session.TakeRespawns();
            Log($"Fox takes the Knight at ({feet.x:F2}, {feet.y:F2}) → Melee ({at.X:F1}, {at.Y:F1}){(respawn ? ", respawned" : "")}");
            if (respawn) respawnHold.Arm();
            // King's Pass's opening drop (the Knight falls in through top1): Fox tumbles in and lands hard unless he techs
            if (!respawn && GameManager.instance.sceneName == "Tutorial_01" && GameManager.instance.entryGateName == "top1") {
                Log("the opening drop: Fox tumbles in");
                if (core.TumbleFighter(0, (float)at.X, (float)at.Y)) return true;
            }
            else if (respawn ? core.RespawnFighter(0, (float)at.X, (float)at.Y) : core.PlaceFighter(0, (float)at.X, (float)at.Y)) return true;
            Fail($"mc_place_fighter: {core.Error}");
            return false;
        }

        static void AddOutlines(Collider2D c, List<ColliderOutline> outlines) {
            bool oneWay = c.GetComponent<PlatformEffector2D>() != null;
            var tf = c.transform;
            V2 W(Vector2 local) { var w = tf.TransformPoint(local + c.offset); return new V2(w.x, w.y); }
            switch (c) {
                case BoxCollider2D b: {
                    var h = b.size / 2;
                    outlines.Add(new ColliderOutline { OneWay = oneWay, Points = new[] { W(new Vector2(-h.x, -h.y)), W(new Vector2(h.x, -h.y)), W(new Vector2(h.x, h.y)), W(new Vector2(-h.x, h.y)) } });
                    break;
                }
                case PolygonCollider2D p:
                    for (int i = 0; i < p.pathCount; i++)
                        outlines.Add(new ColliderOutline { OneWay = oneWay, Points = Array.ConvertAll(p.GetPath(i), W) });
                    break;
                case EdgeCollider2D e:
                    outlines.Add(new ColliderOutline { OneWay = oneWay, Closed = false, Points = Array.ConvertAll(e.points, W) });
                    break;
                default:
                    FoxMod.Instance.LogWarn($"collider {c.GetType().Name} on {c.name} is not converted");
                    break;
            }
        }

        // The Knight's transform sits above his feet; Fox's position is his feet.
        // Measured while his collider is on (Hollow Knight turns it off at times, and then its bounds are empty).
        static float feetOffset = -1f;
        static float HeroFeetOffset(HeroController hero) {
            var col = hero.GetComponent<Collider2D>();
            if (col != null && col.enabled && col.gameObject.activeInHierarchy && col.bounds.size.y > 0.01f)
                feetOffset = hero.transform.position.y - col.bounds.min.y;
            return feetOffset < 0 ? 0f : feetOffset;
        }

        // The Knight follows Fox: no input, no gravity, a kinematic body that still touches static triggers
        // (room exits, hazards: Unity 2D kinematic bodies ignore static colliders unless full contacts are on).
        static void TakeHero(HeroController hero, bool ignoreInput, bool relinquish) {
            if (relinquish) hero.RelinquishControl();
            if (ignoreInput) hero.IgnoreInput();   // FinishedEnteringScene re-enables input after every transition
            hero.AffectedByGravity(false);
            var rb = hero.GetComponent<Rigidbody2D>();
            if (rb != null) { rb.velocity = Vector2.zero; rb.isKinematic = true; rb.useFullKinematicContacts = true; }
        }

        // Hollow Knight drives (transition, hazard respawn, death): normal body; HK sets gravity itself.
        static void GiveBodyBack(HeroController hero) {
            var rb = hero.GetComponent<Rigidbody2D>();
            if (rb != null) rb.isKinematic = false;
        }

        static void SetKnightVisible(HeroController hero, bool visible) {
            var r = hero.GetComponent<MeshRenderer>();
            if (r != null && FoxMod.Instance.Settings.ShowPlaceholder && r.enabled != visible) r.enabled = visible;
        }

        static void ReleaseHero() {
            var hero = HeroController.instance;
            if (hero == null) return;
            GiveBodyBack(hero);
            hero.AffectedByGravity(true);
            var r = hero.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = true;
            hero.RegainControl();
        }

        PadState ReadPad() {
            var d = InputManager.ActiveDevice;
            var p = new PadState();
            if (d != null && d != InputDevice.Null) {
                p.LeftX = d.LeftStickX.Value; p.LeftY = d.LeftStickY.Value;
                p.RightX = d.RightStickX.Value; p.RightY = d.RightStickY.Value;
                p.LeftTrigger = d.LeftTrigger.Value; p.RightTrigger = d.RightTrigger.Value;
                p.South = d.Action1.IsPressed; p.East = d.Action2.IsPressed; p.West = d.Action3.IsPressed; p.North = d.Action4.IsPressed;
                p.LeftBumper = d.LeftBumper.IsPressed; p.RightBumper = d.RightBumper.IsPressed;
                p.DPadUp = d.DPadUp.IsPressed; p.DPadDown = d.DPadDown.IsPressed; p.DPadLeft = d.DPadLeft.IsPressed; p.DPadRight = d.DPadRight.IsPressed;
            }
            if (FoxMod.Instance.Settings.KeyboardFallback) Keyboard(ref p);
            return p;
        }

        // WASD stick (Left Shift: half tilt), arrows C-stick, J = A, K = B, Space / L = jump, I = shield, U = Z
        static void Keyboard(ref PadState p) {
            float tilt = Input.GetKey(KeyCode.LeftShift) ? 0.5f : 1f;
            float kx = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            float ky = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            if (kx != 0 || ky != 0) { p.LeftX = kx * tilt; p.LeftY = ky * tilt; }
            float cx = (Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            float cy = (Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            if (cx != 0 || cy != 0) { p.RightX = cx; p.RightY = cy; }
            p.South |= Input.GetKey(KeyCode.J);
            p.East |= Input.GetKey(KeyCode.K);
            p.West |= Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.L);
            p.LeftBumper |= Input.GetKey(KeyCode.I);
            p.RightBumper |= Input.GetKey(KeyCode.U);
            p.DPadUp |= Input.GetKey(KeyCode.T);       // taunt
            p.DPadLeft |= Input.GetKey(KeyCode.E);     // interact
            p.DPadDown |= Input.GetKey(KeyCode.F);     // focus
        }

        void DrawPlaceholder(V2 feet) {
            if (!FoxMod.Instance.Settings.ShowPlaceholder) return;
            if (placeholder == null) {
                placeholder = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(placeholder.GetComponent<Collider>());
                var mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 0.55f, 0.1f) };   // unlit: HK scenes have few realtime lights
                placeholder.GetComponent<Renderer>().material = mat;
                DontDestroyOnLoad(placeholder);
            }
            float h = (float)(16.0 / FoxMod.Instance.Settings.WorldScale), w = h * 0.6f;   // Fox is about 16 Melee units tall
            placeholder.SetActive(true);
            placeholder.transform.localScale = new Vector3(w * (fox.Facing < 0 ? -1 : 1), h, 1);
            placeholder.transform.position = new Vector3((float)feet.X, (float)feet.Y + h / 2, 0.01f);
        }

        void OnApplicationQuit() => core?.Dispose();
    }
}
