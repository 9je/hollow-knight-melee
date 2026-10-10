// Differential fuzzer: Dolphin's own Interpreter_*.cpp (compiled unmodified against stubs) vs gekko_fp.c
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <functional>
#include <string>
#include <vector>
#include <xmmintrin.h>
#include "Core/PowerPC/Interpreter/Interpreter.h"
#include "Common/CPUDetect.h"
#include "build/Interpreter_FPUtils.h"
extern "C" {
#include "gekko_fp.h"
}
CPUInfo cpu_info;

static uint64_t rs[2];
static inline uint64_t rnd() { // xorshift128+
  uint64_t s1 = rs[0], s0 = rs[1]; rs[0] = s0; s1 ^= s1 << 23;
  rs[1] = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5); return rs[1] + s0;
}
static inline double u2d(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }
static inline uint64_t d2u(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static inline double f2d(uint32_t u) { float f; memcpy(&f, &u, 4); return (double)f; }

static const uint64_t SPECIAL[] = {
  0x0000000000000000ull, 0x8000000000000000ull, 0x3ff0000000000000ull, 0xbff0000000000000ull,
  0x7ff0000000000000ull, 0xfff0000000000000ull, 0x7ff8000000000000ull, 0xfff8000000000000ull,
  0x7ff0000000000001ull, 0xfff0000000000001ull, 0x7ff4000000000000ull, 0x7ff7ffffffffffffull,
  0x7ffffffffffffffull|0x7ff8000000000000ull, 0x7ff800000deadbeeull, 0x0000000000000001ull,
  0x000fffffffffffffull, 0x0010000000000000ull, 0x8000000000000001ull, 0x800fffffffffffffull,
  0x47efffffe0000000ull /*FLT_MAX*/, 0x47efffffefffffffull, 0x47effffff0000000ull, 0x47f0000000000000ull,
  0x3810000000000000ull /*FLT_MIN*/, 0x380fffffe0000000ull, 0x380fffffffffffffull, 0x3800000000000000ull,
  0x36a0000000000000ull /*min denorm single*/, 0x3690000000000000ull, 0x36a0000000000001ull,
  0x7fefffffffffffffull, 0xffefffffffffffffull, 0x41dfffffffc00000ull /*2^31-1*/, 0x41e0000000000000ull,
  0xc1e0000000000000ull, 0xc1e0000000200000ull, 0x4330000000000000ull, 0x3fe0000000000000ull,
  0x3ff8000000000000ull, 0x4004000000000000ull, 0xbfe0000000000000ull,
};
static const int NSPECIAL = sizeof(SPECIAL) / sizeof(SPECIAL[0]);

// random operand generator covering: random bits, widened singles, special values, near single
// overflow/underflow, ties, perturbed specials, small-range ints/fractions.
static double gen() {
  uint64_t r = rnd();
  switch (r % 16) {
  case 0: case 1: return u2d(rnd());
  case 2: case 3: return f2d((uint32_t)rnd());
  case 4: case 5: return u2d(SPECIAL[(r >> 8) % NSPECIAL]);
  case 6: { uint64_t b = SPECIAL[(r >> 8) % NSPECIAL]; return u2d(b ^ (1ull << ((r >> 16) % 64))); }
  case 7: { // near single overflow/underflow region, random mantissa
    int e = (r >> 8) & 1 ? 1023 + 127 + (int)((r >> 9) % 5) - 2 : 1023 - 126 - (int)((r >> 9) % 30) + 2;
    return u2d(((r >> 20) & 0x8000000000000000ull) | ((uint64_t)e << 52) | (rnd() & 0xfffffffffffffull)); }
  case 8: { // single-ish tie: float mantissa + exactly half ulp (bit 28) +/- tiny
    uint32_t f = (uint32_t)rnd(); double d = f2d(f);
    uint64_t b = d2u(d); if (((b >> 52) & 0x7ff) == 0x7ff) return d;
    b |= (1ull << 28); if (r >> 40 & 1) b += (r >> 41) & 3; if (r >> 44 & 1) b ^= 1ull << (r>>45&0x1f);
    return u2d(b); }
  case 9: { // moderate range with fraction (fctiw/psq): +-2^k * rand
    double m = (double)(int64_t)(rnd() >> 11) / 9007199254740992.0 * 2.0 - 1.0;
    return std::ldexp(m, (int)((r >> 8) % 70) - 20); }
  case 10: { // x.5 ties and integers
    double v = (double)((int64_t)(rnd() % 6000000001ull) - 3000000000ll);
    return (r >> 8 & 1) ? v + 0.5 : ((r >> 9 & 1) ? v : v + 0.25); }
  case 11: return u2d(rnd() & 0x800fffffffffffffull); // denormal doubles
  case 12: return u2d((rnd() & 0x8007ffffffffffffull) | 0x7ff0000000000000ull | ((r>>8&1)?0:0)); // NaN/inf, mostly sNaN
  case 13: { uint32_t f = (uint32_t)rnd(); f &= (f & 0x00800000) ? 0xfff80000 : 0xffffe000; return f2d(f); } // few-bit singles
  case 14: return f2d((uint32_t)rnd() & 0x807fffffu); // single denormals
  default: return u2d(rnd());
  }
}
// FMA-tie targeted operands: a (12 bits) * c (13 bits) = 25-bit product (exact float tie), b tiny
static void gen_tie(double &a, double &c, double &b) {
  uint32_t fa = 0x3f800000u | ((uint32_t)rnd() & 0x7ff000u) | 0x1000u; // 12 frac bits... lsb at bit 12
  uint32_t fc = 0x3f800000u | ((uint32_t)rnd() & 0x7fe000u) | 0x2000u;
  fa = (fa & ~0x7f800000u) | ((127 + (int)(rnd() % 40) - 20) << 23) | ((rnd() & 1) << 31);
  fc = (fc & ~0x7f800000u) | ((127 + (int)(rnd() % 40) - 20) << 23) | ((rnd() & 1) << 31);
  a = f2d(fa); c = f2d(fc);
  double p = a * c;
  int e; std::frexp(p, &e);
  double tiny = std::ldexp((double)(1 + rnd() % 7), e - 54 - (int)(rnd() % 8));
  b = (rnd() & 1) ? tiny : -tiny;
  if (rnd() % 4 == 0) b = 0.0;
  if (rnd() % 4 == 0) { // flip operand order roles / add a double-precision-ish perturbation
    a = u2d(d2u(a) + (rnd() & 1)); }
}

static void set_mxcsr(bool ni) { _mm_setcsr(0x1F80 | (ni ? 0x8000 : 0)); }

struct Stats { uint64_t n = 0, bad = 0, hash = 0xcbf29ce484222325ull; };
static inline void hmix(uint64_t &h, uint64_t v) { h = (h ^ v) * 0x100000001b3ull; h ^= h >> 29; }

struct Op {
  std::string name; int nargs;
  void (*dol)(Interpreter&, UGeckoInstruction);
  std::function<GekkoPS(uint32_t, GekkoPS, GekkoPS, GekkoPS)> mine;  // (fpscr, a, b, c)
  bool both;      // compare ps1 too (else expect FD.ps1 unchanged)
  bool tie_gen;   // add tie-targeted vectors
};

#define SENT 0x123456789abcdef0ull
static GekkoPS S(double a, double b) { return GekkoPS{a, b}; }
static GekkoPS only0(double x) { return GekkoPS{x, u2d(SENT)}; }

int main(int argc, char **argv) {
  long N = argc > 1 ? atol(argv[1]) : 10000000;
  Interpreter ip;
  auto &st = ip.m_ppc_state;
  const int FA = 1, FB = 2, FC = 3, FD = 4;
  UGeckoInstruction inst; inst.hex = 0; inst.FA = FA; inst.FB = FB; inst.FC = FC; inst.FD = FD; inst.Rc = 0;

  std::vector<Op> ops;
  #define OP(nm, fn, args, both, tie, expr) ops.push_back({nm, args, &Interpreter::fn, [](uint32_t f, GekkoPS a, GekkoPS b, GekkoPS c) -> GekkoPS { (void)f;(void)a;(void)b;(void)c; return expr; }, both, tie});
  OP("fadd",   faddx,   2, false, false, only0(gekko_fadd(f, a.ps0, b.ps0)))
  OP("fsub",   fsubx,   2, false, false, only0(gekko_fsub(f, a.ps0, b.ps0)))
  OP("fmul",   fmulx,   2, false, false, only0(gekko_fmul(f, a.ps0, c.ps0)))
  OP("fdiv",   fdivx,   2, false, false, only0(gekko_fdiv(f, a.ps0, b.ps0)))
  OP("fadds",  faddsx,  2, true,  false, gekko_fadds(f, a.ps0, b.ps0))
  OP("fsubs",  fsubsx,  2, true,  false, gekko_fsubs(f, a.ps0, b.ps0))
  OP("fmuls",  fmulsx,  2, true,  false, gekko_fmuls(f, a.ps0, c.ps0))
  OP("fdivs",  fdivsx,  2, true,  false, gekko_fdivs(f, a.ps0, b.ps0))
  OP("fmadd",  fmaddx,  3, false, false, only0(gekko_fmadd(f, a.ps0, c.ps0, b.ps0)))
  OP("fmsub",  fmsubx,  3, false, false, only0(gekko_fmsub(f, a.ps0, c.ps0, b.ps0)))
  OP("fnmadd", fnmaddx, 3, false, false, only0(gekko_fnmadd(f, a.ps0, c.ps0, b.ps0)))
  OP("fnmsub", fnmsubx, 3, false, false, only0(gekko_fnmsub(f, a.ps0, c.ps0, b.ps0)))
  OP("fmadds", fmaddsx, 3, true,  true,  gekko_fmadds(f, a.ps0, c.ps0, b.ps0))
  OP("fmsubs", fmsubsx, 3, true,  true,  gekko_fmsubs(f, a.ps0, c.ps0, b.ps0))
  OP("fnmadds",fnmaddsx,3, true,  true,  gekko_fnmadds(f, a.ps0, c.ps0, b.ps0))
  OP("fnmsubs",fnmsubsx,3, true,  true,  gekko_fnmsubs(f, a.ps0, c.ps0, b.ps0))
  OP("frsp",   frspx,   1, true,  false, gekko_frsp(f, b.ps0))
  OP("fres",   fresx,   1, true,  false, gekko_fres(b.ps0))
  OP("frsqrte",frsqrtex,1, false, false, only0(gekko_frsqrte(b.ps0)))
  OP("fctiw",  fctiwx,  1, false, false, only0(u2d(gekko_fctiw(b.ps0))))
  OP("fctiwz", fctiwzx, 1, false, false, only0(u2d(gekko_fctiwz(b.ps0))))
  OP("fsel",   fselx,   3, false, false, only0(gekko_fsel(a.ps0, b.ps0, c.ps0)))
  OP("fneg",   fnegx,   1, false, false, only0(gekko_fneg(b.ps0)))
  OP("fabs",   fabsx,   1, false, false, only0(gekko_fabs(b.ps0)))
  OP("fnabs",  fnabsx,  1, false, false, only0(gekko_fnabs(b.ps0)))
  OP("fmr",    fmrx,    1, false, false, only0(gekko_fmr(b.ps0)))
  OP("ps_add", ps_add,  2, true, false, gekko_ps_add(f, a, b))
  OP("ps_sub", ps_sub,  2, true, false, gekko_ps_sub(f, a, b))
  OP("ps_mul", ps_mul,  2, true, false, gekko_ps_mul(f, a, c))
  OP("ps_div", ps_div,  2, true, false, gekko_ps_div(f, a, b))
  OP("ps_madd", ps_madd, 3, true, true, gekko_ps_madd(f, a, c, b))
  OP("ps_msub", ps_msub, 3, true, true, gekko_ps_msub(f, a, c, b))
  OP("ps_nmadd",ps_nmadd,3, true, true, gekko_ps_nmadd(f, a, c, b))
  OP("ps_nmsub",ps_nmsub,3, true, true, gekko_ps_nmsub(f, a, c, b))
  OP("ps_muls0",ps_muls0,2, true, false, gekko_ps_muls0(f, a, c))
  OP("ps_muls1",ps_muls1,2, true, false, gekko_ps_muls1(f, a, c))
  OP("ps_madds0",ps_madds0,3,true, true, gekko_ps_madds0(f, a, c, b))
  OP("ps_madds1",ps_madds1,3,true, true, gekko_ps_madds1(f, a, c, b))
  OP("ps_sum0", ps_sum0, 3, true, false, gekko_ps_sum0(f, a, c, b))
  OP("ps_sum1", ps_sum1, 3, true, false, gekko_ps_sum1(f, a, c, b))
  OP("ps_merge00",ps_merge00,2,true,false, gekko_ps_merge00(a, b))
  OP("ps_merge01",ps_merge01,2,true,false, gekko_ps_merge01(a, b))
  OP("ps_merge10",ps_merge10,2,true,false, gekko_ps_merge10(a, b))
  OP("ps_merge11",ps_merge11,2,true,false, gekko_ps_merge11(a, b))
  OP("ps_res",  ps_res,  1, true, false, gekko_ps_res(b))
  OP("ps_rsqrte",ps_rsqrte,1,true, false, gekko_ps_rsqrte(f, b))
  OP("ps_sel", ps_sel, 3, true, false, gekko_ps_sel(a, b, c))
  OP("ps_neg", ps_neg, 1, true, false, gekko_ps_neg(b))
  OP("ps_abs", ps_abs, 1, true, false, gekko_ps_abs(b))
  OP("ps_nabs",ps_nabs,1, true, false, gekko_ps_nabs(b))
  OP("ps_mr",  ps_mr,  1, true, false, gekko_ps_mr(b))

  int total_bad = 0;
  uint64_t gh_ref = 0xcbf29ce484222325ull, gh_mine = 0xcbf29ce484222325ull;
  printf("%-12s %-5s %12s %10s\n", "op", "NI", "cases", "mismatch");
  for (int ni = 0; ni < 2; ni++) {
    st.fpscr = ni ? 0x4u : 0u;
    set_mxcsr(ni);
    for (auto &op : ops) {
      rs[0] = 0x9e3779b97f4a7c15ull ^ std::hash<std::string>{}(op.name); rs[1] = 0xbf58476d1ce4e5b9ull + ni;
      for (int i = 0; i < 20; i++) rnd();
      Stats s; int shown = 0;
      for (long i = 0; i < N; i++) {
        GekkoPS a{gen(), gen()}, b{gen(), gen()}, c{gen(), gen()};
        if (op.tie_gen && (i & 3) == 0) { gen_tie(a.ps0, c.ps0, b.ps0); gen_tie(a.ps1, c.ps1, b.ps1);
          if (i & 4) { double t = c.ps1; c.ps1 = c.ps0; c.ps0 = t; } }
        // some ops want a = b = c relations occasionally (sel, cancellation)
        if ((i % 16) == 5) b = a; else if ((i % 16) == 6) { b = a; b.ps0 = -b.ps0; b.ps1 = -b.ps1; }
        st.ps[FA].SetBoth(a.ps0, a.ps1); st.ps[FB].SetBoth(b.ps0, b.ps1); st.ps[FC].SetBoth(c.ps0, c.ps1);
        st.ps[FD].SetBoth(u2d(SENT), u2d(SENT));
        op.dol(ip, inst);
        uint64_t r0 = st.ps[FD].ps0, r1 = st.ps[FD].ps1;
        GekkoPS m = op.mine(st.fpscr.Hex, a, b, c);
        uint64_t m0 = d2u(m.ps0), m1 = d2u(m.ps1);
        s.n++;
        hmix(gh_ref, r0); hmix(gh_ref, r1); hmix(gh_mine, m0); hmix(gh_mine, m1);
        bool bad = (r0 != m0) || (op.both ? (r1 != m1) : (r1 != SENT));
        if (bad) { s.bad++; if (shown++ < 3)
          printf("  MISMATCH %s ni=%d a=%016llx,%016llx b=%016llx,%016llx c=%016llx,%016llx dolphin=%016llx,%016llx mine=%016llx,%016llx\n",
            op.name.c_str(), ni, (unsigned long long)d2u(a.ps0),(unsigned long long)d2u(a.ps1),(unsigned long long)d2u(b.ps0),(unsigned long long)d2u(b.ps1),
            (unsigned long long)d2u(c.ps0),(unsigned long long)d2u(c.ps1),(unsigned long long)r0,(unsigned long long)r1,(unsigned long long)m0,(unsigned long long)m1); }
      }
      printf("%-12s %-5d %12llu %10llu\n", op.name.c_str(), ni, (unsigned long long)s.n, (unsigned long long)s.bad);
      total_bad += (int)s.bad;
    }
  }
  // ---- psq_l / psq_st ----
  set_mxcsr(false); st.fpscr = 0;
  st.spr[SPR_HID2] |= 0x80000000u;  // LSQE
  inst.hex = 0; inst.RA = 0; inst.I = 0; inst.SIMM_12 = 8; inst.RD = FD;
  for (int w = 0; w < 2; w++) {
    inst.W = w;
    for (int type = 0; type < 8; type++) {
      rs[0] = 0x1234567ull * (type + 1) + w; rs[1] = 0xabcdef12345ull;
      for (int i = 0; i < 20; i++) rnd();
      long bad_l = 0, bad_s = 0, n = 0;
      for (long i = 0; i < N; i++) {
        unsigned scale = (unsigned)(rnd() % 64);
        if (i % 8 == 0) scale = 0;
        uint32_t gqr = (type << 0) | (scale << 8) | (type << 16) | (scale << 24);
        st.spr[SPR_GQR0] = gqr;
        // load
        uint8_t buf[64]; for (int k = 0; k < 64; k++) buf[k] = (uint8_t)rnd();
        if (type == 0 && (i & 1)) { uint32_t f = (uint32_t)rnd(); if (i & 2) f &= 0x807fffff; if (i & 4) f |= 0x7f800000; for (int k = 0; k < 4; k++) buf[8 + k] = f >> (24 - 8 * k); }
        memcpy(&ip.m_mmu.mem[0], buf, 64);
        st.ps[FD].SetBoth(u2d(SENT), u2d(SENT));
        inst.RD = FD; Interpreter::psq_l(ip, inst);
        GekkoPS m = gekko_psq_load(buf + 8, type, scale, w);
        if (d2u(m.ps0) != st.ps[FD].ps0 || d2u(m.ps1) != st.ps[FD].ps1) { bad_l++; if (bad_l <= 3) printf("  psq_l MISMATCH type=%d scale=%u w=%d dol=%016llx,%016llx mine=%016llx,%016llx\n", type, scale, w, (unsigned long long)st.ps[FD].ps0, (unsigned long long)st.ps[FD].ps1, (unsigned long long)d2u(m.ps0), (unsigned long long)d2u(m.ps1)); }
        // store
        GekkoPS v{gen(), gen()};
        if (i % 4 == 1) { v.ps0 = std::ldexp(v.ps0 > 0 ? 1.0 : -1.0, (int)(rnd() % 40) - 10) * (double)(int)(rnd() % 1000) / 7.0; v.ps1 = v.ps0 * 1.0000001; }
        if (i % 4 == 2) { v.ps0 = (double)(float)(((int64_t)(rnd() % 70000) - 35000) / 3.0); v.ps1 = std::trunc(v.ps0) + 0.5; }
        st.ps[FA].SetBoth(v.ps0, v.ps1);
        for (int k = 0; k < 64; k++) ip.m_mmu.mem[k] = 0xA5;
        inst.RS = FA; Interpreter::psq_st(ip, inst);
        uint8_t mem2[64]; memset(mem2, 0xA5, 64);
        gekko_psq_store(mem2 + 8, v, type, scale, w);
        if (memcmp(mem2, &ip.m_mmu.mem[0], 64) != 0) { bad_s++; if (bad_s <= 3) printf("  psq_st MISMATCH type=%d scale=%u w=%d ps=%016llx,%016llx\n", type, scale, w, (unsigned long long)d2u(v.ps0), (unsigned long long)d2u(v.ps1)); }
        n++;
      }
      printf("psq_l  type=%d w=%d %12llu %10llu\n", type, w, (unsigned long long)n, (unsigned long long)bad_l);
      printf("psq_st type=%d w=%d %12llu %10llu\n", type, w, (unsigned long long)n, (unsigned long long)bad_s);
      total_bad += bad_l + bad_s;
    }
  }
  // ---- lfs/stfs conversions vs Dolphin helpers ----
  {
    long bad = 0; rs[0] = 77; rs[1] = 99;
    for (long i = 0; i < N; i++) {
      uint32_t w32 = (uint32_t)rnd(); uint64_t x = d2u(gen());
      if (ConvertToDouble(w32) != gekko_convert_to_double(w32)) bad++;
      if (ConvertToSingle(x) != gekko_convert_to_single(x)) bad++;
      if (ConvertToSingleFTZ(x) != gekko_convert_to_single_ftz(x)) bad++;
    }
    printf("convert(lfs/stfs/stfs-ftz) %llu x3 %llu\n", (unsigned long long)N, (unsigned long long)bad); total_bad += (int)bad;
  }
  printf("RESULT-HASH dolphin=%016llx mine=%016llx total_mismatch=%d\n", (unsigned long long)gh_ref, (unsigned long long)gh_mine, total_bad);
  return total_bad != 0;
}
