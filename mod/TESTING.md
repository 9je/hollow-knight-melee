# Testing FoxMelee at home (Checkpoint A, placeholder Fox)

## Install

```bash
cd ~/Documents/ssbmhk
bash mod/install.sh              # backs up saves + the game's Managed folder first
```

Launch with `bash mod/launch.sh`: it backs up your saves again, sets Steam to Invisible (friends don't see you playing), and waits for Enter before starting Hollow Knight (branch 1.5.78.11833, native Linux). For full privacy also set Steam > Profile > Edit Profile > Privacy Settings > Game details to Private once, since Invisible doesn't hide playtime on your profile. The main menu shows "Modding API" and FoxMelee in the top-left mod list. To undo: `bash mod/uninstall.sh`.

## Controls

- Gamepad (Xbox layout): left stick = control stick, right stick = C-stick, A = A, B = B, X/Y = jump, RB = Z, LT/RT = analog L/R (click past 90%), LB = full shield. The D-pad and Start stay Hollow Knight's.
- Keyboard: WASD stick (Left Shift = half tilt), arrows = C-stick, J = A, K = B, Space or L = jump, I = shield, U = Z.

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

Not in this checkpoint: combat. Enemies still hit the (invisible) Knight and use his masks; Fox cannot hurt them yet.

## What to send back

- `ModLog.txt` (whole file).
- For any room that looked wrong: its scene name (from the log) and what was wrong (walls missing, floors you fall through, ledges in odd places).
- Your WorldScale verdict and how input latency felt.
