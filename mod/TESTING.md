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
6. Walls stop Fox; ceilings stop his jumps.
7. Walk through the exit to the next room: the transition plays, then Fox appears at the entry point and keeps moving.
8. Spikes / pits: Hollow Knight's hazard respawn plays and Fox reappears where the Knight respawns.
9. Pause (Start/Esc): Fox freezes; unpause: he continues.

10. Fox hits enemies (King's Pass crawlers and flies): jab, tilts, smashes, aerials. Each hit does Melee's % × your nail
    damage / 5 (old nail: Fox's jab = 4), gives Soul like a nail hit, and both Fox and the enemy freeze for Melee's
    hitlag (jab: 4 frames; bigger hits longer), then the enemy is knocked back. Down-air hits
    several times (as in Melee); a single hitbox never hits the same enemy twice. Enemies get Hollow Knight's knockback
    away from Fox (a back air sends an enemy behind him further behind), after the freeze. Grabs do not hurt
    enemies. Cutting grass gives no hitlag. Try the King's Pass breakable walls: they should break as they do to the nail, and Fox can then walk through (ModLog says "terrain changed, collision rebuilt").

11. Shield (hold R/L): a red bubble the size Melee gives it, shrinking as it weakens and fading with a light press. Down-B: a blue hexagon (shine). Neutral B: lasers fly. These are simple shapes standing in for Melee's own effects.

Not yet: enemies hitting Fox. They still hit the (invisible) Knight and use his masks. Lasers do not hit enemies yet.
To turn Fox's hits off, set `FoxHits` to false in FoxMod.GlobalSettings.json.

## What to send back

- `ModLog.txt` (whole file).
- For any room that looked wrong: its scene name (from the log) and what was wrong (walls missing, floors you fall through, ledges in odd places).
- Your WorldScale verdict and how input latency felt.
- Combat: did hits land where Fox's attacks visibly connect? Too strong or too weak? Any enemy that could not be hit?
