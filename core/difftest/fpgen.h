/* fpgen.h - value generators for the differential fuzzers (ported from spikes/gekko-fp/harness.cpp). */
#ifndef FPGEN_H
#define FPGEN_H
#include <math.h>
#include <stdint.h>
#include <string.h>

typedef struct { uint64_t s[2]; } Rng;
static inline uint64_t rng_next(Rng *r) { /* xorshift128+ */
  uint64_t s1 = r->s[0], s0 = r->s[1];
  r->s[0] = s0; s1 ^= s1 << 23;
  r->s[1] = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);
  return r->s[1] + s0;
}
static inline void rng_seed(Rng *r, uint64_t a, uint64_t b) {
  r->s[0] = a * 0x9E3779B97F4A7C15ull + 0x632BE59BD9B4E019ull;
  r->s[1] = (b + 1) * 0xBF58476D1CE4E5B9ull ^ 0x94D049BB133111EBull;
  for (int i = 0; i < 8; i++) rng_next(r);
}
static inline double fg_u2d(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }
static inline uint64_t fg_d2u(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static inline double fg_f2d(uint32_t u) { float f; memcpy(&f, &u, 4); return (double)f; }

static const uint64_t FG_SPECIAL[] = {
  0x0000000000000000ull, 0x8000000000000000ull, 0x3ff0000000000000ull, 0xbff0000000000000ull,
  0x7ff0000000000000ull, 0xfff0000000000000ull, 0x7ff8000000000000ull, 0xfff8000000000000ull,
  0x7ff0000000000001ull, 0xfff0000000000001ull, 0x7ff4000000000000ull, 0x7ff7ffffffffffffull,
  0x7fffffffffffffffull, 0x7ff800000deadbeeull, 0x0000000000000001ull,
  0x000fffffffffffffull, 0x0010000000000000ull, 0x8000000000000001ull, 0x800fffffffffffffull,
  0x47efffffe0000000ull, 0x47efffffefffffffull, 0x47effffff0000000ull, 0x47f0000000000000ull,
  0x3810000000000000ull, 0x380fffffe0000000ull, 0x380fffffffffffffull, 0x3800000000000000ull,
  0x36a0000000000000ull, 0x3690000000000000ull, 0x36a0000000000001ull,
  0x7fefffffffffffffull, 0xffefffffffffffffull, 0x41dfffffffc00000ull, 0x41e0000000000000ull,
  0xc1e0000000000000ull, 0xc1e0000000200000ull, 0x4330000000000000ull, 0x3fe0000000000000ull,
  0x3ff8000000000000ull, 0x4004000000000000ull, 0xbfe0000000000000ull,
};
#define FG_NSPECIAL ((int)(sizeof FG_SPECIAL / sizeof FG_SPECIAL[0]))

/* Random FPR bit pattern: random bits, widened singles, specials, overflow/underflow windows, ties. */
static inline uint64_t gen_double_bits(Rng *g) {
  uint64_t r = rng_next(g);
  switch (r % 16) {
  case 0: case 1: return rng_next(g);
  case 2: case 3: return fg_d2u(fg_f2d((uint32_t)rng_next(g)));
  case 4: case 5: return FG_SPECIAL[(r >> 8) % FG_NSPECIAL];
  case 6: return FG_SPECIAL[(r >> 8) % FG_NSPECIAL] ^ (1ull << ((r >> 16) % 64));
  case 7: {
    int e = (r >> 8) & 1 ? 1023 + 127 + (int)((r >> 9) % 5) - 2 : 1023 - 126 - (int)((r >> 9) % 30) + 2;
    return ((r >> 20) & 0x8000000000000000ull) | ((uint64_t)e << 52) | (rng_next(g) & 0xfffffffffffffull);
  }
  case 8: {
    uint64_t b = fg_d2u(fg_f2d((uint32_t)rng_next(g)));
    if (((b >> 52) & 0x7ff) == 0x7ff) return b;
    b |= 1ull << 28;
    if (r >> 40 & 1) b += (r >> 41) & 3;
    if (r >> 44 & 1) b ^= 1ull << (r >> 45 & 0x1f);
    return b;
  }
  case 9: {
    double m = (double)(int64_t)(rng_next(g) >> 11) / 9007199254740992.0 * 2.0 - 1.0;
    return fg_d2u(ldexp(m, (int)((r >> 8) % 70) - 20));
  }
  case 10: {
    double v = (double)((int64_t)(rng_next(g) % 6000000001ull) - 3000000000ll);
    return fg_d2u((r >> 8 & 1) ? v + 0.5 : ((r >> 9 & 1) ? v : v + 0.25));
  }
  case 11: return rng_next(g) & 0x800fffffffffffffull;
  case 12: return (rng_next(g) & 0x8007ffffffffffffull) | 0x7ff0000000000000ull;
  case 13: { uint32_t f = (uint32_t)rng_next(g); f &= (f & 0x00800000) ? 0xfff80000 : 0xffffe000; return fg_d2u(fg_f2d(f)); }
  case 14: return fg_d2u(fg_f2d((uint32_t)rng_next(g) & 0x807fffffu));
  default: return rng_next(g);
  }
}

/* Random GPR value: edge values, small values, powers of two +-1, random. */
static inline uint32_t gen_u32(Rng *g) {
  static const uint32_t E[] = {0, 1, 0xFFFFFFFFu, 0x7FFFFFFFu, 0x80000000u, 2, 31, 32, 33, 63, 64, 0xFFFFFFFEu, 0x80000001u};
  uint64_t r = rng_next(g);
  switch (r % 8) {
  case 0: return E[(r >> 8) % (sizeof E / sizeof E[0])];
  case 1: return (uint32_t)((r >> 8) % 64);
  case 2: return (uint32_t)((int32_t)((r >> 8) % 129) - 64);
  case 3: { uint32_t p = 1u << ((r >> 8) % 32); return p + (uint32_t)((int)((r >> 16) % 3) - 1); }
  case 4: return fg_d2u(fg_f2d((uint32_t)rng_next(g))) >> 32;
  default: return (uint32_t)rng_next(g);
  }
}
#endif
