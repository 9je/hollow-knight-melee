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
int mc_step(void);                 /* runs exactly one frame (or the rest of one paused by mc_step_to_hits) */
/* Runs the next frame up to port 0's hit resolution (Fighter_procCollResolve, after Melee placed hitboxes and checked
 * attack collision), where a hit reported with mc_report_hit lands in this frame. mc_step() finishes the frame.
 * 0 paused there (or already), 1 the frame ended without reaching it (no fighter), -1 error. */
int mc_step_to_hits(void);
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
  float hitlag;                  /* +0x195C: hitlag frames left */
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
/* Mesh parts (DObjs) in the bones' depth-first order: hidden[i] = 1 when part i is hidden this frame, joint[i] =
 * its joint's index. The game appends parts to a joint's list, so (joint, index within the joint) names a part. */
int mc_read_dobjs(int port, int follower, uint8_t hidden[], uint8_t joint[], int max);
/* Combat (spec §5.1). Fox's active hitboxes (Fighter+0x914) and hurtboxes (+0x11A0) in Melee units, read-only.
 * group: hitboxes of one move instance share it and hit a victim once together. fresh: Melee has recorded no
 * victim since the hitbox was created or its victims were last cleared (a multi-hit move clears them). */
typedef struct {
  uint32_t slot, group;
  float damage, radius;
  int32_t angle, growth, set_kb, base_kb;
  uint32_t element;
  float x, y, prev_x, prev_y;    /* this frame's centre and last frame's: the hitbox sweeps between them */
  uint32_t fresh;
  uint32_t flags;                /* MC_HIT_*: whom Melee lets it hit */
} McHitbox;
#define MC_HIT_AIRBORNE 1u       /* HitCapsule x40_b2: hits airborne fighters */
#define MC_HIT_GROUNDED 2u       /* x40_b3: hits grounded fighters */
#define MC_HIT_FIGHTERS 4u       /* x42_b5: hits fighters at all */
typedef struct { float ax, ay, bx, by, radius; uint32_t state; } McHurtbox;   /* state: 0 normal, 1 off, 2 intangible */
int mc_read_hitboxes(int port, int follower, McHitbox out[], int max);   /* count, -1 error */
int mc_read_hurtboxes(int port, int follower, McHurtbox out[], int max);
/* Fox's hit in `group` landed on something outside Melee: records a victim on the group's hitboxes (as
 * lbColl_80008688 does) and raises dmg.x1914, so Melee's next hit-processing phase gives Fox its own attacker
 * hitlag. Not used by replays. 0 ok, 1 no such fighter or no active hitbox in the group, -1 error. */
int mc_report_hit(int port, int follower, uint32_t group, float damage);
#include "room.h"

#ifdef __cplusplus
}
#endif
