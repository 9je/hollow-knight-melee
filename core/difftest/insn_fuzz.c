/* insn_fuzz.c - per-instruction differential fuzz: Dolphin's interpreter vs recompiled C.
 * usage: insn_fuzz <states-per-case> [seed] */
#include <stdio.h>
#include <stdlib.h>
#include <xmmintrin.h>
#include "cpu.h"
#include "fpgen.h"
#include "insn_case.h"
#include "oracle.h"
#include "test_hooks.h"

#define WIN 0x80100000u
#define WIN_SIZE 0x1000u
#define WIN_OFF (WIN & 0x1FFFFFFFu)

static void rand_state(Rng *g, Cpu *c) {
  for (int i = 0; i < 32; i++) {
    c->gpr[i] = gen_u32(g);
    c->fpr[i].ps0 = u2f(gen_double_bits(g));
    c->fpr[i].ps1 = u2f(gen_double_bits(g));
  }
  c->cr = (uint32_t)rng_next(g);
  c->spr[SPR_XER] = (uint32_t)rng_next(g) & XER_MASK;
  c->spr[SPR_LR] = gen_u32(g);
  c->spr[SPR_CTR] = gen_u32(g);
  for (int q = 0; q < 8; q++) {
    static const uint32_t T[] = {0, 4, 5, 6, 7};
    uint64_t r = rng_next(g);
    uint32_t lt = r % 10 ? T[(r >> 8) % 5] : 1 + (r >> 8) % 3, st = r % 7 ? T[(r >> 12) % 5] : 1 + (r >> 12) % 3;
    c->spr[SPR_GQR0 + q] = (((uint32_t)(r >> 16) & 63) << 24) | (lt << 16) | (((uint32_t)(r >> 24) & 63) << 8) | st;
  }
  c->msr = 0x00002032u;
  c->spr[SPR_HID2] = 0xE0000000u;
  c->spr[SPR_HID0] = 0x0011C464u;
  c->fpscr = ((uint32_t)rng_next(g) & 0xFFFFFF00u) | ((rng_next(g) & 1) ? GEKKO_FPSCR_NI : 0u);
  for (int i = 0; i < 16; i++) c->sr[i] = (uint32_t)rng_next(g);
  for (uint32_t i = 0; i < WIN_SIZE; i += 8) { uint64_t v = rng_next(g); memcpy(c->mem + WIN_OFF + i, &v, 8); }
}

static void place_ea(Rng *g, const InsnCase *k, Cpu *c) {
  uint32_t t = 0x80100100u + 8u * (uint32_t)(rng_next(g) % 0x1A0);
  if (rng_next(g) % 4 == 0) t += (uint32_t)(rng_next(g) & 7);  /* misaligned EAs: alignment exceptions */
  switch (k->ea_kind) {
  case 1: case 3: c->gpr[k->ra] = t - (uint32_t)k->disp; break;
  case 2: case 4:
    if (k->ra == 0) c->gpr[k->rb] = t;
    else if (k->ra == k->rb) c->gpr[k->ra] = t / 2;
    else c->gpr[k->ra] = t - c->gpr[k->rb];
    break;
  }
}

/* faulted: DAR/DSISR are exception-handler state Dolphin sets on a fault; not compared then. */
static int diff(const Cpu *a, const Cpu *b, int faulted, char *out, size_t n) {
  for (int i = 0; i < 32; i++) if (a->gpr[i] != b->gpr[i]) return snprintf(out, n, "r%d dolphin=%08X ours=%08X", i, a->gpr[i], b->gpr[i]);
  for (int i = 0; i < 32; i++) {
    if (f2u(a->fpr[i].ps0) != f2u(b->fpr[i].ps0)) return snprintf(out, n, "f%d.ps0 dolphin=%016llX ours=%016llX", i, (unsigned long long)f2u(a->fpr[i].ps0), (unsigned long long)f2u(b->fpr[i].ps0));
    if (f2u(a->fpr[i].ps1) != f2u(b->fpr[i].ps1)) return snprintf(out, n, "f%d.ps1 dolphin=%016llX ours=%016llX", i, (unsigned long long)f2u(a->fpr[i].ps1), (unsigned long long)f2u(b->fpr[i].ps1));
  }
  if (a->cr != b->cr) return snprintf(out, n, "cr dolphin=%08X ours=%08X", a->cr, b->cr);
  if ((a->spr[SPR_XER] & XER_MASK) != (b->spr[SPR_XER] & XER_MASK)) return snprintf(out, n, "xer dolphin=%08X ours=%08X", a->spr[SPR_XER], b->spr[SPR_XER]);
  for (int i = 0; i < 1024; i++) if (i != SPR_XER && !(faulted && (i == 18 || i == 19)) && a->spr[i] != b->spr[i]) return snprintf(out, n, "spr%d dolphin=%08X ours=%08X", i, a->spr[i], b->spr[i]);
  if (a->msr != b->msr) return snprintf(out, n, "msr dolphin=%08X ours=%08X", a->msr, b->msr);
  if ((a->fpscr & 0xFF) != (b->fpscr & 0xFF)) return snprintf(out, n, "fpscr dolphin=%08X ours=%08X", a->fpscr, b->fpscr);
  for (int i = 0; i < 16; i++) if (a->sr[i] != b->sr[i]) return snprintf(out, n, "sr%d", i);
  for (uint32_t i = 0; i < WIN_SIZE; i++)
    if (a->mem[WIN_OFF + i] != b->mem[WIN_OFF + i]) return snprintf(out, n, "mem %08X dolphin=%02X ours=%02X", WIN + i, a->mem[WIN_OFF + i], b->mem[WIN_OFF + i]);
  return 0;
}

int main(int argc, char **argv) {
  long states = argc > 1 ? atol(argv[1]) : 200;
  uint64_t seed = argc > 2 ? strtoull(argv[2], 0, 0) : 1;
  Cpu *A = calloc(1, sizeof *A), *B = calloc(1, sizeof *B);
  uint8_t *memA = calloc(1, MEM1_SIZE), *memB = calloc(1, MEM1_SIZE);
  unsigned long long total = 0, bad = 0, printed = 0;
  const char *last_op = "";
  unsigned long long op_bad = 0;
  for (size_t k = 0; k < g_ncases; k++) {
    const InsnCase *kc = &g_cases[k];
    if (strcmp(kc->op, last_op) != 0) {
      if (op_bad) printf("  op %s: %llu mismatches\n", last_op, op_bad);
      op_bad = 0; last_op = kc->op;
    }
    for (long s = 0; s < states; s++) {
      Rng g; rng_seed(&g, seed ^ (k * 0x10001ull), (uint64_t)s);
      A->mem = memA;
      rand_state(&g, A);
      place_ea(&g, kc, A);
      Cpu *saved_b_mem = 0; (void)saved_b_mem;
      memcpy(B, A, sizeof *A);
      B->mem = memB;
      memcpy(memB + WIN_OFF, memA + WIN_OFF, WIN_SIZE);
      uint32_t npc;
      int dr = dolphin_exec(A, WIN, kc->word, &npc);
      _mm_setcsr((_mm_getcsr() & ~0x8000u) | ((B->fpscr & GEKKO_FPSCR_NI) ? 0x8000u : 0u));
      int tr = th_run(B, kc->fn);
      total++;
      char msg[160] = "";
      int fail = 0;
      if ((dr == DOL_OK) != (tr == TH_OK)) { fail = 1; snprintf(msg, sizeof msg, "status dolphin=%d(exc %X) ours=%d", dr, dolphin_last_exceptions(), tr); }
      else if (diff(A, B, dr != DOL_OK, msg, sizeof msg)) fail = 1;
      if (fail) {
        bad++; op_bad++;
        if (printed++ < 30) printf("MISMATCH %s %08X state %ld: %s\n", kc->op, kc->word, s, msg);
      }
    }
  }
  if (op_bad) printf("  op %s: %llu mismatches\n", last_op, op_bad);
  printf("cases=%zu states=%llu mismatches=%llu\n", g_ncases, total, bad);
  return bad != 0;
}
