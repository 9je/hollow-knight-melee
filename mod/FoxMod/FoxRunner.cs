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
        bool sawDeath, sawBench;                // Fox's percent resets after a death, a bench and a Focus heal
        RoomTransform gxRoom;                   // the room gx last drew in
        CombatBridge combat;                    // Fox hitting enemies; null when FoxHits is off
        FoxRenderer model;                      // Fox's Melee model; null = the placeholder box
        SfxPlayer sfx;                          // Melee's sound effects; null = silent
        readonly float[] bones = new float[128 * 12];
        McFighter fox;
        const uint ActionWait = 14;             // Melee's Wait: Fox standing
        FocusRules focus;
        bool focusing, interactHeld;
        const int ScanFrames = 30;              // how often new terrain (a gate closing) is looked for
        List<Collider2D> terrain = new List<Collider2D>();   // the colliders Melee's collision was built from
        readonly TerrainSet terrainSet = new TerrainSet();
        int scanTimer;

        static void Log(string m) => FoxMod.Instance.Log(m);
        void Fail(string why) {
            FoxMod.Instance.LogError(why);
            state = State.Failed;
            if (session.Fail()) ReleaseHero();  // only a Knight Fox took (a boot failure leaves him alone)
            holdingBody = false;
            if (placeholder) placeholder.SetActive(false);
            model?.Hide(); effects?.Hide(); gx?.Hide();
            combat?.ReleaseAll();
            sfx?.StopAll();
        }

        static HeroFlags Flags(HeroController h) => new HeroFlags {
            Transitioning = h.cState.transitioning,
            WaitingToTransition = h.transitionState == GlobalEnums.HeroTransitionState.WAITING_TO_TRANSITION,
            HazardRespawning = h.cState.hazardRespawning, HazardDeath = h.cState.hazardDeath, Dead = h.cState.dead,
            AcceptingInput = h.acceptingInput, ControlRelinquished = h.controlReqlinquished,
            Loading = GameManager.instance.gameState != GlobalEnums.GameState.PLAYING
                   && GameManager.instance.gameState != GlobalEnums.GameState.PAUSED,
        };

        void Update() {
            if (state == State.Failed) return;
            var hero = HeroController.instance;
            var gm = GameManager.instance;
            if (hero == null || gm == null || !gm.IsGameplayScene()) {   // menus: nothing of Fox lingers
                if (placeholder) placeholder.SetActive(false);
                model?.Hide(); effects?.Hide(); gx?.Hide();
                combat?.ReleaseAll();
                return;
            }
            if (state == State.Waiting && !Boot()) return;
            var f = Flags(hero);
            // A new room is converted once Hollow Knight has finished bringing the Knight in.
            if (!FoxSession.HollowBusy(f) && session.NeedsRoom(gm.sceneName) && !EnterRoom(gm.sceneName)) return;
            SetKnightVisible(hero, session.KnightVisible(f));
            if (f.Dead) sawDeath = true;
            if (PlayerData.instance.GetBool("atBench")) sawBench = true;
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
                }
                combat?.ReleaseAll();
                return;
            }
            if (!WatchTerrain(gm.sceneName)) return;
            if (needPlace) { if (!PlaceAtKnight(hero)) return; needPlace = false; }
            if (sawDeath || sawBench) { core.SetPercent(0f); sawDeath = sawBench = false; }
            var take = session.Take(f);
            TakeHero(hero, take.IgnoreInput, take.Relinquish);
            holdingBody = true;
            accumulator += Time.deltaTime;        // scaled: pausing Hollow Knight stops Melee
            int frames = 0;
            while (accumulator >= Frame && frames < 4) {
                accumulator -= Frame;
                frames++;
                // a GameCube controller on the adapter plays natively; otherwise the gamepad/keyboard mapping
                var gc = FoxMod.Instance.GameCube?.Melee();
                var pad = gc == null ? ReadPad() : default;
                var hkc = gc is McInput g ? HkControls.FromGameCube(g) : HkControls.FromPad(pad);
                var input = gc is McInput g2 ? HkControls.ForMelee(g2) : InputMapper.Map(pad);
                if (hkc.Interact && !interactHeld && fox.Airborne == 0) { interactHeld = true; Interact(hero); return; }
                interactHeld = hkc.Interact;
                if (Focus(hero, hkc.Focus)) input = new McInput();   // Fox stands still while focusing
                core.SetInput(0, input);
                int at = 0;
                bool pause = combat != null || enemies != null;
                if (pause) {
                    at = core.StepToHits();   // Melee's hit resolution, mid-frame: hits land this frame
                    if (at < 0) { Fail("libmelee_core stopped: " + core.Error); return; }
                    if (at == 0 && combat != null && !combat.Frame(core, room)) { Fail("reading Fox's hitboxes: " + core.Error); return; }
                    if (at == 0 && enemies != null && !enemies.Frame(core, room, hero.transform.position)) { Fail("applying a hit to Fox: " + core.Error); return; }
                }
                // StepToHits returning 1 means that frame already ran to its end (no hit resolution for port 0)
                if ((!pause || at == 0) && !core.Step()) { Fail("libmelee_core stopped: " + core.Error); return; }
                sfx?.Frame(core);
                if (combat != null && !combat.AfterFrame(core)) { Fail("reading Fox: " + core.Error); return; }
                enemies?.AfterFrame(hero);
            }
            if (accumulator > Frame) accumulator = 0;   // a hitch: drop time rather than run fast
            if (!core.ReadFighter(0, out fox)) { Fail("cannot read Fox: " + core.Error); return; }
            var hk = room.ToHk(new V2(fox.X, fox.Y));
            hero.transform.position = new Vector3((float)hk.X, (float)hk.Y + HeroFeetOffset(hero), hero.transform.position.z);
            // Melee's own frame (Fox, items, effects as Melee draws them); fox.model and the stand-in shapes otherwise
            if (gx != null && frames == 0 && room == gxRoom && gx.Shown) return;   // nothing new to draw
            gxRoom = room;
            if (gx != null && gx.Draw(core, room)) {
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
            if (step.Drain > 0) hero.TakeMP(step.Drain);
            if (step.Heal) { hero.AddHealth(1); core.SetPercent(0f); }   // Focus heals and resets percent (spec §5.3)
            focusing = step.Focusing;
            return focusing;
        }

        bool Boot() {
            var s = FoxMod.Instance.Settings;
            string dir = FoxMod.ModDir, so = Path.Combine(dir, "libmelee_core.so"), mem1 = Path.Combine(dir, "mem1.bin");
            if (string.IsNullOrEmpty(s.IsoPath) || !File.Exists(s.IsoPath)) { Fail($"set IsoPath in FoxMod.GlobalSettings.json to your Melee NTSC 1.02 ISO (now: '{s.IsoPath}')"); return false; }
            try { core = MeleeCore.Load(so); } catch (MeleeCoreException e) { Fail(e.Message); return false; }
            if (!core.Init(mem1, s.IsoPath)) { Fail("mc_init: " + core.Error); return false; }
            if (!core.StartSoloFox()) { Fail("mc_start_match: " + core.Error); return false; }
            core.SetInput(0, new McInput());
            for (int p = 1; p < 4; p++) core.SetInput(p, null);
            for (int i = 0; i < 180; i++) if (!core.Step()) { Fail("countdown: " + core.Error); return false; }   // past the 123-frame countdown
            if (s.ShowModel) {
                var path = Path.Combine(dir, "fox.model");
                try { model = FoxRenderer.Create(FoxModel.Load(path)); Log("drawing Fox's model"); }
                catch (Exception e) { Log($"fox.model not used ({e.Message}); drawing the placeholder"); }
            }
            if (s.FoxHits) combat = new CombatBridge();
            if (s.EnemiesHitFox) enemies = new EnemyBridge(s.EnemyHits ?? new EnemyHitTuning(), () => holdingBody && state == State.Running);
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
            var lo = t.ToMelee(min); var hi = t.ToMelee(max);
            float m = (float)s.CameraMargin;
            var cam = new McBox { Left = (float)lo.X - m, Right = (float)hi.X + m, Top = (float)hi.Y + m, Bottom = (float)lo.Y - m };
            var blast = new McBox { Left = cam.Left - FarBlast, Right = cam.Right + FarBlast, Top = cam.Top + FarBlast, Bottom = cam.Bottom - FarBlast };
            if (!core.SetBounds(blast, cam)) { Fail($"{scene}: mc_set_bounds: {core.Error}"); return false; }
            room = t;
            combat?.ReleaseAll();
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
            bool gone = false;
            foreach (var c in terrain) if (!LiveTerrain(c)) { gone = true; break; }
            if (!gone && ++scanTimer < ScanFrames) return true;
            scanTimer = 0;
            var now = FindTerrain();
            if (!terrainSet.Changed(Ids(now))) { terrain = now; return true; }
            terrain = now;
            var outlines = Outlines(terrain);
            if (outlines.Count == 0) return true;   // keep what Melee has
            var shapes = RoomGeometry.Build(outlines, room);
            if (!core.BuildCollision(shapes, out var col)) { FoxMod.Instance.LogWarn($"{scene}: changed terrain not converted ({core.Error}); keeping the old collision"); return true; }
            if (!core.LoadCollision(col)) { Fail($"{scene}: mc_load_collision: {core.Error}"); return false; }
            Log($"{scene}: terrain changed, collision rebuilt ({shapes.Count} shapes)");
            return true;
        }

        void RoomFailed(string scene, string why) {
            FoxMod.Instance.LogError($"{scene}: room not converted ({why}); play it as the Knight");
            if (session.RoomConverted(scene, false)) ReleaseHero();
            holdingBody = false;
        }

        // Fox keeps his action and velocity; he starts where Hollow Knight put the Knight (feet to feet).
        // After spikes or death he comes back still, as the Knight does (no momentum to carry him back in).
        bool PlaceAtKnight(HeroController hero) {
            var feet = (Vector2)hero.transform.position - new Vector2(0, HeroFeetOffset(hero));
            var at = room.ToMelee(new V2(feet.x, feet.y));
            bool respawn = session.TakeRespawns();
            Log($"Fox takes the Knight at ({feet.x:F2}, {feet.y:F2}) → Melee ({at.X:F1}, {at.Y:F1}){(respawn ? ", respawned" : "")}");
            if (respawn ? core.RespawnFighter(0, (float)at.X, (float)at.Y) : core.PlaceFighter(0, (float)at.X, (float)at.Y)) return true;
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
