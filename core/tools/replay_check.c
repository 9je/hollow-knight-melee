/* replay_check: run a packed replay (see tools/src/ssbmhk_tools/replaycheck.py) through libmelee_core
 * and print every fighter's state after every frame. usage: replay_check <mem1.bin> <iso> <run file> */
#define _POSIX_C_SOURCE 199309L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include "melee_core.h"

static uint32_t be32(const uint8_t *p) { return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3]; }
static uint32_t bits(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }

int main(int argc, char **argv) {
  if (argc != 4) { fprintf(stderr, "usage: replay_check <mem1.bin> <iso> <run file>\n"); return 2; }
  FILE *f = fopen(argv[3], "rb");
  if (!f) { fprintf(stderr, "cannot open %s\n", argv[3]); return 2; }
  fseek(f, 0, SEEK_END);
  long size = ftell(f);
  fseek(f, 0, SEEK_SET);
  uint8_t *run = malloc((size_t)size);
  if (!run || fread(run, 1, (size_t)size, f) != (size_t)size) { fprintf(stderr, "cannot read %s\n", argv[3]); return 2; }
  fclose(f);
  const size_t hdr = 8 + MC_START_SIZE + 16;
  if ((size_t)size < hdr || memcmp(run, "MCRUN2\0\0", 8) != 0) { fprintf(stderr, "not a run file\n"); return 2; }
  const uint8_t *start = run + 8, *h = run + 8 + MC_START_SIZE;
  uint32_t seed = be32(h), nframes = be32(h + 8);
  int32_t first = (int32_t)be32(h + 4);
  uint8_t mask = h[12], flags = h[13];
  if ((size_t)size != hdr + (size_t)nframes * 32u) { fprintf(stderr, "run file size mismatch\n"); return 2; }
  if (mc_init(argv[1], argv[2]) != 0 || mc_start_match(start, seed, flags) != 0) { fprintf(stderr, "%s\n", mc_error()); return 1; }
  struct timespec t0, t1;
  clock_gettime(CLOCK_MONOTONIC, &t0);
  for (uint32_t k = 0; k < nframes; k++) {
    const uint8_t *in = run + hdr + 32u * k;
    for (int p = 0; p < 4; p++, in += 8) {
      if (!(mask & (1u << p))) { mc_set_input(p, NULL); continue; }
      McInput mi = { (uint16_t)(in[0] << 8 | in[1]), (int8_t)in[2], (int8_t)in[3], (int8_t)in[4], (int8_t)in[5], in[6], in[7] };
      mc_set_input(p, &mi);
    }
    if (mc_step() != 0) { fprintf(stderr, "frame %d: %s\n", first + (int32_t)k, mc_error()); return 1; }
    for (int p = 0; p < 4; p++) {
      if (!(mask & (1u << p))) continue;
      for (int fo = 0; fo < 2; fo++) {
        McFighter s;
        if (mc_read_fighter(p, fo, &s) != 0) continue;
        printf("%d %d %d %u %08X %08X %08X %08X %08X %08X %08X %08X %08X %08X %u\n", first + (int32_t)k, p, fo, s.action,
               bits(s.x), bits(s.y), bits(s.facing), bits(s.percent), bits(s.shield), bits(s.self_vel_x),
               bits(s.self_vel_y), bits(s.kb_vel_x), bits(s.kb_vel_y), bits(s.ground_vel), s.airborne);
      }
    }
  }
  clock_gettime(CLOCK_MONOTONIC, &t1);
  double secs = (double)(t1.tv_sec - t0.tv_sec) + (double)(t1.tv_nsec - t0.tv_nsec) / 1e9;
  fprintf(stderr, "# %u frames in %.2f s (%.0f frames/s)\n", nframes, secs, nframes / secs);
  mc_shutdown();
  return 0;
}
