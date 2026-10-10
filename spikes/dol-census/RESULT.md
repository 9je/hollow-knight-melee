# main.dol static-recompilation census (NTSC 1.02)

Run: `census.py <iso> ~/src-ref/melee/config/GALE01/symbols.txt` → `census.txt` (full mnemonic counts).

- Code: text0 0x80003100–0x80005520, text1 0x80005940–0x803B7240 (~3.7 MB, ~970k instructions).
- Function boundaries: 19,827 functions from the decomp cover 99.79% of text bytes; the rest is padding.
- Decoding: everything decodes. Capstone misses `fcmpo` (63/32, 7,884 uses) and `mcrxr` (2): that's a Capstone gap. Our own decoder will handle them. There are 1,658 zero padding words.
- Indirect control flow: 195 `bctr` (switch jump tables and computed tail calls) and 874 `blrl` (function-pointer calls, mostly GObj/fighter callbacks). Every entry point is known, so these go through an address-to-function table. Jump-table bounds can be found from the `cmplwi`/`bgt` guard, the same way decomp-toolkit does it.
- No self-modifying code in game logic. `icbi`/`dcbi`/`rfi`/`mtmsr`/`mtsr` only appear in the Dolphin SDK OS layer (cache, interrupts, exceptions), which we replace with a high-level emulation (HLE) layer rather than recompile.
- FP mix: fsubs 6.6k, fmuls 6.4k, fadds 3.6k, fmul 3.1k, fmadds 2.2k, fcmpo 7.9k, frsp 1.2k, fctiwz 1.0k, frsqrte 278, fres 2. Paired singles are modest: psq_l/st ~1k, ps_* ~1.7k, mostly in sysdolphin math and the GX/HSD libraries.

Verdict: code discovery is not a blocker. Every function, call target and instruction is known statically; Melee is a well-behaved static-recompile target. The remaining costs are the FP semantics (see ../gekko-fp/RESULT.md) and the runtime HLE (OS threads, DVD, GX stubs, ARAM, pads), which is engineering effort, not a feasibility question.

FPSCR check (`where.py`): the only FPSCR instructions are mffs in __setjmp/__OSSaveFPUContext and mtfsf in __longjmp/__OSLoadFPUContext, which just save and restore it. There is no mtfsfi/mtfsb0/mtfsb1/mcrfs. Game code never changes the rounding mode or exception enables, and never reads FPRF. That closes the open risk in ../gekko-fp/RESULT.md: RN=nearest throughout, and FPSCR is only ever saved and restored.
