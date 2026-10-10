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
  /* solo Fox on Final Destination */
  uint8_t start[MC_START_SIZE];
  mc_solo_fox_start(start);
  CHECK(start[0] == 0x10 && start[0x0F] == 0x20 && start[0x60] == 0x02 && start[0x61] == 0x00 && start[0x85] == 0x03);
  CHECK(mc_start_match(start, 1, 0) == 0);
  CHECK(mc_start_match(start, 1, 0) == -1 && strstr(mc_error(), "already"));
  McInput idle = {0}, right = {0};
  right.stick_x = 127;
  mc_set_input(0, &idle);
  for (int p = 1; p < 4; p++) mc_set_input(p, NULL);
  for (int i = 0; i < 180; i++) CHECK(mc_step() == 0);     /* past the 123-frame countdown */
  McFighter fox;
  CHECK(mc_read_fighter(0, 0, &fox) == 0);
  CHECK(fox.action == 14 && fox.airborne == 0);           /* Wait, on the stage */
  CHECK(mc_read_fighter(1, 0, &fox) == 1);                /* port 2 empty */
  CHECK(mc_read_fighter(0, 1, &fox) == 1);                /* Fox has no follower */
  CHECK(mc_read_fighter(4, 0, &fox) == -1);
  CHECK(mc_read_fighter(0, 0, &fox) == 0);
  float x0 = fox.x;
  mc_set_input(0, &right);
  for (int i = 0; i < 30; i++) CHECK(mc_step() == 0);
  CHECK(mc_read_fighter(0, 0, &fox) == 0);
  CHECK(fox.x > x0 + 10.0f && fox.action >= 20 && fox.action <= 22);   /* Dash / Run / RunDirect */
  for (int i = 0; i < 600; i++) CHECK(mc_step() == 0);     /* a one-player match keeps running */
  CHECK(mc_read_fighter(0, 0, &fox) == 0);
  /* determinism across shutdown / re-init */
  mc_shutdown();
  CHECK(mc_step() == -1);
  uint32_t h2 = boot_and_run(mem1, iso, 300);
  CHECK(h1 == h2);
  /* an online match: Slippi's SyncRNG proc sets seed = rotl(global frame, 16) + the start seed every frame.
   * Checked over the first frames on Battlefield, where nothing draws a random number after the proc yet
   * (FD's background particles do, at once). */
  start[0x0F] = 0x1F;
  CHECK(mc_start_match(start, 0x37E0u, MC_ONLINE_RNG) == 0);
  for (uint32_t gf = 0; gf < 5; gf++) {   /* the proc runs before the frame advances the global frame counter */
    CHECK(mc_step() == 0);
    CHECK(mc_peek32(0x80479D60u) == gf + 1 && mc_peek32(0x804D5F90u) == ((gf << 16 | gf >> 16) + 0x37E0u));
  }
  puts("core tests passed");
  mc_shutdown();
  return 0;
}
