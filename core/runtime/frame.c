/* frame.c - the frame boundary, the scene-info hook and indirect calls. */
#include "mc_internal.h"
#include "guest_table.h"
#include "guest_fns.h"

#define ADDR_PAD_LIB 0x804C1F78u     /* HSD_PadLibData: +0 qnum, +1 qread, +2 qwrite, +3 qcount, +8 queue */
#define ADDR_SCENE_INFO 0x804D6720u  /* gm_804D6720: current GameSceneInfo* */
#define ADDR_STATE_MACHINE 0x80479D30u  /* gm_1A3F.c state_machine: +0 curr_mode, +1 pending_mode, +3 curr_state_id, +0xC pending_mode_change */
#define ADDR_SCENE_EXIT 0x80479D64u     /* gm_80479D58.unk_C: nonzero ends the scene frame loop (gm_801A4D34) */
#define ADDR_MODES 0x803DACA4u          /* GameMode modes[]: 0x14 each, kind +1, on_load +4, on_unload +8 */
#define ADDR_RAND_SEED 0x804D5F90u      /* HSD_Rand seed */
#define GM_DEBUG_VS 0x0E
#define GM_COUNT 0x2D
#define GS_VS 2
#define MAGIC_ONLOAD 0x80000200u        /* host callbacks planted in GameMode structs (not guest code) */
#define MAGIC_UNLOAD 0x80000204u
#define MAGIC_SYNC_RNG 0x80000208u      /* GObj proc created by runtime/online_rng.gecko */
#define ADDR_ONLINE_FLAG 0x80002F00u    /* read by runtime/online_rng.gecko; +4: the RNG offset */
#define ADDR_GLOBAL_FRAME 0x80479D60u
#define ADDR_COLL_RESOLVE 0x8006D1ECu   /* Fighter_procCollResolve: fighter proc 14, resolves the frame's hits */

static uint32_t mode_struct(Cpu *c, uint32_t kind) {
  for (uint32_t m = ADDR_MODES; ld8(c, m + 1) != GM_COUNT; m += 0x14)
    if (ld8(c, m + 1) == kind) return m;
  mc_die("game mode %02X not found", kind);
}
/* Leave the current game mode for the debug VS mode at its GS_VS state, as Slippi playback does:
 * the current mode's on_unload sets the next mode, the debug mode's on_load picks the state. */
static void request_match(Machine *m, Cpu *c) {
  uint32_t dbg = mode_struct(c, GM_DEBUG_VS), cur = mode_struct(c, ld8(c, ADDR_STATE_MACHINE));
  m->onload_slot = dbg + 4;
  m->onload_orig = ld32(c, dbg + 4);
  st32(c, dbg + 4, MAGIC_ONLOAD);
  m->unload_slot = cur + 8;
  m->unload_orig = ld32(c, cur + 8);
  st32(c, cur + 8, MAGIC_UNLOAD);
  st8(c, ADDR_STATE_MACHINE + 0xC, 1);   /* pending_mode_change */
  st32(c, ADDR_SCENE_EXIT, 1);
  m->start_req = 2;
}

/* u8 lb_80019894(void): the scene loop's "frames of input queued" wait. This is the frame boundary:
 * yield to mc_step(), then queue exactly one frame of the host's input and report one frame. */
uint32_t hle_lb_80019894(Cpu *c) {
  Machine *m = g_m;
  if (m->start_req == 1) request_match(m, c);
  m->frames++;
  gx_frame_done();
  coro_yield();
  tb_advance_to(m->frames * (uint64_t)TB_PER_FRAME);
  room_boundary_jobs(c);
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

/* void gm_801A4B88(GameSceneInfo *info): records the current scene. On entering GS_VS for a
 * requested match, installs the requested StartMeleeData (the scene's enter data) and RNG seed. */
uint32_t hle_gm_801A4B88(Cpu *c) {
  Machine *m = g_m;
  uint32_t info = c->gpr[3];
  st32(c, ADDR_SCENE_INFO, info);
  if (m->start_req == 2 && ld8(c, info) == GS_VS) {
    uint32_t enter = ld32(c, info + 4);
    for (uint32_t i = 0; i < sizeof m->start; i++) st8(c, enter + i, m->start[i]);
    st32(c, ADDR_RAND_SEED, m->seed);
    st32(c, ADDR_ONLINE_FLAG, (uint32_t)m->online);
    st32(c, ADDR_ONLINE_FLAG + 4, m->seed);
    m->start_req = 0;
    m->in_match = 1;
  }
  RET;
}

uint32_t guest_call_indirect(Cpu *c, uint32_t target) {
  Machine *m = g_m;
  if (target == MAGIC_ONLOAD || target == MAGIC_UNLOAD) {
    int unload = target == MAGIC_UNLOAD;
    uint32_t slot = unload ? m->unload_slot : m->onload_slot, orig = unload ? m->unload_orig : m->onload_orig;
    st32(c, slot, orig);                  /* one shot: restore the game's callback and run it */
    uint32_t lr = c->spr[SPR_LR];
    if (orig) call_guest(c, orig, 0, 0);
    c->spr[SPR_LR] = lr;
    if (unload) st8(c, ADDR_STATE_MACHINE + 1, GM_DEBUG_VS);   /* pending_mode: runGameMode returns it */
    else st8(c, ADDR_STATE_MACHINE + 3, 1);                     /* curr_state_id: debug VS state 1 is GS_VS */
    RET;
  }
  if (target == MAGIC_SYNC_RNG) {   /* Slippi FN_SyncRNG: seed = rotl(global frame, 16) + RNG offset */
    st32(c, ADDR_RAND_SEED, rotl32(ld32(c, ADDR_GLOBAL_FRAME), 16) + ld32(c, ADDR_ONLINE_FLAG + 4));
    RET;
  }
  if (target == ADDR_COLL_RESOLVE && m->hit_pause && c->gpr[3] == ld32(c, 0x80453080u + 0xB0u)) {
    m->hit_pause = 0;                   /* mc_step_to_hits: give the host this frame's hitboxes */
    m->at_hits = 1;
    m->hit_slots = 0;
    coro_yield();
    m->at_hits = 0;
  }
  GuestFn f = guest_lookup(target);
  if (!f) mc_die("indirect call to %08X, which is not recompiled code", target);
  uint32_t gobj = c->gpr[3];   /* a GObj's render callback (HSD_GObj_80390CD4 calls render_cb(gobj, pass)) */
  if (m->gx_on && gobj >= 0x80000000u && gobj < 0x80000000u + MEM1_SIZE - 0x30 && ld32(c, gobj + 0x1C) == target) {
    gx_mark(c, gobj);
    uint32_t r = f(c);
    gx_mark(c, 0);
    return r;
  }
  return f(c);
}
