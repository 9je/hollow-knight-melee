#include <string.h>
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

#define FN_PROC_ATTACK_COLL 0x8006CB94u   /* Fighter_procAttackColl: the victim side of every hit */
#define FN_WALK_COLL 0x800C9788u          /* ftCo_Walk_Coll: ft_80084280, which stops at an edge if |lstick x| < 0.75 */
#define FN_OTTOTTO_IASA 0x8009A4A4u       /* the teeter's input check (action state table entry 245, Ottotto) */
#define FN_OTTOTTOWAIT_IASA 0x8009A72Cu   /* its wait's (246, OttottoWait) */
#define EDGE_STOP_TILT 0.7375f
#define EDGE_LOOK 6.0f                    /* how far past Fox the floor is looked for */
#define EDGE_STEP 18.0f                   /* the deepest step walked off */            /* the largest tilt below 0.75 the GameCube stick gives (59/80) */

/* Once Fox teeters and the stick eases below 75% tilt, the assist has done its job: pushing on (a walk past 75%) goes off as
 * in Melee. Without this, that walk was stopped at the edge again each frame and Fox restarted his teeter on a loop. A
 * stick held at full tilt throughout keeps him at the edge. A turn, a dash or leaving the ground ends it. */
void edge_assist_boundary(Cpu *c) {
  Machine *m = g_m;
  if (!m->edge_assist) return;
  uint32_t gobj = ld32(c, 0x80453080u + 0xB0u);
  if (!gobj) return;
  uint32_t fp = ld32(c, gobj + 0x2C), a = ld32(c, fp + 0x10u), u = ld32(c, fp + 0x620u);
  float x; memcpy(&x, &u, 4);
  if (a == 245u || a == 246u) { if (x < 0.75f && x > -0.75f) m->edge_teetered = 1; }   /* Ottotto, OttottoWait */
  else if (a < 15u || a > 17u) m->edge_teetered = 0;                                   /* not WalkSlow/Middle/Fast */
}

int mc_set_edge_assist(int on) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  m->edge_assist = on != 0;
  return 0;
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
  if (m->standin_gobj && c->gpr[3] == m->standin_gobj) {   /* plan 7: the stand-in's procs */
    if (ld32(c, m->standin_gobj + 0x1C) == target) RET;      /* its render pass: never drawn */
    if (target == FN_PROC_ATTACK_COLL && !standin_captured(c)) RET;   /* hit only while held (pummels, throws) */
    GuestFn g = guest_lookup(target);
    if (!g) mc_die("indirect call to %08X, which is not recompiled code", target);
    m->standin_depth++;
    uint32_t r = g(c);
    m->standin_depth--;
    return r;
  }
  GuestFn f = guest_lookup(target);
  if (!f) mc_die("indirect call to %08X, which is not recompiled code", target);
  if ((target == FN_OTTOTTO_IASA || target == FN_OTTOTTOWAIT_IASA) && m->edge_assist && !m->edge_teetered && c->gpr[3] == ld32(c, 0x80453080u + 0xB0u)) {
    /* edge assist, teetering: a full tilt towards the edge reads as a slight one, so the teeter holds and its wait plays on.
     * Melee would walk off; the assist stopped that walk at the edge every frame and the teeter restarted every frame: a
     * frozen pose. Easing the stick (edge_teetered) hands the edge back to Melee's rules; a tilt away from the edge is as it is. */
    uint32_t fp = ld32(c, c->gpr[3] + 0x2C), was = ld32(c, fp + 0x620u), uf = ld32(c, fp + 0x2Cu);
    float x, facing; memcpy(&x, &was, 4); memcpy(&facing, &uf, 4);
    if ((x > EDGE_STOP_TILT && facing > 0) || (x < -EDGE_STOP_TILT && facing < 0)) {
      float t = x > 0 ? EDGE_STOP_TILT : -EDGE_STOP_TILT; uint32_t u; memcpy(&u, &t, 4);
      st32(c, fp + 0x620u, u);
      uint32_t r = f(c);
      st32(c, fp + 0x620u, was);
      return r;
    }
  }
  if (target == FN_WALK_COLL && m->edge_assist && !m->edge_teetered && c->gpr[3] == ld32(c, 0x80453080u + 0xB0u)) {
    /* edge assist: the edge check sees a tilt below 0.75, so the walk stops at the edge as a slower walk does */
    uint32_t fp = ld32(c, c->gpr[3] + 0x2C), was = ld32(c, fp + 0x620u);
    float x; memcpy(&x, &was, 4);
    /* only above a drop: a step down shorter than Fox (a stair; ledges need 18, room_build.c) is walked off at full tilt */
    uint32_t ux = ld32(c, fp + 0xB0u), uy = ld32(c, fp + 0xB4u);
    float px, py; memcpy(&px, &ux, 4); memcpy(&py, &uy, 4);
    if ((x > EDGE_STOP_TILT || x < -EDGE_STOP_TILT) && !room_floor_between(c, px + (x > 0 ? EDGE_LOOK : -EDGE_LOOK), py + 4.0f, py - EDGE_STEP)) {
      float t = x > 0 ? EDGE_STOP_TILT : -EDGE_STOP_TILT; uint32_t u; memcpy(&u, &t, 4);
      st32(c, fp + 0x620u, u);
      uint32_t r = f(c);
      st32(c, fp + 0x620u, was);
      return r;
    }
  }
  uint32_t gobj = c->gpr[3];   /* a GObj's render callback (HSD_GObj_80390CD4 calls render_cb(gobj, pass)) */
  int render_cb = (m->gx_on || m->render_pause) && gobj >= 0x80000000u && gobj < 0x80000000u + MEM1_SIZE - 0x30 && ld32(c, gobj + 0x1C) == target;
  if (render_cb && m->render_pause) {   /* mc_step_to_render: the frame's logic is done; the host reads it here */
    m->render_pause = 0;
    m->at_render = 1;
    coro_yield();
    m->at_render = 0;
  }
  if (render_cb && m->gx_on) {
    gx_mark(c, gobj);
    uint32_t r = f(c);
    gx_mark(c, 0);
    return r;
  }
  return f(c);
}
