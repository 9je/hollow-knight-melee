/* melee_core.h - libmelee_core C API (spec §3.1). One instance per process; not thread-safe.
 * Every function returning int: 0 = ok, -1 = error (see mc_error()). After a guest error the
 * machine is dead and every call fails until mc_init succeeds again. */
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

int mc_init(const char *mem1_path, const char *iso_path);   /* boots to the first frame boundary */
void mc_shutdown(void);
const char *mc_error(void);
int mc_step(void);                 /* runs exactly one frame */
uint64_t mc_frame(void);           /* frame boundaries reached since mc_init */
uint32_t mc_mem_crc32(void);       /* CRC-32 of MEM1 (determinism checks) */
uint32_t mc_peek32(uint32_t ea);   /* big-endian MEM1 read; 0 outside MEM1 or with no machine */

typedef struct { uint16_t buttons; int8_t stick_x, stick_y, cstick_x, cstick_y; uint8_t trigger_l, trigger_r; } McInput;  /* raw PADStatus values */
typedef struct {
  uint32_t action;               /* action state, Fighter+0x10 */
  float x, y;                    /* +0xB0, +0xB4 */
  float facing;                  /* +0x2C: 1 right, -1 left */
  float percent;                 /* +0x1830 */
  float shield;                  /* +0x1998 */
  float self_vel_x, self_vel_y;  /* +0x80, +0x84 */
  float kb_vel_x, kb_vel_y;      /* +0x8C, +0x90 */
  float ground_vel;              /* +0xEC */
  uint32_t airborne;             /* +0xE0: 0 grounded, 1 airborne */
} McFighter;
#define MC_START_SIZE 0x138
void mc_set_input(int port, const McInput *in);     /* NULL = no controller */
/* Starts a match; on return the game is at the in-game scene's first frame boundary, so the next
 * mc_step() runs the match's first frame (Slippi frame -123). */
#define MC_ONLINE_RNG 1u   /* flags: replay a Slippi online match (its per-frame RNG sync, seeded by `seed`) */
int mc_start_match(const uint8_t start[MC_START_SIZE], uint32_t seed, uint32_t flags);
void mc_solo_fox_start(uint8_t out[MC_START_SIZE]);
int mc_read_fighter(int port, int follower, McFighter *out);   /* 0 ok, 1 no such fighter, -1 error */
/* The fighter's joint world matrices (3x4 row-major, Melee units), depth first, as Melee's render pass leaves
 * them each frame; read-only. Returns the number written, 0 when there is no fighter, -1 on error. */
int mc_read_bones(int port, int follower, float out[][12], int max);
#include "room.h"

#ifdef __cplusplus
}
#endif
