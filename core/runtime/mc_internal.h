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
  int online;                           /* MC_ONLINE_RNG: Slippi online's per-frame RNG sync */
  uint32_t onload_slot, onload_orig, unload_slot, unload_orig;   /* hijacked GameMode callbacks */
  /* frame-boundary jobs (room.c), run inside the guest at the start of the next mc_step */
  int room_pending;                     /* a validated room waits in room_* below */
  McVec2 *room_verts; McLine *room_lines; McJoint *room_joints;   /* host copies, MC_MAX_* each */
  McCollision room;
  uint32_t room_guest;                  /* guest block holding the current room (freed on the next load) */
  int dead;
  char error[256];
} Machine;

extern Machine *g_m;
_Noreturn void mc_die(const char *fmt, ...);
void tb_advance_to(uint64_t t);
uint32_t call_guest(Cpu *c, uint32_t addr, uint32_t r3, uint32_t r4);
void service(Cpu *c);
void room_boundary_jobs(Cpu *c);   /* room.c: pending room loads and placements */
#define RET return c->spr[SPR_LR]
