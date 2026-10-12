# Hollow Knight Melee

Play Hollow Knight as Super Smash Bros. Melee characters, with movement and combat that match Melee NTSC 1.02
frame for frame.

The mod doesn't imitate Melee. It runs Melee's own game code, statically recompiled from your ISO into a native
library (`libmelee_core`). Hollow Knight's rooms become Melee collision, and Fox's hits, hitlag, knockback, grabs and
sounds come from Melee itself.

**Status:** early development. Fox is playable. More characters, unlocked by charms, are planned, along with a
one-click Windows installer.

## Requirements

- Hollow Knight 1.5 (Steam) and the [Hollow Knight Modding API](https://github.com/hk-modding/api).
- Your own copy of Super Smash Bros. Melee NTSC 1.02 as an ISO. Nothing from Melee is included in this repository
  or its releases: everything Melee-derived is built on your PC from your ISO.
- A GameCube controller on a Wii U/Mayflash adapter (recommended), or a gamepad or keyboard.

## Layout

- `tools/`: the PowerPC to C recompiler and the ISO and asset tools (Python).
- `core/`: the runtime that hosts recompiled Melee (`libmelee_core`) and its tests.
- `mod/`: the Hollow Knight mod (`FoxMod`, `FoxCore`) and its install scripts.

## Building (developers, Linux)

See `mod/install.sh`. A Windows build and installer are in progress.

## Licence

GPL-2.0-or-later (see `LICENSE`). Parts of the floating-point emulation are derived from Dolphin (GPL-2.0-or-later).
Super Smash Bros. Melee and Hollow Knight belong to their owners; this project ships none of their files.
