/* irq.c - host->guest calls and interrupt delivery. Interrupts are taken at the recompiler's poll
 * points (function entry, loop back-edges) when MSR.EE is set, and wherever the guest re-enables
 * them. All registers may be live there, so the whole context is saved and restored around the
 * handlers, as the exception vector and rfi do on hardware. */
#include <setjmp.h>
#include <string.h>
#include "mc_internal.h"
#include "guest_table.h"
#include "guest_fns.h"

#define ADDR_CURRENT_CONTEXT 0x800000D4u   /* OSGetCurrentContext() pointer */
#define FN_DEC_CALLBACK 0x80343BC8u        /* DecrementerExceptionCallback(exception, ctx) */
#define FN_AR_HANDLER 0x80350F98u          /* __ARHandler(interrupt, ctx) */
#define FN_GX_FINISH 0x8033CF4Cu           /* GXFinishInterruptHandler(interrupt, ctx) */
#define FN_VI_RETRACE 0x8034E964u          /* __VIRetraceHandler(interrupt, ctx) */
#define ADDR_GX_DRAWDONE 0x804D7328u       /* GXMisc.c DrawDone (u8) */

uint32_t call_guest(Cpu *c, uint32_t addr, uint32_t r3, uint32_t r4) {
  GuestFn f = guest_lookup(addr);
  if (!f) mc_die("no guest function at %08X", addr);
  uint32_t lr = c->spr[SPR_LR];
  c->gpr[3] = r3;
  c->gpr[4] = r4;
  c->spr[SPR_LR] = HOST_RETURN;
  uint32_t r = f(c);
  if (r != HOST_RETURN) mc_die("guest call %08X returned to %08X", addr, r);
  c->spr[SPR_LR] = lr;
  return c->gpr[3];
}

static int queued(const Machine *m) { return m->n_dvd || m->pend_ar || m->pend_pe; }
static void deliver_queues(Machine *m, Cpu *c) {
  while (queued(m)) {
    while (m->n_dvd) {                        /* DVD completions, in order */
      uint32_t cb = m->dvd[0].cb, block = m->dvd[0].block, len = m->dvd[0].len;
      memmove(m->dvd, m->dvd + 1, sizeof m->dvd[0] * (size_t)--m->n_dvd);
      if (cb) call_guest(c, cb, len, block);  /* DVDCallback(result, block) */
    }
    if (m->pend_pe) { m->pend_pe = 0; call_guest(c, FN_GX_FINISH, 9u, ld32(c, ADDR_CURRENT_CONTEXT)); }
    if (m->pend_ar) {
      m->pend_ar = 0;
      m->hw[0x500B] |= 0x20;                  /* DSP CSR: ARAM DMA interrupt */
      call_guest(c, FN_AR_HANDLER, 6u, ld32(c, ADDR_CURRENT_CONTEXT));
    }
  }
}

static jmp_buf dec_jmp;
/* OSLoadContext is reached only as the rfi that ends the decrementer handler (Melee runs one
 * thread): unwind back to service(), which restores the interrupted context. */
uint32_t hle_OSLoadContext(Cpu *c) {
  (void)c;
  if (!g_m->in_dec) mc_die("OSLoadContext outside the decrementer handler (thread switch?)");
  longjmp(dec_jmp, 1);
}

void service(Cpu *c) {
  static Cpu saved;
  Machine *m = g_m;
  if (m->in_irq) return;
  int dec = m->dec_armed && m->tb >= m->dec_deadline;
  if (!dec && !queued(m)) { c->irq_pending = 0; return; }
  m->in_irq = 1;
  saved = *c;
  c->msr &= ~0x8000u;                         /* handlers run with EE off */
  if (dec) {
    m->dec_armed = 0;
    m->in_dec = 1;
    if (!setjmp(dec_jmp)) call_guest(c, FN_DEC_CALLBACK, 8u, ld32(c, ADDR_CURRENT_CONTEXT));
    m->in_dec = 0;
  }
  deliver_queues(m, c);
  *c = saved;
  c->irq_pending = (m->dec_armed && m->tb >= m->dec_deadline) || queued(m);
  m->in_irq = 0;
}
void guest_irq(Cpu *c) { service(c); }

/* BOOL OSRestoreInterrupts(BOOL level) / BOOL OSEnableInterrupts(void): old EE; deliver on enable. */
uint32_t hle_OSRestoreInterrupts(Cpu *c) {
  uint32_t old = (c->msr >> 15) & 1u;
  if (c->gpr[3]) { c->msr |= 0x8000u; service(c); } else c->msr &= ~0x8000u;
  c->gpr[3] = old;
  RET;
}
uint32_t hle_OSEnableInterrupts(Cpu *c) {
  uint32_t old = (c->msr >> 15) & 1u;
  c->msr |= 0x8000u;
  service(c);
  c->gpr[3] = old;
  RET;
}
/* No GPU: a draw-done token completes as soon as it is set (PE finish interrupt pending). */
uint32_t hle_GXSetDrawDone(Cpu *c) { st8(c, ADDR_GX_DRAWDONE, 0); g_m->pend_pe = 1; c->irq_pending = 1; RET; }
uint32_t hle_GXWaitDrawDone(Cpu *c) { service(c); RET; }
/* One retrace: raise the VI display interrupt and run the game's VI handler. */
uint32_t hle_VIWaitForRetrace(Cpu *c) {
  service(c);
  g_m->hw[0x2030] |= 0x80;                    /* VI DI0 interrupt status */
  call_guest(c, FN_VI_RETRACE, 24u, ld32(c, ADDR_CURRENT_CONTEXT));
  RET;
}
