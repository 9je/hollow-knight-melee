# core/: recompiled Melee (NTSC 1.02)

`main.dol` is statically recompiled into C (`tools/src/ssbmhk_tools/recomp/`) and checked against
Dolphin's own interpreter, compiled unmodified from the pinned commit
`e6f3ae17627e4344da95b13424af5baf4c892b08`. Nothing generated from the game, and no Dolphin
source, is committed: `core/build/` and `core/difftest/dolphin-src/` are gitignored.

| Path | What |
|---|---|
| `gekko/` | Gekko FP semantics in C99 (bit-exact vs Dolphin, see `spikes/gekko-fp/RESULT.md`) |
| `runtime/cpu.h` | guest CPU state, MEM1 access, CR/XER helpers, runtime hook prototypes |
| `runtime/dispatch.c` | guest address -> recompiled function lookup |
| `runtime/test_hooks.c` | hook implementations for tests (every guest stop longjmps out with a status) |
| `difftest/` | Dolphin oracle (`oracle.cpp` + `stubs/`), per-instruction fuzz, per-function diff |

## Running (needs `dev.toml` with `iso` and `decomp_dir`)

```bash
make -C core dolphin-src        # sparse clone of Dolphin at the pinned commit
make -C core gekko-test         # cpu.h unit test
make -C core oracle-selftest    # Dolphin oracle sanity
make -C core insn-fuzz STATES=1000 [OPT=-O0]   # every distinct non-branch word in main.dol
make -C core guest -j           # recompile main.dol -> build/libguest.a
make -C core func-diff && cd core && ./build/func_diff --all --jobs 16 --csv build/all.csv
./build/func_diff --list difftest/funcs_math.txt --sets 1000 --jobs 16
```

## Results (2026-10-10, GCC 13.3, x86-64)

- **Per instruction:** 56,800 distinct non-branch instruction words (all of them, capped at 4,000
  per op; 160 ops), 1,000 random CPU/memory states each: **0 mismatches at -O2 and at -O0**.
  After adding FP alignment faults, rerun with 300 states each and 1 in 4 EAs misaligned:
  17,040,000 cases, **0 mismatches** at both -O2 and -O0.
- **Whole DOL:** 19,827 functions, 133 shards, 185 functions with jump tables, 0 unresolved
  branches, 0 compiler warnings. Generation ~10 s; `make guest -j16` ~3 min; `libguest.a` 39 MB.
- **Math/memory list** (70 functions x 1,000 argument sets): 67,858 PASS, 1,142 FAULT_EQUAL,
  1,000 DOLPHIN_BUDGET, **0 MISMATCH / FAULT_MISMATCH / TIMEOUT / CRASH**.
- **Every function** (1 random argument set): 6,105 PASS, 13,531 FAULT_EQUAL (random pointers
  fault identically on both sides), **0 MISMATCH**, 1 FAULT_MISMATCH (below), 47 DOLPHIN_BUDGET,
  33 RECOMP_TRAP, 99 INDIRECT_MISS, 11 BAD_RETURN, 0 TIMEOUT, 0 CRASH.

## Known gaps (inputs for plan 1c)

- **Trap instructions** (high-level replacement needed): `rfi` in Config24MB, Config48MB,
  OSExceptionVector (2), OSLoadContext, OSSwitchFPUContext, RealMode, SystemCallVector,
  TRKExceptionHandler, TRKInterruptHandler, TRKSwapAndGo; `sc` in DCFlushRange, DCStoreRange,
  PPCSync; `dcbz_l` in __LCEnable. `__OSDBJump` (`bla 0x60`) traps by design.
- **setjmp/longjmp:** all 11 BAD_RETURN cases are `__longjmp` returning into a `__setjmp` site
  (HSD error handling, e.g. hsd_803B6BE4 -> 803B6C34). Plan 1c replaces both with host
  setjmp/longjmp.
- **INDIRECT_MISS:** 49 calls through a null function pointer and the rest through garbage
  pointers, all read from random argument memory; no miss targets real code.
- **Hooked SPRs:** DEC, time base, WPAR, DMAU/DMAL (locked cache DMA), MMCR0/1, THRM1-3 go
  through `guest_mfspr/guest_mtspr`; the runtime owns their behaviour.
- **Supervisor mode assumed:** the one FAULT_MISMATCH is `TRK_ppc_memcpy` (MetroTRK debugger),
  where a random argument written to MSR sets PR=1 and Dolphin then rejects a privileged
  `mtmsr`. Retail code always runs with PR=0.
- **Interrupts:** not modelled; the oracle ignores asynchronous exceptions (decrementer etc.).

## libmelee_core (plan 1c)

```bash
make -C core core-test            # boots the game, determinism, solo Fox, online RNG sync
make -C core runtime-test         # hooks, timebase, coroutine, nmsub zero sign
make -C core replay-check-tool && (cd tools && .venv/bin/ssbmhk-replay-check)   # every replay in replay_dir
```

- The game runs on a coroutine and yields at `lb_80019894` (the scene loop's pad wait). Each `mc_step()` is one frame with the input set by `mc_set_input()`.
- Time is virtual: one tick per timebase read, and a jump to `frame × 40.5 MHz/60` at each boundary.
- Interrupts (DEC alarms, DVD, ARAM, PE finish) are taken at the recompiler's poll points, with the full CPU context saved and restored.
- Matches start like Slippi playback: the debug-VS mode is entered at its GS_VS state, then the requested `StartMeleeData` and seed are installed.
- The core is Slippi's Melee, not bare 1.02: every slippi-ssbm-asm code tagged `[affects-gameplay]` that Slippi's netplay INI enables (UCF 0.84, neutral spawns, wobble limit, ...; list in `runtime/slippi_gameplay.txt`) is applied as Gecko code at generation time (`make -C core gecko`, pinned and verified). The recompiler handles the Slippi asm idioms in those bodies: `bl`/`blrl` data tables, `branch` jumps into the middle of functions, and returns past the caller's return site (UCF Shield Drop).
- `mc_start_match(..., MC_ONLINE_RNG)` replays an online match: Slippi online re-seeds the RNG every frame (`runtime/online_rng.gecko` plus a host proc).
- A zero result of `fnmsub`/`fnmsubs`/`ps_nmsub` gets the sign Slippi's deterministic JIT gives it (`b - a*c`), not the hardware's (`gekko_slippi_nmsub`, set by `mc_init`; the difftest keeps the hardware rule).
- Sound: no audio is produced. Recompiler taps (`runtime/taps.txt`, `--tap`) on `HSD_AudioSFXStartParam` and the key-off calls queue read-only events (`runtime/sfx.c`) that `mc_read_sfx()` drains; the mod plays them from samples read out of the user's ISO (`ssbmhk-extract-sfx`).
- Replay checking compares action, position, facing, percent, shield, velocities and airborne, bit for bit, on every frame for every fighter. Measured speed on the development machine: 100–125 frames/s on most stages, 66 on Fountain of Dreams (its reflections), so 1.1–2× real time.

## Rooms (plan 2)

```bash
make -C core room-test     # validation and the outline builder (no game needed)
make -C core rooms-test    # reload, bounds, placement and a built room in the real game
(cd tools && .venv/bin/ssbmhk-replay-check --reload-collision)   # stages' own collision through the room API
```

- `mc_build_collision(shapes)` turns solid outlines and one-way platforms into Melee collision (`MapCollData`). It uses Melee's own floor/wall/ceiling slope rule and ledge rule. Coordinates are Melee units; the host stage is FD (world scale 1.0).
- `mc_load_collision(col)` validates at once and applies at the next frame boundary. Inside the game it frees the previous collision and calls the game's own `mpLibLoad`, so collision behaves exactly as on a stage. After loading different geometry, place every fighter with `mc_place_fighter`.
- `mc_set_bounds(blast, camera)` sets the room's blast zone and camera box.
- `mc_place_fighter(port, x, y)` moves a fighter and keeps its action and velocity. A grounded fighter is snapped to a floor within 10 units, otherwise it becomes airborne.
- Oracle: reloading Final Destination's own collision at the start of the FD replay keeps it bit-exact.
- Limits: 2048 vertices, 1536 lines and 256 shapes per room, as in Melee.
