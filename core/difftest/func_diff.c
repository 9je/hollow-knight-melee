/* func_diff.c - per-function differential run: Dolphin's interpreter vs recompiled main.dol.
 * usage: func_diff [--list FILE | --all] [--sets N] [--seed S] [--budget N] [--jobs J] [--csv OUT] [--mem mem1.bin]
 * Each (function, arg-set) runs in a forked child with a 10 s alarm; the child prints one CSV line. */
#define _GNU_SOURCE
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/wait.h>
#include <unistd.h>
#include <xmmintrin.h>
#include "cpu.h"
#include "fpgen.h"
#include "guest_meta.h"
#include "guest_table.h"
#include "oracle.h"
#include "test_hooks.h"

#define SENTINEL 0x80000100u
#define SCRATCH 0x80F00000u
#define SCRATCH_SIZE 0x40000u

enum { PASS, FAULT_EQUAL, MISMATCH, FAULT_MISMATCH, DOLPHIN_BUDGET, RECOMP_TRAP, INDIRECT_MISS, BAD_RETURN,
       TIMEOUT, CRASH, NSTATUS };
static const char *STATUS[] = {"PASS", "FAULT_EQUAL", "MISMATCH", "FAULT_MISMATCH", "DOLPHIN_BUDGET",
                               "RECOMP_TRAP", "INDIRECT_MISS", "BAD_RETURN", "TIMEOUT", "CRASH"};

static uint8_t *memA, *memB;
static uint64_t g_seed = 1, g_budget = 5000000;
static int g_out = 1;

/* faulted: DAR/DSISR are exception-handler state Dolphin sets on a fault; not compared then. */
static int diff_state(const Cpu *a, const Cpu *b, int faulted, char *out, size_t n) {
  for (int i = 0; i < 32; i++) if (a->gpr[i] != b->gpr[i]) return snprintf(out, n, "r%d dolphin=%08X ours=%08X", i, a->gpr[i], b->gpr[i]);
  for (int i = 0; i < 32; i++) {
    if (f2u(a->fpr[i].ps0) != f2u(b->fpr[i].ps0)) return snprintf(out, n, "f%d.ps0 dolphin=%016llX ours=%016llX", i, (unsigned long long)f2u(a->fpr[i].ps0), (unsigned long long)f2u(b->fpr[i].ps0));
    if (f2u(a->fpr[i].ps1) != f2u(b->fpr[i].ps1)) return snprintf(out, n, "f%d.ps1 dolphin=%016llX ours=%016llX", i, (unsigned long long)f2u(a->fpr[i].ps1), (unsigned long long)f2u(b->fpr[i].ps1));
  }
  if (a->cr != b->cr) return snprintf(out, n, "cr dolphin=%08X ours=%08X", a->cr, b->cr);
  if ((a->spr[SPR_XER] & XER_MASK) != (b->spr[SPR_XER] & XER_MASK)) return snprintf(out, n, "xer dolphin=%08X ours=%08X", a->spr[SPR_XER], b->spr[SPR_XER]);
  for (int i = 0; i < 1024; i++) if (i != SPR_XER && !(faulted && (i == 18 || i == 19)) && a->spr[i] != b->spr[i]) return snprintf(out, n, "spr%d dolphin=%08X ours=%08X", i, a->spr[i], b->spr[i]);
  if (a->msr != b->msr) return snprintf(out, n, "msr");
  if ((a->fpscr & 0xFF) != (b->fpscr & 0xFF)) return snprintf(out, n, "fpscr dolphin=%08X ours=%08X", a->fpscr, b->fpscr);
  for (int i = 0; i < 16; i++) if (a->sr[i] != b->sr[i]) return snprintf(out, n, "sr%d", i);
  if (memcmp(a->mem, b->mem, MEM1_SIZE) != 0) {
    for (uint32_t i = 0; i < MEM1_SIZE; i++)
      if (a->mem[i] != b->mem[i]) return snprintf(out, n, "mem %08X dolphin=%02X ours=%02X", 0x80000000u + i, a->mem[i], b->mem[i]);
  }
  return 0;
}

static void init_state(Cpu *c, uint8_t *mem, Rng *g) {
  memset(c, 0, sizeof *c);
  c->mem = mem;
  c->gpr[1] = 0x816FFF00u;
  c->gpr[2] = GUEST_R2;
  c->gpr[13] = GUEST_R13;
  for (int j = 0; j < 8; j++) c->gpr[3 + j] = SCRATCH + 0x1000u * j + 8u * (uint32_t)(rng_next(g) % 0x100);
  for (int j = 1; j <= 8; j++) {
    float f; uint32_t u;
    do { u = (uint32_t)rng_next(g); memcpy(&f, &u, 4); } while (!isfinite(f));
    if (rng_next(g) & 1) f = (float)((double)(int32_t)(rng_next(g) % 2001 - 1000) / 8.0);
    c->fpr[j].ps0 = c->fpr[j].ps1 = (double)f;
  }
  c->spr[SPR_LR] = SENTINEL;
  c->msr = 0x00002032u;
  c->spr[SPR_HID2] = 0xE0000000u;
  c->spr[SPR_HID0] = 0x0011C464u;
  /* GQRs as OSInitFastCast leaves them (Dolphin SDK): GQR2-5 = u8, u16, s8, s16 with scale 0. */
  c->spr[SPR_GQR0 + 2] = 0x00040004u;
  c->spr[SPR_GQR0 + 3] = 0x00050005u;
  c->spr[SPR_GQR0 + 4] = 0x00060006u;
  c->spr[SPR_GQR0 + 5] = 0x00070007u;
}

static void fill_scratch(Rng *g) {
  for (uint32_t i = 0; i < SCRATCH_SIZE; i += 4) {
    uint32_t v;
    if (rng_next(g) % 10) { float f = (float)((double)(int32_t)(rng_next(g) % 20001 - 10000) / 64.0); memcpy(&v, &f, 4); }
    else v = (uint32_t)rng_next(g);
    uint32_t be = __builtin_bswap32(v);
    uint32_t off = (SCRATCH & 0x1FFFFFFFu) + i;
    memcpy(memA + off, &be, 4);
    memcpy(memB + off, &be, 4);
  }
}

/* Runs in the child. Returns status; detail filled. */
static int run_one(uint32_t addr, GuestFn fn, int set, uint64_t *steps, char *detail, size_t n) {
  Rng g; rng_seed(&g, g_seed ^ ((uint64_t)addr << 20), (uint64_t)set);
  fill_scratch(&g);
  static Cpu A, B;
  Rng g2 = g;
  init_state(&A, memA, &g);
  init_state(&B, memB, &g2);
  uint32_t pc = addr;
  _mm_setcsr(_mm_getcsr() & ~0x8000u);
  int dr = dolphin_run(&A, &pc, SENTINEL, g_budget, steps);
  if (dr == DOL_BUDGET) return DOLPHIN_BUDGET;
  _mm_setcsr(_mm_getcsr() & ~0x8000u);
  uint32_t ret = 0;
  th_lookup = guest_lookup;
  int tr = th_call(&B, fn, &ret);
  if (tr == TH_TRAP) { snprintf(detail, n, "trap at %08X", th_last_trap_pc); return RECOMP_TRAP; }
  if (tr == TH_INDIRECT_MISS) { snprintf(detail, n, "target %08X", th_last_target); return INDIRECT_MISS; }
  if (tr == TH_BAD_RETURN) { snprintf(detail, n, "returned to %08X", th_last_target); return BAD_RETURN; }
  if (tr == TH_OK && ret != SENTINEL) { snprintf(detail, n, "returned to %08X", ret); return BAD_RETURN; }
  if ((dr == DOL_EXCEPTION) != (tr == TH_FAULT)) {
    snprintf(detail, n, "dolphin=%s(pc %08X exc %X) ours=%s(ea %08X)", dr == DOL_OK ? "ok" : "exception", pc,
             dolphin_last_exceptions(), tr == TH_OK ? "ok" : "fault", th_last_fault_ea);
    return FAULT_MISMATCH;
  }
  if (diff_state(&A, &B, tr == TH_FAULT, detail, n)) return tr == TH_FAULT ? FAULT_MISMATCH : MISMATCH;
  return tr == TH_FAULT ? FAULT_EQUAL : PASS;
}

static const char *name_of(uint32_t addr) {
  for (size_t i = 0; i < g_guest_nnames; i++) if (g_guest_names[i].addr == addr) return g_guest_names[i].name;
  return "?";
}

static void do_item(uint32_t addr, int set) {
  int fd[2];
  if (pipe(fd)) { perror("pipe"); exit(2); }
  pid_t pid = fork();
  if (pid == 0) {
    close(fd[0]);
    alarm(10);
    char detail[200] = "";
    uint64_t steps = 0;
    int st = run_one(addr, guest_lookup(addr), set, &steps, detail, sizeof detail);
    char line[400];
    int len = snprintf(line, sizeof line, "%08X,%s,%d,%s,%llu,%s\n", addr, name_of(addr), set, STATUS[st],
                       (unsigned long long)steps, detail);
    if (write(fd[1], line, (size_t)len) < 0) _exit(3);
    _exit(0);
  }
  close(fd[1]);
  char buf[400];
  ssize_t len = read(fd[0], buf, sizeof buf - 1);
  close(fd[0]);
  int ws;
  waitpid(pid, &ws, 0);
  if (len <= 0) {
    int sig = WIFSIGNALED(ws) ? WTERMSIG(ws) : 0;
    len = snprintf(buf, sizeof buf, "%08X,%s,%d,%s,0,signal %d\n", addr, name_of(addr), set,
                   sig == SIGALRM ? "TIMEOUT" : "CRASH", sig);
  }
  if (write(g_out, buf, (size_t)len) < 0) exit(2);
}

static uint8_t *load_mem(const char *path) {
  uint8_t *m = calloc(1, MEM1_SIZE);
  FILE *f = fopen(path, "rb");
  if (!f || fread(m, 1, MEM1_SIZE, f) != MEM1_SIZE) { fprintf(stderr, "cannot read %s\n", path); exit(2); }
  fclose(f);
  return m;
}

int main(int argc, char **argv) {
  const char *list = 0, *csv = 0, *mem = "build/gen/mem1.bin";
  int all = 0, sets = -1, jobs = 1;
  for (int i = 1; i < argc; i++) {
    if (!strcmp(argv[i], "--list")) list = argv[++i];
    else if (!strcmp(argv[i], "--all")) all = 1;
    else if (!strcmp(argv[i], "--sets")) sets = atoi(argv[++i]);
    else if (!strcmp(argv[i], "--seed")) g_seed = strtoull(argv[++i], 0, 0);
    else if (!strcmp(argv[i], "--budget")) g_budget = strtoull(argv[++i], 0, 0);
    else if (!strcmp(argv[i], "--jobs")) jobs = atoi(argv[++i]);
    else if (!strcmp(argv[i], "--csv")) csv = argv[++i];
    else if (!strcmp(argv[i], "--mem")) mem = argv[++i];
    else { fprintf(stderr, "unknown arg %s\n", argv[i]); return 2; }
  }
  if (sets < 0) sets = all ? 1 : 1000;
  uint32_t *addrs = 0; size_t naddr = 0;
  if (all) {
    addrs = malloc(g_guest_nentries * sizeof *addrs);
    for (size_t i = 0; i < g_guest_nentries; i++) addrs[naddr++] = g_guest_entries[i].addr;
  } else if (list) {
    FILE *f = fopen(list, "r");
    if (!f) { perror(list); return 2; }
    char nm[256];
    addrs = malloc(g_guest_nnames * sizeof *addrs);
    while (fscanf(f, "%255s", nm) == 1) {
      size_t i = 0;
      while (i < g_guest_nnames && strcmp(g_guest_names[i].name, nm)) i++;
      if (i == g_guest_nnames) { fprintf(stderr, "unknown function %s\n", nm); return 2; }
      addrs[naddr++] = g_guest_names[i].addr;
    }
    fclose(f);
  } else { fprintf(stderr, "need --list FILE or --all\n"); return 2; }
  memA = load_mem(mem);
  memB = malloc(MEM1_SIZE);
  memcpy(memB, memA, MEM1_SIZE);
  int pfd[2];
  if (pipe(pfd)) return 2;
  pid_t *kids = calloc((size_t)jobs, sizeof *kids);
  for (int w = 0; w < jobs; w++) {
    kids[w] = fork();
    if (kids[w] == 0) {
      close(pfd[0]);
      g_out = pfd[1];
      size_t total = naddr * (size_t)sets;
      for (size_t k = (size_t)w; k < total; k += (size_t)jobs) do_item(addrs[k / (size_t)sets], (int)(k % (size_t)sets));
      _exit(0);
    }
  }
  close(pfd[1]);
  FILE *in = fdopen(pfd[0], "r"), *out = csv ? fopen(csv, "w") : 0;
  unsigned long long counts[NSTATUS] = {0};
  char line[512];
  int printed = 0;
  while (fgets(line, sizeof line, in)) {
    if (out) fputs(line, out);
    char *p = line;
    for (int c = 0; c < 3 && p; c++) p = strchr(p + 1, ',');
    if (!p) continue;
    for (int s = 0; s < NSTATUS; s++) {
      size_t l = strlen(STATUS[s]);
      if (!strncmp(p + 1, STATUS[s], l) && p[1 + l] == ',') {
        counts[s]++;
        if ((s == MISMATCH || s == FAULT_MISMATCH || s == TIMEOUT || s == CRASH) && printed++ < 25) fputs(line, stdout);
      }
    }
  }
  for (int w = 0; w < jobs; w++) waitpid(kids[w], 0, 0);
  if (out) fclose(out);
  for (int s = 0; s < NSTATUS; s++) printf("%-15s %llu\n", STATUS[s], counts[s]);
  return (counts[MISMATCH] || counts[FAULT_MISMATCH] || counts[TIMEOUT] || counts[CRASH]) ? 1 : 0;
}
