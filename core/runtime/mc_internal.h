/* mc_internal.h - state shared by the libmelee_core runtime files. Single instance (g_m). */
#pragma once
#include <stdio.h>
#include "cpu.h"
#include "coro.h"
#include "melee_core.h"

#define TB_HZ 40500000u                 /* Gekko timebase: bus clock / 4 */
#define TB_PER_FRAME (TB_HZ / 60u)      /* virtual time per frame boundary */
#define HOST_RETURN 0x80000100u         /* LR sentinel for host->guest calls */
#define ARAM_SIZE 0x01000000u
#define GUEST_STACK (64u << 20)         /* host stack for the guest coroutine (recompiled frames are large) */

typedef struct Machine {
  Cpu cpu;
  uint8_t hw[0x10000];                  /* 0xCC000000 hardware registers (shadow) */
  uint8_t lc[0x4000];                   /* 0xE0000000 locked cache */
  uint8_t *aram;
  FILE *iso;
  uint64_t tb, dec_deadline;            /* virtual timebase; DEC fires when tb >= dec_deadline */
  int dec_armed;
  struct { uint32_t cb, block, len; } dvd[64];   /* DVD completions waiting for delivery, FIFO */
  int n_dvd, pend_ar, pend_pe, in_irq, in_dec;
  Coro *guest;
  uint32_t guest_csr;                   /* guest MXCSR, swapped in around coroutine resumes */
  uint8_t pad[4][12];                   /* raw PADStatus per port for the next frame */
  uint64_t frames;                      /* frame boundaries reached */
  int start_req;                        /* 0 none, 1 mc_start_match asked, 2 mode change under way */
  int in_match;
  uint8_t start[0x138];                 /* StartMeleeData to install */
  uint32_t seed;
  int gx_on;                            /* mc_gx_capture */
  uint8_t *gx_fifo[2]; uint32_t gx_len[2], gx_cap[2];   /* [cur]: this frame's stream; [!cur]: the last complete one */
  struct McGxMarkI { uint32_t offset, gobj; uint16_t classifier; uint8_t p_link, gx_link; float view[12]; uint32_t owner; uint8_t owner_plink, pad[3]; } *gx_marks[2];
  int gx_nmarks[2], gx_capmarks[2], gx_cur;
  int hit_pause;                        /* mc_step_to_hits: pause at port 0's Fighter_procCollResolve */
  int at_hits;                          /* paused there, mid-frame */
  int render_pause;                     /* mc_step_to_render: pause at the frame's first render callback */
  int at_render;                        /* paused there, mid-frame */
  int hit_slots;                        /* mc_apply_hit descriptors used at this pause */
  int standin_port;                     /* -1 none (plan 7, standin.c) */
  uint32_t standin_gobj;                /* its fighter GObj, refreshed at each boundary */
  int standin_req;                      /* next boundary: 1 wake, 2 sleep */
  float standin_x, standin_y, standin_facing;
  int standin_depth;
  int edge_assist;                      /* mc_set_edge_assist: port 0's walks teeter at edges */                    /* > 0 while one of its procs runs: its sounds are dropped */
  int online;                           /* MC_ONLINE_RNG: Slippi online's per-frame RNG sync */
  uint32_t onload_slot, onload_orig, unload_slot, unload_orig;   /* hijacked GameMode callbacks */
  /* frame-boundary jobs (room.c), run inside the guest at the start of the next mc_step */
  int room_pending;                     /* a validated room waits in room_* below */
  McVec2 *room_verts; McLine *room_lines; McJoint *room_joints;   /* host copies, MC_MAX_* each */
  McCollision room;
  uint32_t room_guest;
  int place_pending[4]; float place_x[4], place_y[4];   /* mc_place_fighter, run after a pending room */                  /* guest block holding the current room (freed on the next load) */
  int dead;
  char error[256];
} Machine;

extern Machine *g_m;
_Noreturn void mc_die(const char *fmt, ...);
void tb_advance_to(uint64_t t);
uint32_t call_guest(Cpu *c, uint32_t addr, uint32_t r3, uint32_t r4);
void service(Cpu *c);
void room_boundary_jobs(Cpu *c);
void standin_boundary_jobs(Cpu *c);   /* standin.c: wake or sleep the stand-in */
int standin_captured(Cpu *c);
int standin_hit_sound(Cpu *c, int32_t id);   /* a hit sound on the stand-in, which is heard */
void room_ground_fighter(Cpu *c, uint32_t gobj, float x, float y);   /* room.c: onto the floor under (x, y), else Fall */           /* its action is CapturePulledHi..CaptureDamageLw */
void worker_call(void (*fn)(void *), void *arg);   /* worker.c: run fn on the core's own thread, wait */
void worker_stop(void);
void sfx_reset(void);   /* sfx.c: empty the sound event queue */   /* room.c: pending room loads and placements */
#define RET return c->spr[SPR_LR]

/* gx.c */
uint8_t *gx_append(uint32_t size);
void gx_mark(Cpu *c, uint32_t gobj);
void gx_frame_done(void);
void gx_free(Machine *m);
void gx_draw_free(void);
void gx_track_frame(const uint8_t *fifo, uint32_t len, const uint8_t *mem);

