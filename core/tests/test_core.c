/* test_core: libmelee_core end-to-end checks against the real game. usage: test_core <mem1.bin> <iso> */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <xmmintrin.h>
#include "melee_core.h"

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL %s:%d: %s (%s)\n", __FILE__, __LINE__, #x, mc_error()); exit(1); } } while (0)

static uint32_t boot_and_run(const char *mem1, const char *iso, int frames) {
  CHECK(mc_init(mem1, iso) == 0);
  for (int i = 0; i < frames; i++) CHECK(mc_step() == 0);
  return mc_mem_crc32();
}

int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: test_core <mem1.bin> <iso>\n"); return 2; }
  const char *mem1 = argv[1], *iso = argv[2];
  /* errors, no machine */
  CHECK(mc_step() == -1);
  CHECK(mc_init("/nonexistent/mem1.bin", iso) == -1 && strstr(mc_error(), "cannot open"));
  CHECK(mc_init(mem1, mem1) == -1 && strstr(mc_error(), "not Melee NTSC 1.02"));
  char bad[4096];
  snprintf(bad, sizeof bad, "%s.bad", mem1);
  {
    FILE *in = fopen(mem1, "rb"), *out = fopen(bad, "wb");
    CHECK(in && out);
    int ch, n = 0;
    while ((ch = fgetc(in)) != EOF) fputc(n++ == 0x3100 ? ch ^ 0xFF : ch, out);
    fclose(in);
    fclose(out);
  }
  CHECK(mc_init(bad, iso) == -1 && strstr(mc_error(), "does not match"));
  remove(bad);
  /* boot, host MXCSR untouched */
  _mm_setcsr(0x1F80u);
  uint32_t h1 = boot_and_run(mem1, iso, 300);
  CHECK(_mm_getcsr() == 0x1F80u);
  CHECK(mc_frame() == 301);
  CHECK((mc_peek32(0x80479D30u) >> 24) < 0x2Du);   /* state_machine.curr_mode is a valid GameModeKind */
  /* determinism across shutdown / re-init */
  mc_shutdown();
  CHECK(mc_step() == -1);
  uint32_t h2 = boot_and_run(mem1, iso, 300);
  CHECK(h1 == h2);
  puts("core tests passed");
  mc_shutdown();
  return 0;
}
