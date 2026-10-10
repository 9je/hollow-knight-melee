/* test_runtime: unit tests for hw.c (hooks, timebase) and coro.c. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "mc_internal.h"

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); exit(1); } } while (0)

static int steps;
static void count_body(void *arg) { (void)arg; for (int i = 0; i < 3; i++) { steps++; coro_yield(); } }
static void fault_body(void *arg) { Cpu *c = arg; (void)ld32(c, 0xC8000000u); }

int main(void) {
  Machine *m = calloc(1, sizeof *m);
  g_m = m;
  Cpu *c = &m->cpu;
  c->mem = calloc(1, MEM1_SIZE);
  /* hardware register shadow */
  st32(c, 0xCC00680Cu, 0x00000035u);
  CHECK(ld32(c, 0xCC00680Cu) == 0x00000034u);   /* EXI transfer start bit reads back done */
  st16(c, 0xCC00500Au, 0x0E01u);
  CHECK(ld16(c, 0xCC00500Au) == 0x0000u);       /* DSP CSR reset/DMA/init bits self-clear */
  st32(c, 0xCC003000u, 0xDEADBEEFu);
  CHECK(ld32(c, 0xCC003000u) == 0xDEADBEEFu);   /* plain registers keep their value */
  st32(c, 0xE0000010u, 0x12345678u);
  CHECK(ld32(c, 0xE0000010u) == 0x12345678u);   /* locked cache */
  /* virtual timebase and decrementer */
  uint32_t t0 = guest_mftb(c, 0), t1 = guest_mftb(c, 0);
  CHECK(t1 == t0 + 1);                          /* time moves on every read */
  guest_mtspr(c, 22, 1000);
  CHECK(c->irq_pending == 0);
  tb_advance_to(m->tb + 500);
  CHECK(c->irq_pending == 0);
  tb_advance_to(m->tb + 600);
  CHECK(c->irq_pending == 1);                   /* DEC due: interrupt pending */
  c->irq_pending = 0;
  guest_mtspr(c, 22, 0xFFFFFFFFu);
  CHECK(c->irq_pending == 1);                   /* negative DEC is due at once */
  uint64_t t = m->tb;
  tb_advance_to(t - 5);
  CHECK(m->tb == t);                            /* never goes backwards */
  /* coroutines */
  Coro *co = coro_new(count_body, NULL, 1u << 20);
  CHECK(coro_resume(co) == 1 && steps == 1);
  CHECK(coro_resume(co) == 1 && steps == 2);
  CHECK(coro_resume(co) == 1 && steps == 3);
  CHECK(coro_resume(co) == 0);
  coro_free(co);
  /* a guest fault kills the machine, not the process */
  co = coro_new(fault_body, c, 1u << 20);
  CHECK(coro_resume(co) == 1);
  CHECK(m->dead && strstr(m->error, "C8000000") != NULL);
  CHECK(coro_resume(co) == 1 && m->dead);       /* a dead machine stays parked */
  coro_free(co);
  /* nmsub zero sign: hardware gives -(a*c - b) = -0 when a*c == b; Slippi's deterministic JIT computes
   * b - a*c = +0. Everything else is the same. */
  CHECK(signbit(gekko_fnmsubs(0, 2.0, 3.0, 6.0).ps0) && signbit(gekko_fnmsub(0, 2.0, 3.0, 6.0)));
  gekko_slippi_nmsub = 1;
  CHECK(!signbit(gekko_fnmsubs(0, 2.0, 3.0, 6.0).ps0) && !signbit(gekko_fnmsubs(0, 2.0, 3.0, 6.0).ps1));
  CHECK(!signbit(gekko_fnmsub(0, 2.0, 3.0, 6.0)));
  GekkoPS a = {2.0, 0.0}, cc = {3.0, 5.0}, b = {6.0, -0.0}, r = gekko_ps_nmsub(0, a, cc, b);
  CHECK(!signbit(r.ps0) && signbit(r.ps1));                 /* -0 - (+0) = -0 */
  CHECK(gekko_fnmsubs(0, 2.0, 3.0, 7.0).ps0 == 1.0);        /* nonzero results unchanged */
  gekko_slippi_nmsub = 0;
  puts("runtime tests passed");
  return 0;
}
