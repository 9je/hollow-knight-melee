# Gekko FP spike: can C reproduce Dolphin's FP bit-for-bit on x86-64?

## Dolphin reference
- Repo https://github.com/dolphin-emu/dolphin, shallow sparse clone of master, commit
  **e6f3ae17627e4344da95b13424af5baf4c892b08** (in `dolphin-src/`, Source/Core/{Common,Core/PowerPC}).
- Files ported: `Interpreter_FPUtils.h`, `Interpreter_FloatingPoint.cpp`, `Interpreter_Paired.cpp`,
  `Interpreter_LoadStorePaired.cpp`, `Common/FloatUtils.{h,cpp}`, FPSCR in `Gekko.h`.

## Deliverables
- `gekko_fp.h`, `gekko_fp.c`: plain C99, no dependencies beyond libm (`fma`, `copysign`, `ldexpf`).
- `harness.cpp` + `build.sh`: the harness compiles **Dolphin's own, unmodified** .cpp files
  (copied byte-for-byte into `build/`) against small stubs in `stubs/` (PowerPCState, flat
  big-endian MMU, empty fmt, Interpreter class declaration). The real `Gekko.h`, `BitField.h`,
  `FloatUtils.cpp`, `UReg_FPSCR` etc. are used. Each op is run through the real
  `Interpreter::fmaddsx(...)`, `ps_madd`, `psq_l`, `psq_st`... and compared with `gekko_*`.
- Comparison is on raw bits: ps0 and ps1 of FD (including NaN payloads and sign), and for
  "double" scalar ops that ps1 is left untouched. psq compares the loaded ps0/ps1 and the
  whole 64-byte guest memory image after a store.
- Reproduce: `./build.sh strict -O2 -msse2 -mfpmath=sse -ffp-contract=off && ./build/harness_strict 10000000`

## Results (10,000,000 cases per op, per NI setting; 0 mismatches everywhere)
Every row below ran 10M cases with FPSCR.NI=0 and 10M with NI=1 (MXCSR.FTZ set as Dolphin's
SetSIMDMode does). 

| group | ops | cases each | mismatches |
|---|---|---|---|
| scalar double | fadd fsub fmul fdiv fmadd fmsub fnmadd fnmsub | 2x10M | 0 |
| scalar single | fadds fsubs fmuls fdivs fmadds fmsubs fnmadds fnmsubs frsp | 2x10M | 0 |
| estimates | fres frsqrte ps_res ps_rsqrte | 2x10M | 0 |
| convert/select/move | fctiw fctiwz fsel fneg fabs fnabs fmr | 2x10M | 0 |
| paired arithmetic | ps_add sub mul div madd msub nmadd nmsub muls0 muls1 madds0 madds1 sum0 sum1 | 2x10M | 0 |
| paired moves | ps_merge00/01/10/11 sel neg abs nabs mr | 2x10M | 0 |
| psq_l | GQR types float,u8,u16,s8,s16 and invalid 1-3; W=0/1; all 64 scales | 16 combos x 10M | 0 |
| psq_st | same | 16 combos x 10M | 0 |
| lfs / stfs / psq_st-float conversions (ConvertToDouble/Single/SingleFTZ) | | 3x10M | 0 |

Input mix per operand: random 64-bit patterns, widened random singles, a table of 41 specials
(+-0, inf, QNaN/SNaN with various payloads, double and single denormals, FLT_MAX/FLT_MIN
neighbours, 2^31 edges), one-bit perturbations of those, exponent windows around single
overflow (2^127..2^129) and underflow, single "half-ulp tie + sticky" patterns, x.5 integer
ties, few-bit singles, and for the FMA-s family a dedicated generator (25-bit exact product with
a tiny +-b so the double result lands exactly on the float tie: Dolphin's error-free-transform
correction path). Also b=a and b=-a (cancellation) injected.
Mutation check: disabling the tie fix, Force25Bit on frC, or its use in fmuls each produced
thousands of mismatches in 300k cases, so the fuzz does exercise those paths (tie path ~0.5% of fmadds cases).

### Compiler configurations (identical result hash across all four, 10M/op)
1. `-O2 -msse2 -mfpmath=sse -ffp-contract=off` (gnu++23 / c99): 0 mismatches.
2. `-O2 -march=native` (FMA+AVX2 CPU), gnu99 so GCC default `-ffp-contract=fast`: 0 mismatches, and the
   result hash equals config 1 (so contraction did **not** change any result; applies to both the
   Dolphin reference code and gekko_fp.c).
3. `-O2` (baseline x86-64, no FMA, default contraction): 0 mismatches, same hash.
4. `-O3 -march=native -ffp-contract=fast`: 0 mismatches, same hash.
GCC 13.3.0 only (no clang in the environment).

## Ops I could not match
None among those implemented. Not implemented / out of scope (by instruction): FPSCR sticky bits
and FPRF/FI/FR, exceptions (VE/ZE/OE/UE/XE enabled => Dolphin suppresses the write on invalid /
div-by-zero; we assume all disabled), fcmp/ps_cmp (CR only), fctiw with RN other than nearest
(fctiwz works; a recompiler would need explicit floor/ceil for RN=2/3), mtfsf-driven rounding.

## FPSCR dependence found
- NI (bit 0x4): honoured in `ForceSingle` (pre-rounding subnormal-single results flushed to
  signed zero) and, on x86, Dolphin ALSO sets MXCSR.FTZ so host double ops flush denormal results.
  `gekko_fp.c` takes `fpscr` and handles the ForceSingle part; the runtime must set MXCSR.FTZ when NI=1
  (tested: with MXCSR=0x9F80 results match Dolphin). With NI=0 nothing else depends on FPSCR.
  Melee normally runs NI=0 (not verified from the dol; worth checking by scanning for mtfsb1 29/mtfsf).
- RN: rounding mode must be nearest for everything except fctiw (Dolphin sets MXCSR RC to match RN;
  host ops would follow it). Assumed nearest.
- VE/ZE/OE/UE/XE: when enabled Dolphin skips the register write for invalid/div0 results. Assumed 0.
- Everything else (FPRF, FI, FR, sticky flags, FX/VX) is output-only; needed only if the game reads
  them (mffs/mcrfs) - not modelled.

## Host-compiler / semantic pitfalls found
1. **NaN sign**: x86 generates the default NaN with sign set (0xFFF8...); Dolphin returns +QNaN
   0x7FF8000000000000 for invalid ops (inf*0, inf-inf, 0/0, sqrt(-x)). Must be set explicitly
   (the `ppc_nan()` helper). NaN propagation order differs from x86 too (PPC: a, b, c; always
   quieted), so never rely on the hardware result for NaN operands.
2. **Single-form fused ops are not `(float)fma(double)`**: Dolphin does Force25Bit(frC), fma in double,
   then an error-free-transformation correction when the double result is an exact float tie. A plain
   `(float)fma(a,c,b)` double-rounds incorrectly (~0.5% of the targeted vectors). Force25Bit also applies
   to fmuls/ps_mul/ps_muls and uses the NaN/denormal/overflow-carrying bit tricks (kept verbatim).
3. **`fma()` must be a true fused op**: it is (glibc ifunc / inlined vfmadd with -mfma), but on
   targets without hardware FMA this is slow software; results identical in all configs.
4. **FP contraction**: `-ffp-contract=fast` (GCC default in gnu99/gnu++ modes, only active with -mfma) did
   not change results here, because the library has no `x*y+z` expressions outside `fma()`. Generated
   recompiled code that writes `a*c+b` for a non-fused Gekko op (fmul followed by fadd, which Gekko
   rounds twice) WOULD be contracted wrongly; the recompiler output must be built with
   `-ffp-contract=off` (or use `#pragma STDC FP_CONTRACT OFF`), and never `-ffast-math`.
5. `-std=c99` implies `-ffp-contract=off` in GCC, `-std=gnu99` does not.
6. Negative-zero results come out right only because bit tricks/`copysign` are used; the fctiw
   magic-number rounding `(x+2^52)-2^52` relies on MXCSR round-to-nearest and no value-changing
   optimization (fine without fast-math).
7. `ScaleAndClamp` casts NaN float to a small int type (UB in C++). On x86 GCC yields 0; `gekko_fp.c`
   defines NaN -> 0 explicitly. Real hardware behaviour for NaN in psq_st not verified.
8. psq_l/st with invalid GQR types 1-3: Dolphin loads 0.0/0.0 and stores nothing (asserts); copied.
   `int8_t >>` of a negative mask in Force25Bit relies on arithmetic shift (all targeted compilers).
9. Dolphin's ps_* paired ops do NOT apply Force25Bit to the A operand and do not round A/B inputs
   to single; ps_sum0/1 and ps_res differ per op (ps_res does not round the result; ps_rsqrte does). Copied exactly.
10. Dolphin keeps itself known-inexact in these cases (code comments): frC overflowing to an
   unreachable value in single FMA, and some 64-bit-subnormal double inputs to single FMA. The port is
   bug-compatible with Dolphin there (matches), but these may differ from the console.

## Verdict
Feasible. A small C99 library (about 400 lines) reproduces the Dolphin-defined behaviour of
every requested scalar, paired-single and psq op bit-exactly (including NaN payloads/signs,
denormals, ties, NI=1) over 10M+ differential cases per op, against Dolphin's own unmodified
interpreter code, and the result is stable across GCC -O2/-O3, with and without FMA
hardware/contraction, as long as the host is x86-64/SSE2 double (no x87), `fma()` is truly fused, the
recompiled code is built with `-ffp-contract=off` and no fast-math, MXCSR is round-to-nearest (FTZ iff
FPSCR.NI), and FPSCR exception enables are 0. Remaining risks are not FP arithmetic but (a) whether
Melee ever enables FPSCR exceptions, non-nearest RN, or reads FPRF/FI/FR via mffs (needs a scan of the
dol; FPRF-reading would need the flags added), (b) the few places where Dolphin itself is
documented as inexact vs. hardware, which only matter if replays diverge from the console there,
and (c) the harness only tests Dolphin's interpreter; its JIT is not checked here, though Dolphin
aims to match the interpreter bit-for-bit.
