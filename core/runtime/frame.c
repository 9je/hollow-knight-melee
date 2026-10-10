/* frame.c - the frame boundary, the scene-info hook and indirect calls. */
#include "mc_internal.h"
#include "guest_table.h"
#include "guest_fns.h"

#define ADDR_PAD_LIB 0x804C1F78u     /* HSD_PadLibData: +0 qnum, +1 qread, +2 qwrite, +3 qcount, +8 queue */
#define ADDR_SCENE_INFO 0x804D6720u  /* gm_804D6720: current GameSceneInfo* */

/* u8 lb_80019894(void): the scene loop's "frames of input queued" wait. This is the frame boundary:
 * yield to mc_step(), then queue exactly one frame of the host's input and report one frame. */
uint32_t hle_lb_80019894(Cpu *c) {
  Machine *m = g_m;
  m->frames++;
  coro_yield();
  tb_advance_to(m->frames * (uint64_t)TB_PER_FRAME);
  uint32_t qnum = ld8(c, ADDR_PAD_LIB), qread = ld8(c, ADDR_PAD_LIB + 1);
  if (qnum == 0) mc_die("pad queue not initialised at the frame boundary");
  uint32_t q = ld32(c, ADDR_PAD_LIB + 8) + qread * 0x30u;   /* HSD_PadData: PADStatus stat[4] */
  for (int i = 0; i < 4; i++)
    for (int k = 0; k < 12; k++) st8(c, q + 12u * (uint32_t)i + (uint32_t)k, m->pad[i][k]);
  st8(c, ADDR_PAD_LIB + 2, (qread + 1) % qnum);
  st8(c, ADDR_PAD_LIB + 3, 1);
  c->gpr[3] = 1;
  RET;
}

/* void gm_801A4B88(GameSceneInfo *info): records the current scene. */
uint32_t hle_gm_801A4B88(Cpu *c) {
  st32(c, ADDR_SCENE_INFO, c->gpr[3]);
  RET;
}

uint32_t guest_call_indirect(Cpu *c, uint32_t target) {
  GuestFn f = guest_lookup(target);
  if (!f) mc_die("indirect call to %08X, which is not recompiled code", target);
  return f(c);
}
