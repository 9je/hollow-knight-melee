/* gekko_soft_fma (musl's) against the C library's fma (glibc: exactly rounded) on random and edge-case operands. */
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
double gekko_soft_fma(double x, double y, double z);
static uint64_t s = 88172645463325252ull;
static uint64_t next(void) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return s; }
static double bits(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }
static uint64_t ubits(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static double pick(void) {
  static const double edge[] = {0.0, -0.0, 1.0, -1.0, INFINITY, -INFINITY, NAN, 0x1p-1074, 0x1p-1022, 0x1.fffffffffffffp1023, 0x1p-537, 0x1p512};
  uint64_t r = next();
  if (r % 16 == 0) return edge[(r >> 8) % (sizeof edge / sizeof edge[0])];
  if (r % 4 == 0) return (double)(float)bits(next());   /* single-precision values, as the paired singles give */
  if (r % 4 == 1) return bits((next() & 0x800FFFFFFFFFFFFFull) | ((uint64_t)(1023 + (int)(next() % 60) - 30) << 52));   /* near 1 */
  return bits(next());
}
int main(void) {
  long bad = 0, zeros = 0;
  for (long i = 0; i < 20000000; i++) {
    double a = pick(), b = pick(), c = pick();
    double want = fma(a, b, c), got = gekko_soft_fma(a, b, c);
    if (isnan(want) ? !isnan(got) : ubits(want) != ubits(got)) {
      if (want == 0 && got == 0) { if (zeros++ < 3) printf("zero sign: fma(%a, %a, %a): %a, want %a\n", a, b, c, got, want); continue; }
      if (bad++ < 5) printf("fma(%a, %a, %a): %a, want %a\n", a, b, c, got, want);
    }
  }
  printf("soft fma: %ld mismatches, %ld zero signs\n", bad, zeros);
  return bad != 0;
}
