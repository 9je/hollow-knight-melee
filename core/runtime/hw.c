/* hw.c - cpu.h hooks: faults, hardware register shadow, locked cache, SPRs, virtual timebase. */
#include <stdarg.h>
#include <stdio.h>
#include <xmmintrin.h>
#include "mc_internal.h"

Machine *g_m;

_Noreturn void mc_die(const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(g_m->error, sizeof g_m->error, fmt, ap);
  va_end(ap);
  g_m->dead = 1;
  for (;;) coro_yield();   /* the host never resumes a dead machine past this point */
}

_Noreturn void guest_fault(Cpu *c, uint32_t ea, uint32_t size, int write) {
  (void)c;
  mc_die("guest %s fault at %08X (size %u)", write ? "write" : "read", ea, size);
}
_Noreturn void guest_trap(Cpu *c, uint32_t pc, uint32_t insn) {
  (void)c;
  mc_die("guest trap at %08X (instruction %08X)", pc, insn);
}
void guest_sc(Cpu *c, uint32_t pc) { (void)c; (void)pc; }
/* A return to an address other than the call site's (longjmp): propagate it up until the frame
 * whose call site, or __setjmp landing, matches (see recomp/function.py). */
uint32_t guest_bad_return(Cpu *c, uint32_t expected, uint32_t got) { (void)c; (void)expected; return got; }

uint8_t *guest_io_ptr(Cpu *c, uint32_t ea, uint32_t size, int write) {
  Machine *m = g_m;
  if ((ea & 0xFFFF0000u) == 0xCC000000u && (ea & 0xFFFFu) + size <= sizeof m->hw) {
    uint32_t o = ea & 0xFFFFu;
    if (!write && (o == 0x680C || o == 0x6820 || o == 0x6834)) m->hw[o + 3] &= ~1u;   /* EXI: transfers complete at once */
    if (!write && (o == 0x500A || o == 0x500B)) { m->hw[0x500A] &= ~0x0Eu; m->hw[0x500B] &= ~0x01u; }  /* DSP CSR */
    return m->hw + o;
  }
  if ((ea & 0xFFFFC000u) == 0xE0000000u && (ea & 0x3FFFu) + size <= sizeof m->lc) return m->lc + (ea & 0x3FFFu);
  guest_fault(c, ea, size, write);
}

static void dec_check(Cpu *c) { if (g_m->dec_armed && g_m->tb >= g_m->dec_deadline) c->irq_pending = 1; }
void tb_advance_to(uint64_t t) {
  if (t > g_m->tb) g_m->tb = t;
  dec_check(&g_m->cpu);
}
/* Virtual time: advances by one tick per lower-half read (so time-polling loops finish) and to the
 * next frame's time at each frame boundary (frame.c). Never host time. */
uint32_t guest_mftb(Cpu *c, int upper) {
  Machine *m = g_m;
  if (!upper) m->tb++;
  dec_check(c);
  return upper ? (uint32_t)(m->tb >> 32) : (uint32_t)m->tb;
}
uint32_t guest_mfspr(Cpu *c, uint32_t spr) {
  if (spr == 22) return (uint32_t)(g_m->dec_deadline - g_m->tb - 1);   /* DEC */
  return c->spr[spr];
}
void guest_mtspr(Cpu *c, uint32_t spr, uint32_t v) {
  if (spr == 22) {                          /* DEC: interrupt when it passes zero */
    g_m->dec_deadline = g_m->tb + (uint64_t)(int64_t)(int32_t)v + 1;
    g_m->dec_armed = 1;
    dec_check(c);
    return;
  }
  c->spr[spr] = spr == 923 ? v & ~2u : v;   /* DMAL: locked-cache DMA completes at once */
}
/* Runs on the guest coroutine: sets the live MXCSR; melee_core.c swaps the host's back after each resume. */
void guest_fpscr_changed(Cpu *c) {
  g_m->guest_csr = 0x1F80u | ((c->fpscr & 4u) ? 0x8000u : 0u);
  _mm_setcsr(g_m->guest_csr);
}
