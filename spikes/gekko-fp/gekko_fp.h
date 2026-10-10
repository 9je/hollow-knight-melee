/* gekko_fp.h - Gekko floating point semantics, ported from Dolphin (see RESULT.md for commit).
 * Plain C99. Assumes FPSCR.RN = nearest, all FPSCR exception enables (VE/ZE/...) = 0,
 * sticky/status flags (FPRF, FI, FR, FX, ...) not modelled. FPSCR.NI (bit value 0x4) is honoured
 * where Dolphin honours it; when NI=1 the host MXCSR FTZ bit must also be set by the runtime
 * (Dolphin's SetSIMDMode does this), because Dolphin relies on hardware FTZ for double ops.
 * All "s" (single) forms and paired ops return both halves, because the hardware/Dolphin write
 * ps1 = ps0 for scalar single results (frD.ps1 must be overwritten). Double forms return only ps0.
 */
#ifndef GEKKO_FP_H
#define GEKKO_FP_H
#include <stdint.h>

typedef struct { double ps0, ps1; } GekkoPS;
#define GEKKO_FPSCR_NI 0x4u

/* scalar double */
double gekko_fadd(uint32_t fpscr, double a, double b);
double gekko_fsub(uint32_t fpscr, double a, double b);
double gekko_fmul(uint32_t fpscr, double a, double c);
double gekko_fdiv(uint32_t fpscr, double a, double b);
double gekko_fmadd (uint32_t fpscr, double a, double c, double b);   /* a*c+b */
double gekko_fmsub (uint32_t fpscr, double a, double c, double b);   /* a*c-b */
double gekko_fnmadd(uint32_t fpscr, double a, double c, double b);
double gekko_fnmsub(uint32_t fpscr, double a, double c, double b);
/* scalar single (result written to both ps0 and ps1) */
GekkoPS gekko_fadds(uint32_t fpscr, double a, double b);
GekkoPS gekko_fsubs(uint32_t fpscr, double a, double b);
GekkoPS gekko_fmuls(uint32_t fpscr, double a, double c);
GekkoPS gekko_fdivs(uint32_t fpscr, double a, double b);
GekkoPS gekko_fmadds (uint32_t fpscr, double a, double c, double b);
GekkoPS gekko_fmsubs (uint32_t fpscr, double a, double c, double b);
GekkoPS gekko_fnmadds(uint32_t fpscr, double a, double c, double b);
GekkoPS gekko_fnmsubs(uint32_t fpscr, double a, double c, double b);
GekkoPS gekko_frsp (uint32_t fpscr, double b);
GekkoPS gekko_fres (double b);
double  gekko_frsqrte(double b);
/* returns the 64-bit FPR image (0xfff8000000000000|value ...), assumes RN nearest / chop */
uint64_t gekko_fctiw (double b);
uint64_t gekko_fctiwz(double b);
double gekko_fsel(double a, double b, double c);        /* a>=-0.0 ? c : b  (ps0 only) */
double gekko_fneg(double b);
double gekko_fabs(double b);
double gekko_fnabs(double b);
double gekko_fmr(double b);

/* paired single */
GekkoPS gekko_ps_add (uint32_t fpscr, GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_sub (uint32_t fpscr, GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_mul (uint32_t fpscr, GekkoPS a, GekkoPS c);
GekkoPS gekko_ps_div (uint32_t fpscr, GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_madd (uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_msub (uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_nmadd(uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_nmsub(uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_muls0(uint32_t fpscr, GekkoPS a, GekkoPS c);
GekkoPS gekko_ps_muls1(uint32_t fpscr, GekkoPS a, GekkoPS c);
GekkoPS gekko_ps_madds0(uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_madds1(uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_sum0(uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_sum1(uint32_t fpscr, GekkoPS a, GekkoPS c, GekkoPS b);
GekkoPS gekko_ps_merge00(GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_merge01(GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_merge10(GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_merge11(GekkoPS a, GekkoPS b);
GekkoPS gekko_ps_res   (GekkoPS b);
GekkoPS gekko_ps_rsqrte(uint32_t fpscr, GekkoPS b);
GekkoPS gekko_ps_sel(GekkoPS a, GekkoPS b, GekkoPS c);
GekkoPS gekko_ps_neg (GekkoPS b);
GekkoPS gekko_ps_abs (GekkoPS b);
GekkoPS gekko_ps_nabs(GekkoPS b);
GekkoPS gekko_ps_mr  (GekkoPS b);

/* quantized loads/stores. type = GQR LD_TYPE/ST_TYPE (0 float, 4 u8, 5 u16, 6 s8, 7 s16),
 * scale = 6-bit GQR scale field, w = instruction W bit. mem is guest (big-endian) memory.
 * Types 1-3 (invalid): load returns {0,0}, store writes nothing (matches Dolphin).
 * Store returns the number of bytes written. */
GekkoPS gekko_psq_load (const uint8_t *mem, unsigned type, unsigned scale, unsigned w);
unsigned gekko_psq_store(uint8_t *mem, GekkoPS v, unsigned type, unsigned scale, unsigned w);

/* helpers exposed for the harness / recompiler */
uint32_t gekko_convert_to_single(uint64_t x);        /* Dolphin ConvertToSingle  (stfs)  */
uint32_t gekko_convert_to_single_ftz(uint64_t x);    /* Dolphin ConvertToSingleFTZ (psq_st float) */
uint64_t gekko_convert_to_double(uint32_t x);        /* Dolphin ConvertToDouble  (lfs)   */
#endif
