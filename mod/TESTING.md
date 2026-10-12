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
    several times (as in Melee); a single hitbox never hits the same enemy twice. Enemies get Hollow Knight's knockback
    away from Fox (a back air sends an enemy behind him further behind), after the freeze. Cutting grass gives no hitlag. Try the King's Pass breakable walls: they should break as they do to the nail, and Fox can then walk through (ModLog says "terrain changed, collision rebuilt").

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

## What to send back

- `ModLog.txt` (whole file).
- For any room that looked wrong: its scene name (from the log) and what was wrong (walls missing, floors you fall through, ledges in odd places).
- Your WorldScale verdict and how input latency felt.
- Combat: did hits land where Fox's attacks visibly connect? Too strong or too weak? Any enemy that could not be hit?
