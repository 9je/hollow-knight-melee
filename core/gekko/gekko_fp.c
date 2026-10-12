/* gekko_fp.c - C99 port of Dolphin's Gekko FP semantics (Interpreter_FPUtils.h,
 * Interpreter_FloatingPoint.cpp, Interpreter_Paired.cpp, Interpreter_LoadStorePaired.cpp,
 * Common/FloatUtils.cpp). Exceptions/sticky flags dropped (VE=ZE=0 assumed). */
#include "gekko_fp.h"
#include <math.h>
#include <string.h>

#define DOUBLE_QBIT 0x0008000000000000ULL
#define DOUBLE_SIGN 0x8000000000000000ULL
#define DOUBLE_EXP  0x7FF0000000000000ULL
#define DOUBLE_FRAC 0x000FFFFFFFFFFFFFULL
#define PPC_NAN_BITS 0x7FF8000000000000ULL  /* NOT the x86 default NaN (which has sign set) */

static inline uint64_t d2u(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static inline double u2d(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }
static inline uint32_t f2u(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
static inline float u2f(uint32_t u) { float f; memcpy(&f, &u, 4); return f; }
static inline int is_snan(double d) {
  uint64_t i = d2u(d);
  return (i & DOUBLE_EXP) == DOUBLE_EXP && (i & DOUBLE_FRAC) != 0 && (i & DOUBLE_QBIT) == 0;
}
static inline double make_quiet(double d) { return u2d(d2u(d) | DOUBLE_QBIT); }
static inline double ppc_nan(void) { return u2d(PPC_NAN_BITS); }

static inline int count_lz64(uint64_t x) { /* x != 0 */
  int n = 0; while (!(x & DOUBLE_SIGN)) { x <<= 1; n++; } return n;
}

static inline float force_single(uint32_t fpscr, double value) {
  if (fpscr & GEKKO_FPSCR_NI) {
    /* Dolphin: pre-rounding subnormal-single results always flushed (x86: bFlushToZero true,
       so the later FlushToZero(float) is not executed) */
    uint64_t vws = d2u(value) & (DOUBLE_EXP | DOUBLE_FRAC);
    if (vws < 0x3810000000000000ULL) {
      uint64_t flushed_double = d2u(value) & DOUBLE_SIGN;
      return u2f((uint32_t)(flushed_double >> 32));
    }
  }
  return (float)value;
}

static inline double force25(double d) {
  uint64_t integral = d2u(d);
  uint64_t exponent = integral & DOUBLE_EXP;
  uint64_t fraction = integral & DOUBLE_FRAC;
  if (exponent == 0 && fraction != 0) {
    int64_t keep_mask = (int64_t)0xFFFFFFFFF8000000ULL;
    uint64_t round = 0x8000000;
    uint32_t shift = (uint32_t)(count_lz64(fraction) - (63 - 52));
    keep_mask >>= shift;  /* arithmetic shift, as in Dolphin (implementation-defined in C) */
    round >>= shift;
    integral = (integral & (uint64_t)keep_mask) + (integral & round);
  } else {
    integral = (integral & 0xFFFFFFFFF8000000ULL) + (integral & 0x8000000);
  }
  return u2d(integral);
}

/* NaN result selection shared by add/sub/mul/div */
static inline double nan2(double a, double b) {
  if (isnan(a)) return make_quiet(a);
  if (isnan(b)) return make_quiet(b);
  return ppc_nan();
}
static inline double ni_add(double a, double b) { double r = a + b; return isnan(r) ? nan2(a, b) : r; }
static inline double ni_sub(double a, double b) { double r = a - b; return isnan(r) ? nan2(a, b) : r; }
static inline double ni_mul(double a, double b) { double r = a * b; return isnan(r) ? nan2(a, b) : r; }
static inline double ni_div(double a, double b) { double r = a / b; return isnan(r) ? nan2(a, b) : r; }

/* fused multiply-adds: the CPU's FMA instruction where it has one (exactly rounded, the same result as libm's fma), chosen
 * at load time; libm's otherwise */
#if defined(__x86_64__) && defined(__GNUC__) && !defined(__clang__)
#define FMA_CLONES __attribute__((target_clones("fma", "default")))
#else
#define FMA_CLONES
#endif
static inline double ni_madd_msub(int sub, int single, double a, double c, double b) {
  double result;
  if (!single) {
    result = fma(a, c, sub ? -b : b);
  } else {
    const double c_round = force25(c);
    const double b_sign = sub ? -b : b;
    result = fma(a, c_round, b_sign);
    const uint64_t result_bits = d2u(result);
    if ((result_bits & 0x000000001fffffffULL) == 0x0000000010000000ULL) {
      const double a_prime = b_sign - result;
      const double b_prime = result + a_prime;
      const double delta_a = fma(a, c_round, a_prime);
      const double delta_b = b_sign - b_prime;
      const double error = delta_a + delta_b;
      if (error != 0.0) {
        if ((error > 0.0) == (result > 0.0)) result = u2d(result_bits + 1);
        else result = u2d(result_bits - 1);
      }
    }
  }
  if (isnan(result)) {
    if (isnan(a)) return make_quiet(a);
    if (isnan(b)) return make_quiet(b);
    if (isnan(c)) return make_quiet(c);
    return ppc_nan();
  }
  return result;
}

int gekko_slippi_nmsub = 0;
static inline double jit_nmsub_zero(double r, double a, double c, double b) {
  return gekko_slippi_nmsub && r == 0.0 ? copysign(0.0, b - a * c) : r;
}

/* ---- estimate tables (Common/FloatUtils.cpp) ---- */
typedef struct { int base, dec; } BaseAndDec;
static const BaseAndDec frsqrte_expected[32] = {
    {0x1a7e800, -0x568}, {0x17cb800, -0x4f3}, {0x1552800, -0x48d}, {0x130c000, -0x435},
    {0x10f2000, -0x3e7}, {0x0eff000, -0x3a2}, {0x0d2e000, -0x365}, {0x0b7c000, -0x32e},
    {0x09e5000, -0x2fc}, {0x0867000, -0x2d0}, {0x06ff000, -0x2a8}, {0x05ab800, -0x283},
    {0x046a000, -0x261}, {0x0339800, -0x243}, {0x0218800, -0x226}, {0x0105800, -0x20b},
    {0x3ffa000, -0x7a4}, {0x3c29000, -0x700}, {0x38aa000, -0x670}, {0x3572000, -0x5f2},
    {0x3279000, -0x584}, {0x2fb7000, -0x524}, {0x2d26000, -0x4cc}, {0x2ac0000, -0x47e},
    {0x2881000, -0x43a}, {0x2665000, -0x3fa}, {0x2468000, -0x3c2}, {0x2287000, -0x38e},
    {0x20c1000, -0x35e}, {0x1f12000, -0x332}, {0x1d79000, -0x30a}, {0x1bf4000, -0x2e6},
};
static const BaseAndDec fres_expected[32] = {
    {0x7ff800, 0x3e1}, {0x783800, 0x3a7}, {0x70ea00, 0x371}, {0x6a0800, 0x340}, {0x638800, 0x313},
    {0x5d6200, 0x2ea}, {0x579000, 0x2c4}, {0x520800, 0x2a0}, {0x4cc800, 0x27f}, {0x47ca00, 0x261},
    {0x430800, 0x245}, {0x3e8000, 0x22a}, {0x3a2c00, 0x212}, {0x360800, 0x1fb}, {0x321400, 0x1e5},
    {0x2e4a00, 0x1d1}, {0x2aa800, 0x1be}, {0x272c00, 0x1ac}, {0x23d600, 0x19b}, {0x209e00, 0x18b},
    {0x1d8800, 0x17c}, {0x1a9000, 0x16e}, {0x17ae00, 0x15b}, {0x14f800, 0x15b}, {0x124400, 0x143},
    {0x0fbe00, 0x143}, {0x0d3800, 0x12d}, {0x0ade00, 0x12d}, {0x088400, 0x11a}, {0x065000, 0x11a},
    {0x041c00, 0x108}, {0x020c00, 0x106},
};

static double approx_rsqrt(double val) {
  int64_t integral = (int64_t)d2u(val);
  int64_t mantissa = integral & ((1LL << 52) - 1);
  const int64_t sign = integral & (int64_t)(1ULL << 63);
  int64_t exponent = integral & (0x7FFLL << 52);
  if (mantissa == 0 && exponent == 0) return sign ? -INFINITY : INFINITY;
  if (exponent == (0x7FFLL << 52)) {
    if (mantissa == 0) return sign ? ppc_nan() : 0.0;  /* Dolphin: quiet_NaN() == 0x7FF8.. */
    return make_quiet(val);
  }
  if (sign) return ppc_nan();
  if (!exponent) {
    do { exponent -= 1LL << 52; mantissa <<= 1; } while (!(mantissa & (1LL << 52)));
    mantissa &= (1LL << 52) - 1;
    exponent += 1LL << 52;
  }
  const int64_t exponent_lsb = exponent & (1LL << 52);
  exponent = ((0x3FFLL << 52) - ((exponent - (0x3FELL << 52)) / 2)) & (0x7FFLL << 52);
  integral = sign | exponent;
  const int i = (int)((exponent_lsb | mantissa) >> 37);
  const BaseAndDec *e = &frsqrte_expected[i / 2048];
  integral |= (int64_t)((int64_t)(e->base + e->dec * (i % 2048)) << 26);
  return u2d((uint64_t)integral);
}

static double approx_recip(double val) {
  int64_t integral = (int64_t)d2u(val);
  const int64_t mantissa = integral & ((1LL << 52) - 1);
  const int64_t sign = integral & (int64_t)(1ULL << 63);
  int64_t exponent = integral & (0x7FFLL << 52);
  if (mantissa == 0 && exponent == 0) return copysign(INFINITY, val);
  if (exponent == (0x7FFLL << 52)) {
    if (mantissa == 0) return copysign(0.0, val);
    return make_quiet(val);
  }
  if (exponent < (895LL << 52)) return copysign((double)3.40282346638528859811704183484516925440e+38, val); /* FLT_MAX */
  if (exponent >= (1149LL << 52)) return copysign(0.0, val);
  exponent = (0x7FDLL << 52) - exponent;
  const int i = (int)(mantissa >> 37);
  const BaseAndDec *e = &fres_expected[i / 1024];
  integral = sign | exponent;
  integral |= (int64_t)((int64_t)(e->base - (e->dec * (i % 1024) + 1) / 2) << 29);
  return u2d((uint64_t)integral);
}

/* ---- scalar double ---- */
double gekko_fadd(uint32_t f, double a, double b) { (void)f; return ni_add(a, b); }
double gekko_fsub(uint32_t f, double a, double b) { (void)f; return ni_sub(a, b); }
double gekko_fmul(uint32_t f, double a, double c) { (void)f; return ni_mul(a, c); }
double gekko_fdiv(uint32_t f, double a, double b) { (void)f; return ni_div(a, b); }
FMA_CLONES double gekko_fmadd (uint32_t f, double a, double c, double b) { (void)f; return ni_madd_msub(0, 0, a, c, b); }
FMA_CLONES double gekko_fmsub (uint32_t f, double a, double c, double b) { (void)f; return ni_madd_msub(1, 0, a, c, b); }
FMA_CLONES double gekko_fnmadd(uint32_t f, double a, double c, double b) { (void)f; double t = ni_madd_msub(0, 0, a, c, b); return isnan(t) ? t : -t; }
FMA_CLONES double gekko_fnmsub(uint32_t f, double a, double c, double b) { (void)f; double t = ni_madd_msub(1, 0, a, c, b); return isnan(t) ? t : jit_nmsub_zero(-t, a, c, b); }

/* ---- scalar single ---- */
static inline GekkoPS fill(float x) { GekkoPS r; r.ps0 = r.ps1 = (double)x; return r; }
GekkoPS gekko_fadds(uint32_t f, double a, double b) { return fill(force_single(f, ni_add(a, b))); }
GekkoPS gekko_fsubs(uint32_t f, double a, double b) { return fill(force_single(f, ni_sub(a, b))); }
GekkoPS gekko_fmuls(uint32_t f, double a, double c) { return fill(force_single(f, ni_mul(a, force25(c)))); }
GekkoPS gekko_fdivs(uint32_t f, double a, double b) { return fill(force_single(f, ni_div(a, b))); }
FMA_CLONES GekkoPS gekko_fmadds (uint32_t f, double a, double c, double b) { return fill(force_single(f, ni_madd_msub(0, 1, a, c, b))); }
FMA_CLONES GekkoPS gekko_fmsubs (uint32_t f, double a, double c, double b) { return fill(force_single(f, ni_madd_msub(1, 1, a, c, b))); }
FMA_CLONES GekkoPS gekko_fnmadds(uint32_t f, double a, double c, double b) { float t = force_single(f, ni_madd_msub(0, 1, a, c, b)); return fill(isnan(t) ? t : -t); }
FMA_CLONES GekkoPS gekko_fnmsubs(uint32_t f, double a, double c, double b) { float t = force_single(f, ni_madd_msub(1, 1, a, c, b)); return fill(isnan(t) ? t : (float)jit_nmsub_zero(-t, a, force25(c), b)); }
GekkoPS gekko_frsp(uint32_t f, double b) { return fill(force_single(f, b)); }
GekkoPS gekko_fres(double b) { GekkoPS r; r.ps0 = r.ps1 = approx_recip(b); return r; } /* Fill(double): not rounded */
double gekko_frsqrte(double b) { return approx_rsqrt(b); }

static uint64_t convert_to_integer(double b, int chop) {
  double rounded;
  uint32_t value;
  if (chop) rounded = trunc(b);
  else { const double ip = copysign(4503599627370496.0, b); rounded = (b + ip) - ip; }
  if (isnan(b)) value = 0x80000000u;
  else if (rounded >= 2147483648.0) value = 0x7fffffffu;
  else if (rounded < -2147483648.0) value = 0x80000000u;
  else value = (uint32_t)(int32_t)rounded;
  uint64_t result = 0xfff8000000000000ULL | value;
  if (value == 0 && signbit(b)) result |= 0x100000000ULL;
  return result;
}
uint64_t gekko_fctiw (double b) { return convert_to_integer(b, 0); }
uint64_t gekko_fctiwz(double b) { return convert_to_integer(b, 1); }
double gekko_fsel(double a, double b, double c) { return a >= -0.0 ? c : b; }
double gekko_fneg(double b)  { return u2d(d2u(b) ^ DOUBLE_SIGN); }
double gekko_fabs(double b)  { return u2d(d2u(b) & ~DOUBLE_SIGN); } /* Dolphin: fabs(); identical bits */
double gekko_fnabs(double b) { return u2d(d2u(b) | DOUBLE_SIGN); }
double gekko_fmr(double b)   { return b; }

/* ---- paired ---- */
static inline GekkoPS mk(float a, float b) { GekkoPS r; r.ps0 = (double)a; r.ps1 = (double)b; return r; }
static inline float negf(float t) { return isnan(t) ? t : -t; }

GekkoPS gekko_ps_add(uint32_t f, GekkoPS a, GekkoPS b) { return mk(force_single(f, ni_add(a.ps0, b.ps0)), force_single(f, ni_add(a.ps1, b.ps1))); }
GekkoPS gekko_ps_sub(uint32_t f, GekkoPS a, GekkoPS b) { return mk(force_single(f, ni_sub(a.ps0, b.ps0)), force_single(f, ni_sub(a.ps1, b.ps1))); }
GekkoPS gekko_ps_div(uint32_t f, GekkoPS a, GekkoPS b) { return mk(force_single(f, ni_div(a.ps0, b.ps0)), force_single(f, ni_div(a.ps1, b.ps1))); }
GekkoPS gekko_ps_mul(uint32_t f, GekkoPS a, GekkoPS c) {
  double c0 = force25(c.ps0), c1 = force25(c.ps1);
  return mk(force_single(f, ni_mul(a.ps0, c0)), force_single(f, ni_mul(a.ps1, c1)));
}
FMA_CLONES GekkoPS gekko_ps_madd(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(force_single(f, ni_madd_msub(0, 1, a.ps0, c.ps0, b.ps0)), force_single(f, ni_madd_msub(0, 1, a.ps1, c.ps1, b.ps1)));
}
FMA_CLONES GekkoPS gekko_ps_msub(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(force_single(f, ni_madd_msub(1, 1, a.ps0, c.ps0, b.ps0)), force_single(f, ni_madd_msub(1, 1, a.ps1, c.ps1, b.ps1)));
}
FMA_CLONES GekkoPS gekko_ps_nmadd(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(negf(force_single(f, ni_madd_msub(0, 1, a.ps0, c.ps0, b.ps0))), negf(force_single(f, ni_madd_msub(0, 1, a.ps1, c.ps1, b.ps1))));
}
FMA_CLONES GekkoPS gekko_ps_nmsub(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  double r0 = negf(force_single(f, ni_madd_msub(1, 1, a.ps0, c.ps0, b.ps0)));
  double r1 = negf(force_single(f, ni_madd_msub(1, 1, a.ps1, c.ps1, b.ps1)));
  return mk(isnan(r0) ? r0 : jit_nmsub_zero(r0, a.ps0, force25(c.ps0), b.ps0), isnan(r1) ? r1 : jit_nmsub_zero(r1, a.ps1, force25(c.ps1), b.ps1));
}
GekkoPS gekko_ps_muls0(uint32_t f, GekkoPS a, GekkoPS c) {
  double c0 = force25(c.ps0);
  return mk(force_single(f, ni_mul(a.ps0, c0)), force_single(f, ni_mul(a.ps1, c0)));
}
GekkoPS gekko_ps_muls1(uint32_t f, GekkoPS a, GekkoPS c) {
  double c1 = force25(c.ps1);
  return mk(force_single(f, ni_mul(a.ps0, c1)), force_single(f, ni_mul(a.ps1, c1)));
}
FMA_CLONES GekkoPS gekko_ps_madds0(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(force_single(f, ni_madd_msub(0, 1, a.ps0, c.ps0, b.ps0)), force_single(f, ni_madd_msub(0, 1, a.ps1, c.ps0, b.ps1)));
}
FMA_CLONES GekkoPS gekko_ps_madds1(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(force_single(f, ni_madd_msub(0, 1, a.ps0, c.ps1, b.ps0)), force_single(f, ni_madd_msub(0, 1, a.ps1, c.ps1, b.ps1)));
}
GekkoPS gekko_ps_sum0(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(force_single(f, ni_add(a.ps0, b.ps1)), force_single(f, c.ps1));
}
GekkoPS gekko_ps_sum1(uint32_t f, GekkoPS a, GekkoPS c, GekkoPS b) {
  return mk(force_single(f, c.ps0), force_single(f, ni_add(a.ps0, b.ps1)));
}
GekkoPS gekko_ps_merge00(GekkoPS a, GekkoPS b) { GekkoPS r = { a.ps0, b.ps0 }; return r; }
GekkoPS gekko_ps_merge01(GekkoPS a, GekkoPS b) { GekkoPS r = { a.ps0, b.ps1 }; return r; }
GekkoPS gekko_ps_merge10(GekkoPS a, GekkoPS b) { GekkoPS r = { a.ps1, b.ps0 }; return r; }
GekkoPS gekko_ps_merge11(GekkoPS a, GekkoPS b) { GekkoPS r = { a.ps1, b.ps1 }; return r; }
GekkoPS gekko_ps_res(GekkoPS b) { GekkoPS r = { approx_recip(b.ps0), approx_recip(b.ps1) }; return r; }
GekkoPS gekko_ps_rsqrte(uint32_t f, GekkoPS b) {
  return mk(force_single(f, approx_rsqrt(b.ps0)), force_single(f, approx_rsqrt(b.ps1)));
}
GekkoPS gekko_ps_sel(GekkoPS a, GekkoPS b, GekkoPS c) {
  GekkoPS r = { a.ps0 >= -0.0 ? c.ps0 : b.ps0, a.ps1 >= -0.0 ? c.ps1 : b.ps1 }; return r;
}
GekkoPS gekko_ps_neg (GekkoPS b) { GekkoPS r = { gekko_fneg(b.ps0), gekko_fneg(b.ps1) }; return r; }
GekkoPS gekko_ps_abs (GekkoPS b) { GekkoPS r = { u2d(d2u(b.ps0) & ~DOUBLE_SIGN), u2d(d2u(b.ps1) & ~DOUBLE_SIGN) }; return r; }
GekkoPS gekko_ps_nabs(GekkoPS b) { GekkoPS r = { gekko_fnabs(b.ps0), gekko_fnabs(b.ps1) }; return r; }
GekkoPS gekko_ps_mr  (GekkoPS b) { return b; }

/* ---- conversions (Interpreter_FPUtils.h) ---- */
uint32_t gekko_convert_to_single(uint64_t x) {
  const uint32_t exp = (uint32_t)((x >> 52) & 0x7ff);
  if (exp > 896 || (x & ~DOUBLE_SIGN) == 0) {
    return (uint32_t)(((x >> 32) & 0xc0000000) | ((x >> 29) & 0x3fffffff));
  } else if (exp >= 874) {
    uint32_t t = (uint32_t)(0x80000000u | ((x & DOUBLE_FRAC) >> 21));
    t = t >> (905 - exp);
    t |= (uint32_t)((x >> 32) & 0x80000000);
    return t;
  }
  return (uint32_t)(((x >> 32) & 0xc0000000) | ((x >> 29) & 0x3fffffff));
}
uint32_t gekko_convert_to_single_ftz(uint64_t x) {
  const uint32_t exp = (uint32_t)((x >> 52) & 0x7ff);
  if (exp > 896 || (x & ~DOUBLE_SIGN) == 0)
    return (uint32_t)(((x >> 32) & 0xc0000000) | ((x >> 29) & 0x3fffffff));
  return (uint32_t)((x >> 32) & 0x80000000);
}
uint64_t gekko_convert_to_double(uint32_t value) {
  uint64_t x = value, exp = (x >> 23) & 0xff, frac = x & 0x007fffff;
  if (exp > 0 && exp < 255) {
    uint64_t y = !(exp >> 7);
    uint64_t z = y << 61 | y << 60 | y << 59;
    return ((x & 0xc0000000) << 32) | z | ((x & 0x3fffffff) << 29);
  } else if (exp == 0 && frac != 0) {
    exp = 1023 - 126;
    do { frac <<= 1; exp -= 1; } while ((frac & 0x00800000) == 0);
    return ((x & 0x80000000) << 32) | (exp << 52) | ((frac & 0x007fffff) << 29);
  } else {
    uint64_t y = exp >> 7;
    uint64_t z = y << 61 | y << 60 | y << 59;
    return ((x & 0xc0000000) << 32) | z | ((x & 0x3fffffff) << 29);
  }
}

/* ---- psq_l / psq_st ---- */
static inline float deq_scale(unsigned s) { s &= 63; return ldexpf(1.0f, s < 32 ? -(int)s : 64 - (int)s); }
static inline float q_scale(unsigned s)   { s &= 63; return ldexpf(1.0f, s < 32 ? (int)s : (int)s - 64); }

static inline uint32_t rd_be(const uint8_t *p, int n) { uint32_t v = 0; for (int i = 0; i < n; i++) v = (v << 8) | p[i]; return v; }
static inline void wr_be(uint8_t *p, uint32_t v, int n) { for (int i = n - 1; i >= 0; i--) { p[i] = (uint8_t)v; v >>= 8; } }

/* clamp(float(ps)*scale, lo, hi) then convert; NaN -> 0 (Dolphin: UB cast, x86 gives 0 for all
   small types since cvttss2si returns 0x80000000 whose low 16 bits are 0) */
static inline uint32_t sc_clamp(double ps, unsigned scale, float lo, float hi, int is_signed) {
  float v = (float)ps * q_scale(scale);
  if (v != v) return 0;
  if (v < lo) v = lo; else if (hi < v) v = hi;
  return is_signed ? (uint32_t)(int32_t)v : (uint32_t)v;
}

GekkoPS gekko_psq_load(const uint8_t *mem, unsigned type, unsigned scale, unsigned w) {
  GekkoPS r = { 0.0, 0.0 };
  if (type == 0) {
    r.ps0 = u2d(gekko_convert_to_double(rd_be(mem, 4)));
    r.ps1 = w ? 1.0 : u2d(gekko_convert_to_double(rd_be(mem + 4, 4)));
    return r;
  }
  if (type < 4) return r;
  int n = (type == 4 || type == 6) ? 1 : 2;
  int sgn = (type == 6 || type == 7);
  float s = deq_scale(scale);
  uint32_t v0 = rd_be(mem, n);
  float f0 = sgn ? (float)(n == 1 ? (int32_t)(int8_t)v0 : (int32_t)(int16_t)v0) : (float)v0;
  r.ps0 = (double)(f0 * s);
  if (w) { r.ps1 = 1.0; return r; }
  uint32_t v1 = rd_be(mem + n, n);
  float f1 = sgn ? (float)(n == 1 ? (int32_t)(int8_t)v1 : (int32_t)(int16_t)v1) : (float)v1;
  r.ps1 = (double)(f1 * s);
  return r;
}

unsigned gekko_psq_store(uint8_t *mem, GekkoPS v, unsigned type, unsigned scale, unsigned w) {
  if (type == 0) {
    wr_be(mem, gekko_convert_to_single_ftz(d2u(v.ps0)), 4);
    if (w) return 4;
    wr_be(mem + 4, gekko_convert_to_single_ftz(d2u(v.ps1)), 4);
    return 8;
  }
  if (type < 4) return 0;
  float lo, hi; int sgn = 0, n;
  switch (type) {
    case 4: lo = 0.f; hi = 255.f; n = 1; break;
    case 5: lo = 0.f; hi = 65535.f; n = 2; break;
    case 6: lo = -128.f; hi = 127.f; n = 1; sgn = 1; break;
    default: lo = -32768.f; hi = 32767.f; n = 2; sgn = 1; break;
  }
  wr_be(mem, sc_clamp(v.ps0, scale, lo, hi, sgn), n);
  if (w) return (unsigned)n;
  wr_be(mem + n, sc_clamp(v.ps1, scale, lo, hi, sgn), n);
  return (unsigned)(2 * n);
}
