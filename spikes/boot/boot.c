#define _POSIX_C_SOURCE 199309L
/* THROWAWAY boot spike (plan 1c scoping). Findings: polls EXI CR, then DSP CSR, then DSP mailbox (ucode handshake). */
/* boot.c - THROWAWAY spike: run recompiled main.dol from __start with minimal hooks and see where it stops.
 * usage: boot <mem1.bin> <iso> [seconds] */
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>
#include <xmmintrin.h>
#include "cpu.h"
#include "guest_table.h"
#include "guest_meta.h"

static uint8_t hw[0x10000];     /* 0xCC000000 hardware registers (shadow) */
static uint8_t lc[0x4000];      /* 0xE0000000 locked cache */
static struct { uint32_t ea; int w; unsigned long n; } io_log[512];
static int io_n;

static void io_note(uint32_t ea, int w) {
  for (int i = 0; i < io_n; i++) if (io_log[i].ea == ea && io_log[i].w == w) { io_log[i].n++; return; }
  if (io_n < 512) { io_log[io_n].ea = ea; io_log[io_n].w = w; io_log[io_n].n = 1; io_n++; }
}
static void io_dump(void) {
  fprintf(stderr, "--- io accesses (%d distinct)\n", io_n);
  for (int i = 0; i < io_n; i++) fprintf(stderr, "  %08X %s x%lu\n", io_log[i].ea, io_log[i].w ? "W" : "R", io_log[i].n);
}
static const char *fname(uint32_t a) {
  const char *best = "?"; uint32_t ba = 0;
  for (size_t i = 0; i < g_guest_nnames; i++) if (g_guest_names[i].addr <= a && g_guest_names[i].addr >= ba) { ba = g_guest_names[i].addr; best = g_guest_names[i].name; }
  return best;
}
void guest_fault(Cpu *c, uint32_t ea, uint32_t size, int write) {
  (void)c; fprintf(stderr, "FAULT ea=%08X size=%u %s\n", ea, size, write ? "W" : "R"); io_dump(); abort();
}
uint8_t *guest_io_ptr(Cpu *c, uint32_t ea, uint32_t size, int write) {
  if ((ea & 0xFFFF0000u) == 0xCC000000u && (ea & 0xFFFF) + size <= sizeof hw) {
    io_note(ea, write);
    uint32_t o = ea & 0xFFFF;
    /* EXI channel control (0x680C/0x6820/0x6834): transfers complete instantly (TSTART reads back 0). */
    if (!write && (o == 0x680C || o == 0x6820 || o == 0x6834)) hw[o + 3] &= ~1u;
    /* DSP CSR (0x500A, 16-bit): reset / DMA / init-busy bits self-clear. */
    if (!write && (o == 0x500A || o == 0x500B)) { hw[0x500A] &= ~0x0Eu; hw[0x500B] &= ~0x01u; }
    return hw + o;
  }
  if ((ea & 0xFFFFC000u) == 0xE0000000u && (ea & 0x3FFF) + size <= sizeof lc) return lc + (ea & 0x3FFF);
  guest_fault(c, ea, size, write);
}
void guest_sc(Cpu *c, uint32_t pc) { (void)c; (void)pc; }
void guest_trap(Cpu *c, uint32_t pc, uint32_t insn) {
  (void)c; fprintf(stderr, "TRAP pc=%08X (%s) insn=%08X lr=%08X\n", pc, fname(pc), insn, c->spr[SPR_LR]); io_dump(); abort();
}
uint32_t guest_bad_return(Cpu *c, uint32_t expected, uint32_t got) {
  (void)c; fprintf(stderr, "BAD_RETURN expected=%08X got=%08X\n", expected, got); abort();
}
uint32_t guest_call_indirect(Cpu *c, uint32_t target) {
  GuestFn f = guest_lookup(target);
  if (!f) { fprintf(stderr, "INDIRECT_MISS %08X\n", target); abort(); }
  return f(c);
}
uint32_t guest_mftb(Cpu *c, int upper) {
  (void)c; struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
  uint64_t t = (uint64_t)ts.tv_sec * 40500000ull + (uint64_t)ts.tv_nsec * 405ull / 10000ull;
  return upper ? (uint32_t)(t >> 32) : (uint32_t)t;
}
uint32_t guest_mfspr(Cpu *c, uint32_t spr) { return c->spr[spr]; }
void guest_mtspr(Cpu *c, uint32_t spr, uint32_t v) { c->spr[spr] = spr == 923 ? v & ~2u : v; }
void guest_fpscr_changed(Cpu *c) { _mm_setcsr((_mm_getcsr() & ~0x8000u) | ((c->fpscr & 4u) ? 0x8000u : 0u)); }

static void on_alarm(int s) { (void)s; fprintf(stderr, "TIMEOUT\n"); io_dump(); abort(); }

static void w32(uint8_t *m, uint32_t a, uint32_t v) { m[a & 0x1FFFFFF] = v >> 24; m[(a & 0x1FFFFFF) + 1] = v >> 16; m[(a & 0x1FFFFFF) + 2] = v >> 8; m[(a & 0x1FFFFFF) + 3] = v; }
static uint32_t be32(const uint8_t *p) { return (uint32_t)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]; }

int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: boot mem1.bin iso [seconds]\n"); return 2; }
  static Cpu cpu; Cpu *c = &cpu;
  c->mem = calloc(1, MEM1_SIZE);
  FILE *f = fopen(argv[1], "rb"); if (!f || fread(c->mem, 1, MEM1_SIZE, f) != MEM1_SIZE) return 2; fclose(f);
  /* Apploader/IPL leftovers (values as Dolphin's BS2 emulation writes them). */
  FILE *iso = fopen(argv[2], "rb"); uint8_t hdr[0x440];
  if (!iso || fread(hdr, 1, sizeof hdr, iso) != sizeof hdr) return 2;
  memcpy(c->mem, hdr, 0x20);                    /* disc ID */
  uint32_t fst_off = be32(hdr + 0x424), fst_size = be32(hdr + 0x428);
  uint32_t fst_addr = (0x81800000u - fst_size) & ~31u;
  fseek(iso, fst_off, SEEK_SET);
  if (fread(c->mem + (fst_addr & 0x1FFFFFF), 1, fst_size, iso) != fst_size) return 2;
  w32(c->mem, 0x80000020, 0x0D15EA5E); w32(c->mem, 0x80000024, 1);
  w32(c->mem, 0x80000028, 0x01800000); w32(c->mem, 0x8000002C, 3);
  w32(c->mem, 0x80000030, 0); w32(c->mem, 0x80000034, fst_addr);
  w32(c->mem, 0x80000038, fst_addr); w32(c->mem, 0x8000003C, fst_size);
  w32(c->mem, 0x800000CC, 0); w32(c->mem, 0x800000F0, 0x01800000);
  w32(c->mem, 0x800000F8, 162000000); w32(c->mem, 0x800000FC, 486000000);
  c->msr = 0x00002032u;
  c->spr[SPR_HID2] = 0xE0000000u;
  c->spr[SPR_LR] = 0x80000100u;
  signal(SIGALRM, on_alarm);
  alarm(argc > 3 ? atoi(argv[3]) : 10);
  uint32_t r = guest_lookup(GUEST_ENTRY)(c);
  fprintf(stderr, "__start returned %08X\n", r);
  io_dump();
  return 0;
}
