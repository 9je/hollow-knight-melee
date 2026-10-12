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
        FoxRenderer model;                      // Fox's Melee model; null = the placeholder box
        SfxPlayer sfx;                          // Melee's sound effects; null = silent
        readonly float[] bones = new float[128 * 12];
        McFighter fox;
        const uint ActionWait = 14;             // Melee's Wait: Fox standing
        FocusRules focus;
        bool focusing, interactHeld, focusHeld;
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

        void Update() {
            if (state == State.Failed) return;
            var hero = HeroController.instance;
            var gm = GameManager.instance;
            var invState = gm != null && gm.inventoryFSM != null ? gm.inventoryFSM.ActiveStateName : null;
            GcInput.InventoryOpen = invState != null && invState != "Closed" && !invState.StartsWith("Init");
            GcInput.HideDPadSides = hero != null && gm != null && gm.IsGameplayScene() && gm.gameState == GlobalEnums.GameState.PLAYING && !GcInput.InventoryOpen;
            // menus (and quitting to them: Quit_To_Menu is a gameplay scene to Hollow Knight, but Fox holding the Knight
            // there stalled the quit on a black screen): nothing of Fox lingers
            if (hero == null || gm == null || !gm.IsGameplayScene() || gm.sceneName == "Quit_To_Menu") {
                if (placeholder) placeholder.SetActive(false);
                model?.Hide(); effects?.Hide(); gx?.Hide();
                combat?.ReleaseAll(); grab?.Release();
                if (holdingBody && hero != null) { GiveBodyBack(hero); hero.AffectedByGravity(true); }   // Hollow Knight's body again
                holdingBody = false;
                needPlace = true;
                return;
            }
            if (state == State.Waiting && !Boot()) return;
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
            if (session.Owner(f) == HeroOwner.Hollow) {
                enemies?.Clear();
                // Transitions, hazards, death and unconvertible rooms are Hollow Knight's: it needs the Knight's
                // normal physics (entering a room drops him with gravity onto the floor).
                if (holdingBody && session.ReturnBody(f)) GiveBodyBack(hero);
                holdingBody = false;
                needPlace = true;
                accumulator = 0;
                if (!session.Interacting) {     // reading a tablet or sitting down: Fox stays drawn where he stood
                    if (placeholder) placeholder.SetActive(false);
                    model?.Hide(); effects?.Hide(); gx?.Hide();
                } else if (PlayerData.instance.GetBool("atBench")) Rest(hero);
                combat?.ReleaseAll(); grab?.Release();
                return;
            }
            if (!WatchTerrain(gm.sceneName)) return;
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
                if (hkc.Interact && !interactHeld && fox.Airborne == 0) { interactHeld = true; Interact(hero); return; }
                interactHeld = hkc.Interact;
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
                if (!Gate()) return;
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
                profBridgeMs += profWatch.Elapsed.TotalMilliseconds - st;
                enemies?.AfterFrame(hero);
                if (grab != null && !grab.After(core, room)) { Fail("stand-in: " + core.Error); return; }
                if (!core.ReadFighter(0, out fox)) { Fail("cannot read Fox: " + core.Error); return; }
                if (pogo != null && !pogo.Frame(core, room, fox)) { Fail("pogo: " + core.Error); return; }
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

        // What Hollow Knight's ListenForUp does when "up" is pressed: send its wasPressed event, for every state machine
        // listening right now (a tablet, an NPC or a bench the Knight stands at). No input device is involved, so
        // Hollow Knight's own controller (and keyboard prompts) stay as they are.
        static int PressUp(HeroController hero) {
            int n = 0;
            foreach (var fsm in new List<PlayMakerFSM>(PlayMakerFSM.FsmList)) {
                if (fsm == null || fsm.gameObject == hero.gameObject || fsm.Fsm?.ActiveState == null) continue;
                foreach (var a in fsm.Fsm.ActiveState.Actions)
                    if (a is HutongGames.PlayMaker.Actions.ListenForUp up && up.Enabled && up.wasPressed != null) {
                        fsm.Fsm.Event(up.wasPressed);
                        n++;
                        break;
                    }
            }
            return n;
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
            if (step.Heal) { hero.AddHealth(1); focusSound.Heal(); }   // Focus heals (and percent follows the masks back down)
            focusSound.Charging(step.Focusing);
            focusEffects.Focusing(hero, step.Focusing);
            if (step.Heal) focusEffects.Healed(hero);
            focusing = step.Focusing;
            return focusing;
        }

        // spec §6: the moves locked by what the Knight has (MoveGating); set when it changes
        uint gatesSet = uint.MaxValue;
        bool Gate() {
            uint locked = 0;
            if (FoxMod.Instance.Settings.MoveGating) {
                var pd = PlayerData.instance;
                locked = MoveGates.Locked(new HkUnlocks {
                    VengefulSpirit = pd.GetInt("fireballLevel") > 0, MothwingCloak = pd.GetBool("hasDash"),
                    MantisClaw = pd.GetBool("hasWalljump"), CrystalHeart = pd.GetBool("hasSuperDash"),
                    DesolateDive = pd.GetInt("quakeLevel") > 0, MonarchWings = pd.GetBool("hasDoubleJump"),
                });
            }
            if (locked == gatesSet) return true;
            if (!core.SetGates(0, locked)) { Fail("mc_set_gates: " + core.Error); return false; }
            gatesSet = locked;
            Log($"locked moves: {locked}");
            return true;
        }

        bool Boot() {
            var s = FoxMod.Instance.Settings;
            Prompts.Hook();
            if (!hooked) {
                hooked = true;
                On.HeroController.Respawn += (orig, self) => Track(orig(self));
                On.HeroController.HazardRespawn += (orig, self) => Track(orig(self));
            }
            string dir = FoxMod.ModDir, so = Path.Combine(dir, "libmelee_core.so"), mem1 = Path.Combine(dir, "mem1.bin");
            if (string.IsNullOrEmpty(s.IsoPath) || !File.Exists(s.IsoPath)) { Fail($"set IsoPath in FoxMod.GlobalSettings.json to your Melee NTSC 1.02 ISO (now: '{s.IsoPath}')"); return false; }
            try { core = MeleeCore.Load(so); } catch (MeleeCoreException e) { Fail(e.Message); return false; }
            if (!core.Init(mem1, s.IsoPath)) { Fail("mc_init: " + core.Error); return false; }
            bool grabs = s.Grabs?.Enabled ?? false;
            if (!(grabs ? core.StartFoxWithStandIn() : core.StartSoloFox())) { Fail("mc_start_match: " + core.Error); return false; }
            core.SetInput(0, new McInput());
            if (grabs) core.SetInput(GrabBridge.Port, new McInput());
            for (int p = grabs ? 2 : 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) if (!core.Step()) { Fail("countdown: " + core.Error); return false; }   // past the 123-frame countdown
            if (!core.SetEdgeAssist(s.EdgeAssist)) { Fail("mc_set_edge_assist: " + core.Error); return false; }
            if (grabs && !core.SetStandIn(GrabBridge.Port)) { Fail("mc_standin_set: " + core.Error); return false; }
            if (s.ShowModel) {
                var path = Path.Combine(dir, "fox.model");
                try { model = FoxRenderer.Create(FoxModel.Load(path)); Log("drawing Fox's model"); }
                catch (Exception e) { Log($"fox.model not used ({e.Message}); drawing the placeholder"); }
            }
            if (s.FoxHits) combat = new CombatBridge();
            if (s.EnemiesHitFox) enemies = new EnemyBridge(s.EnemyHits ?? new EnemyHitTuning(), () => holdingBody && state == State.Running);
            if (grabs) grab = new GrabBridge(s.Grabs, enemies);
            if (s.Pogo?.Enabled ?? false) pogo = new PogoBridge(s.Pogo, () => holdingBody && state == State.Running);
            try { effects = new EffectsRenderer(); } catch (Exception e) { Log("effects not drawn: " + e.Message); }
            if (s.MeleeGraphics) {
                try {
                    if (!core.GxCapture(true)) throw new Exception(core.Error);
                    gx = new GxRenderer();
                    Log("drawing Melee's own graphics");
                } catch (Exception e) { Log("Melee's graphics not drawn (" + e.Message + "): fox.model instead"); gx = null; }
            }
            try { sfx = SfxPlayer.Create(core, Log); } catch (Exception e) { Log("Melee sounds off: " + e.Message); }
            focus = new FocusRules(Math.Max(1, (int)Math.Round(s.FocusSeconds * 60)), 33);
            state = State.Running;
            Log("libmelee_core running");
            return true;
        }

        /// Converts the scene's terrain into the core's collision. A room that cannot be converted is played
        /// as the Knight (FoxSession gives him back) until the next room converts.
        bool EnterRoom(string scene) {
            terrainAt.Clear();
            terrain = FindTerrain();
            terrainSet.Reset();
            terrainSet.Changed(Ids(terrain));
            var outlines = Outlines(terrain);
            if (outlines.Count == 0) { RoomFailed(scene, "no terrain colliders"); return true; }
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

        static bool LiveTerrain(Collider2D c) =>
            c != null && c.enabled && !c.isTrigger && c.gameObject.layer == TerrainLayer && c.gameObject.activeInHierarchy;

        static List<Collider2D> FindTerrain() {
            var l = new List<Collider2D>();
            foreach (var c in FindObjectsOfType<Collider2D>()) if (LiveTerrain(c)) l.Add(c);
            return l;
        }

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
                if (!LiveTerrain(c)) { gone = true; break; }
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
        void HideKnightDeaths(HeroController hero, bool hide) {
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
