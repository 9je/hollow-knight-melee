# Testing FoxMelee at home (Checkpoint A, placeholder Fox)

## Install

```bash
cd ~/Documents/ssbmhk
bash mod/install.sh              # backs up saves + the game's Managed folder first
```

Launch with `bash mod/launch.sh`: it backs up your saves again, sets Steam to Invisible (friends don't see you playing), and waits for Enter before starting Hollow Knight (branch 1.5.78.11833, native Linux). For full privacy also set Steam > Profile > Edit Profile > Privacy Settings > Game details to Private once, since Invisible doesn't hide playtime on your profile. The main menu shows "Modding API" and FoxMelee in the top-left mod list. To undo: `bash mod/uninstall.sh`.

## Controls

- **GameCube controller (Wii U / Mayflash adapter, in Wii U mode): native.** The mod reads the adapter over USB the way
  Slippi Dolphin does, so Melee gets your controller's values unmapped. Close Slippi Dolphin first, since only one
  program can hold the adapter. Settings: `GameCubePort` (0 = first controller found, 1-4 = that port),
  `GameCubeAdapter: false` to turn it off.
- **D-pad (any controller):** up = Melee taunt; left or right = interact (inspect, talk, sit at a bench; Fox must be on
  the ground); down (hold) = Focus: Fox stands still, Soul drains, and a mask heals after about 1.1 s (`FocusSeconds`).
  Start = Hollow Knight's pause menu (A confirms, B goes back).
- Gamepad (Xbox layout): left stick = control stick, right stick = C-stick, A = A, B = B, X/Y = jump, RB = Z, LT/RT =
  analog L/R (click past 90%), LB = full shield.
- Keyboard: WASD stick (Left Shift = half tilt), arrows = C-stick, J = A, K = B, Space or L = jump, I = shield, U = Z,
  T = taunt, E = interact, F = focus.

## Checklist (King's Pass)

1. Load a save (or a new game). After 2-3 s the Knight disappears and Fox (his Melee model, posed by Melee's own animation) appears. If `ModLog.txt` says `fox.model not used`, an orange box stands in. Check `ModLog.txt` for `libmelee_core running` and `<scene>: N shapes from M colliders`.
2. Stand, walk, dash, run, turn around: the box moves like Fox and the camera follows.
3. Jump, double jump, fast fall. Compare Fox's full jump with the Knight's (WorldScale, default 6.0). Write down what feels right. If Fox's jump is too small for the rooms, lower WorldScale (the world gets smaller in Melee units); if he clears everything too easily, raise it.
4. Wavedash (jump, air dodge diagonally into the floor), waveland onto a ledge-top.
5. Ledges: run off and grab one (CliffCatch); ledge-hop.
   Edge assist (`EdgeAssist`, on by default; not Melee): walking into an edge, Fox teeters (Ottotto) whatever your tilt.
   In Melee only a walk below 75% tilt stops at an edge; a faster walk goes off. Dash, run, jump or drift to leave the
   edge. `EdgeAssist: false` gives Melee's exact rule back.
6. Walls stop Fox; ceilings stop his jumps.
7. Walk through the exit to the next room: the transition plays, then Fox appears at the entry point and keeps moving.
8. Spikes / pits: Hollow Knight's hazard respawn plays and Fox reappears where the Knight respawns.
9. Pause (Start/Esc): Fox freezes; unpause: he continues.

10. Fox hits enemies (King's Pass crawlers and flies): jab, tilts, smashes, aerials. Each hit does Melee's % × your nail
    damage / 5 (old nail: Fox's jab = 4), gives Soul like a nail hit, and both Fox and the enemy freeze for Melee's
    hitlag (jab: 4 frames; bigger hits longer), then the enemy is knocked back. Down-air hits
    several times (as in Melee); a single hitbox never hits the same enemy twice. After the freeze, small enemies fly with
    Melee's knockback (`Knockback`, on): Melee's formula on the move's damage, angle and knockback values, with the
    enemy's "percent" from its health (0% at full, 100% near death, so hurt enemies fly further), Melee's launch speed
    and decay, and hitstun, during which the enemy's AI waits, so you can combo them. Bosses and big or tough enemies
    keep Hollow Knight's own small knockback. Settings: `Knockback` (`Enabled`, `Weight` 100, `PercentAtFullHp`/`NoHp`,
    `MaxHp`, `MaxWidth`/`MaxHeight`, `Scale`). Cutting grass gives no hitlag. Try the King's Pass breakable walls: they should break as they do to the nail, and Fox can then walk through (ModLog says "terrain changed, collision rebuilt").

11. Melee's graphics: Fox, his blaster, lasers and every effect are drawn from Melee's own graphics, as Melee draws
    them: shield bubble (red, shrinking and fading like Melee's), shine (blue flash and sparkles), Firefox (charge burst,
    flames), Illusion (streak), lasers (red beam and muzzle ring), smoke and dust. `ModLog.txt` says "drawing Melee's own
    graphics". Things to look for: parts of Fox drawn in front of / behind the wrong parts, effects in odd places, a
    stutter when a new effect appears for the first time, frame rate. `MeleeGraphics: false` goes back to the extracted
    model (fox.model) with simple shapes for the shield, shine and lasers.

12. Sounds: Fox's Melee sound effects (laser, jumps, attacks, landing, shine...) play from your ISO (`melee.sfx`, extracted by `install.sh`; `ModLog.txt` says `Melee sounds: N sounds`, or why Fox is silent). Volume: `SfxVolume` (0-1, default 0.8, also follows Hollow Knight's sound volume); `MeleeSfx: false` turns them off. The announcer and other characters' banks are not included.

Lasers hit enemies too (3 damage with the old nail, Melee's 4-frame hitlag on the enemy, none on Fox) and vanish on hit, as in Melee.

13. Enemies hitting Fox: an enemy's hit goes through Melee. Fox takes percent (10% per mask of the enemy's damage),
    Melee's knockback, hitlag and hitstun; DI, crouch-cancel and teching work as in Melee. Only a hit that lands costs
    masks (Hollow Knight's own damage: HUD, death). Shield blocks hits (Hollow Knight's attacks are heavy on the shield;
    attacks of 3+ masks go through it); spot dodge, rolls and air dodge avoid them. Each enemy can hit again after half
    a second; Fox is not invulnerable to the others. Fox's percent follows his masks (10% at full health up to 100% on the last mask; a heal brings it back down). Tuning in the
    settings file under `EnemyHits` (PercentPerMask, Angle, BaseKnockback, KnockbackGrowth, ShieldDamagePerMask,
    UnblockableFromMasks, CooldownFrames, GraceFrames: after Fox's attack lands, that enemy's body can't hurt him for 20 frames; PercentAtFullHealth 10 and PercentAtLastMask 100: Fox's percent follows his masks); `EnemiesHitFox: false` gives hits back to the Knight. Known: the Knight's
    hit box (not Fox's hurtboxes) decides contact. Hollow Knight's recoil and damage freeze do not play (Melee's hitlag
    and knockback are Fox's); hits the Knight would ignore (respawn invulnerability, invincibility) are ignored.
To turn Fox's hits off, set `FoxHits` to false in FoxMod.GlobalSettings.json.

14. Grabs and push (plan 7): an invisible second Fox inside Melee stands in for the nearest small enemy, so Melee's own
    rules decide everything.
    - Walk into a small enemy (crawler, husk): Fox and it nudge apart as two Melee fighters do. Push applies on the
      ground only, as in Melee; it doesn't move the enemy itself.
    - Z (or grab) next to a small enemy: Melee's grab range and timing. The enemy freezes in Fox's hand, A pummels
      (nail damage from Melee's 3%), and a stick direction throws it: throw damage, then it flies along Melee's
      knockback (weaker enemies, at lower health, fly further). Fox's throws that fire lasers hit with the lasers too.
    - Bosses and large or tough enemies can't be grabbed (no push either).
    - If the enemy's attack hits Fox mid-grab, or the grab times out, the enemy drops free (Melee's grab release).
    - Settings under `Grabs`: `Enabled` (false: no stand-in, as before), `WakeRangeX/Y` (HK units around Fox),
      `MaxWidth/MaxHeight` (HK units), `MaxHp`, `PercentAtFullHp`/`PercentAtNoHp`, `LaunchScale`.
    - Cost: about 2.5 ms more per Melee frame while a small enemy is near Fox (the perf log line shows it).

16. Hazard pogo (spec §5.4, the one deliberate exception to Melee): down-air onto spikes or thorns ends the down-air and
    bounces Fox up (about a full hop) into Melee's normal fall, ends a fast fall and gives his double jump back; that
    touch doesn't hurt him. Down-air again to chain pogos. Acid, pits and enemies
    are never pogoed; any other spike contact is Hollow Knight's hazard as before. Settings under `Pogo`: `Enabled`,
    `BounceHop` (share of the character's own full-hop speed, 0.9), `GraceFrames` (8). Try it on King's Pass's first spikes.

17. Move gating: replaced in roster stage 1 (item 19). Movesets are complete, wall jump needs Mantis Claw.

15. Frame rate: Melee's drawing (about three quarters of each Melee frame) now runs on the core's own thread while
    Unity goes on (`DrawOffThread`, on). No added input lag: Fox's picture was already drawn a frame behind. The
    `perf:` log line's "Melee ms/frame" is now the main thread's share only (about 1.7 ms instead of about 6). If
    anything looks wrong (Fox's picture tearing, flickering or lagging more than before), set `DrawOffThread: false`
    and tell me.

18. Fixes from the 2026-10-12 test:
    - **Quick map:** hold D-pad right (keyboard: Tab) while standing. `ModLog.txt` logs `quick map (can open: ...)`.
    - **Stairs:** a full-tilt walk goes down steps shorter than Fox. Edge assist still stops him at real drops.
    - **Death:** Melee's KO blast and Fox's KO cry play where he stands as the death starts. The Knight's head and
      death animation stay hidden. Logged as `KO`.
    - **Out of the room:** knocked past a room's sides or bottom, Fox takes Hollow Knight's pit hazard (one mask,
      back at the last safe spot) instead of falling for a long time. Logged as `out of the room ... pit`.
    - **Knockback:**
      - an up-throw no longer leaves an enemy hanging in the air (enemies without gravity fall during hitstun);
      - a down-throw or a hard hit knocks the enemy down when it lands, and it waits 45 frames before acting;
      - walking and crawling enemies no longer cancel ground launches, so the shine pushes them and waveshines
        should work.

      Each launch is logged (`launch <enemy>: kb .. angle .. hitstun ..`), so send the lines for any enemy that
      still doesn't react.

19. Characters (roster stage 1). In `FoxMod.GlobalSettings.json` set `"Character": "Marth"` (any name from the roster:
    Fox, Marth, Jigglypuff, Falco, Sheik, Captain Falcon, Peach, Ice Climbers, Pikachu, Yoshi, Samus, Dr. Mario,
    Ganondorf, Mario, Young Link, Link, Game & Watch, Luigi, Donkey Kong, Mewtwo, Roy, Zelda, Ness, Pichu, Bowser,
    Kirby), then launch. `ModLog.txt` says `character: Marth`. Marth moves, attacks enemies, grabs and throws (the
    stand-in is still a Fox), and sounds like Marth. Wall jump needs Mantis Claw now, for every character. Zelda and
    Sheik can't transform. Characters other than Fox and Marth are untested until stage 4: their oddities
    (Nana, Peach's turnips, Kirby) aren't handled yet.

20. Overnight 2026-10-12/13. Run `bash mod/install.sh` first: it now extracts every character's sounds, so expect
    30 `melee-*.sfx` files.
    - **Characters at a bench:** sit, then press D-pad up (keyboard T). Choose with left/right, play with up, close with
      down. Your settings have `UnlockAllCharacters: true`, so all 26 are listed. Picking one restarts Melee with that
      character where the Knight sits (`ModLog.txt`: `switching to ...`, `character: ...`). Stand up and play. Check
      that:
      - their sounds play;
      - hits, grabs and throws work;
      - wall jump needs Mantis Claw;
      - Zelda and Sheik can't transform;
      - Ice Climbers: Nana's hits land on enemies, and she follows Popo through doors and back out of pits;
      - Kirby can inhale an enemy (he swallows the invisible Fox stand-in) and copy Fox's blaster.

      Note anything odd, character by character.
    - **Pause, then Reset character:** this button sits below Quit, and you're back at your last bench with nothing
      lost. If it's missing or in an odd spot, `ModLog.txt` says `pause menu: ...`.
    - **Quick map:** hold D-pad right. The character stops, and Hollow Knight's map should open.
    - **Death:** Melee's KO blast plays, with now and then the star-KO scream (`KO (star)`), and no Knight mask or shade
      animation. Hollow Knight's death sound is muted. Each hidden or muted thing is logged as `death: hid ...` /
      `death: muted ...`; send those lines if something still shows.
    - **Pogo:** each character bounces about 80% of their own jump height (`BounceHop` 0.9), so Marth no longer
      flies off.
    - **Mod list:** the mod is now called "Hollow Knight: Melee" (version 0.2.0).
    - **Windows (a friend's PC, or yours):**
      1. Download `HollowKnightMeleeInstaller.exe` from the newest pre-release on GitHub.
      2. Run it. Expect a SmartScreen warning: choose "More info", then "Run anyway".
      3. It should find Hollow Knight and the ISO (or ask you to drag the ISO in), then build for 5-10 minutes.
      4. Launch from Steam.
      5. If anything fails, send `%LOCALAPPDATA%\HollowKnightMelee\installer.log`.

21. Roster stages 2-5 (2026-10-13). Run `bash mod/install.sh` (it now also extracts `stocks.bin`, Melee's stock
    icons).
    - **Character select:** sit at a bench and press D-pad up. Melee's character select appears:
      - Melee's layout, with Sheik beside Zelda;
      - stock icons; characters you don't own are dark silhouettes that say where to find them;
      - a P1 token and Player 1's panel.

      Stick or D-pad moves, A plays, B closes. Check the Knight doesn't stand up or cast while you choose. Picking a
      character shows it with "Ready..." for about a second (the core restarting in the background, without freezing
      the game), then it stands up. To see only what your save owns, set `UnlockAllCharacters` to false.
    - **Iselda:** her shop's first item is Marth, 50 Geo, with Marth's icon. Buying him logs `Marth bought from
      Iselda` and makes him selectable. The Quill no longer gives Marth.
    - **Charms:**
      - a character's charm says "Roy (Fury of the Fallen)" etc. and won't equip: it costs no notches and has no
        Hollow Knight effect;
      - your save's equipped Fury of the Fallen comes off when it loads (`character charms taken off`);
      - Sprintmaster makes the run and dash a touch faster;
      - Steady Body makes you take less knockback;
      - Fragile/Unbreakable Strength's hits do 1.5x damage;
      - Deep Focus heals two masks over a longer Focus.
    - **Gates:**
      - entering a room by a door, any door the Knight couldn't reach without an ability you lack gets an
        invisible wall (`gate: <room>[<door>] closed ...`);
      - the door you came in by is always open;
      - getting the ability reopens the room's doors (`abilities changed`);
      - `AbilityGates: false` turns this off;
      - send the `gate:` lines for any door that feels wrongly closed.
    - **Dive floors:** with Desolate Dive owned, land from a fast fall onto a dive floor. It breaks (`dive floor ...
      broken`).
    - **Shade Cloak gates:** without Shade Cloak, the black barriers are walls. With it, you pass.
    - **Dream Nail:** hold D-pad up (a tap is still the taunt). The Knight's Dream Nail swings: try it on a
      Dreamer, the Seer, or a dream-nailable enemy (`dream nail (can: True)`).
    - **Quick map:** hold D-pad right. It should open now. If it doesn't, send the `quick map` line.

## What to send back

- `ModLog.txt` (whole file).
- For any room that looked wrong: its scene name (from the log) and what was wrong (walls missing, floors you fall through, ledges in odd places).
- Your WorldScale verdict and how input latency felt.
- Combat: did hits land where Fox's attacks visibly connect? Too strong or too weak? Any enemy that could not be hit?
