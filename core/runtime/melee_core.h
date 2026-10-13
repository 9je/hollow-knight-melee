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
/* Runs the frame (from its start or a hit pause) up to Melee's drawing (the first GObj render callback): the frame's logic
 * is done, and what mc_read_* report is what the frame ends with. mc_step() finishes the frame (the drawing), which the host
 * may run on another thread while it does other work. mc_step_to_render and mc_step_to_hits finish a frame left paused
 * here before running their own. 0 paused there, 1 the frame ended first, -1 error. */
int mc_step_to_render(void);
int mc_finish_frame(void);         /* finishes a frame paused by mc_step_to_render; nothing otherwise. 0 ok, -1 error */
uint64_t mc_frame(void);           /* frame boundaries reached since mc_init */
uint32_t mc_mem_crc32(void);       /* CRC-32 of MEM1 (determinism checks) */
uint32_t mc_peek32(uint32_t ea);   /* big-endian MEM1 read; 0 outside MEM1 or with no machine */
int mc_poke32(uint32_t ea, uint32_t v);   /* big-endian MEM1 write between frames (tests); -1 outside MEM1 or with no machine */

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
void mc_solo_start(uint8_t out[MC_START_SIZE], int character);      /* Melee external id 0..25 at port 0 */
void mc_standin_start(uint8_t out[MC_START_SIZE], int character);   /* plus a Fox stand-in at port 1 */
/* Edge assist (a deviation from Melee, off by default): port 0's walk stops at a floor's edge (Ottotto) whatever the
 * stick's tilt, as Melee's walk does below 75% tilt. Dash, run and air movement still go off edges. */
int mc_set_edge_assist(int on);
/* Hazard pogo (spec §5.4, not Melee): an airborne fighter's vertical speed becomes vy times its own full-hop speed
 * (jump_v_initial_velocity, so each character bounces to a share of its own jump), a fast fall
 * ends and its double jump is back. 0 done, 1 not airborne (nothing), -1 error. */
int mc_bounce(int port, float vy);
/* Hollow Knight's death while the fighter plays: Melee's KO, the blast (as at the bottom blast line, ftCo_800D3BC8) and the
 * fighter's KO voice where it stands, without the stock loss or rebirth; then it is invisible and held there until its next
 * placement (mc_place_fighter, mc_respawn_fighter) brings it back. star: the star KO's scream (DeadUpStar's voice) instead of
 * the KO voice. Runs at the next frame boundary. 0 done, -1 error. */
int mc_ko(int port, int star);
/* Move gating (spec §6): locked moves (MC_GATE_* bits) don't start. A locked special does nothing when its input comes, a
 * locked double jump counts as already used in the air, a locked wall jump is turned off as for a fighter without one. */
#define MC_GATE_DOUBLE_JUMP 1u
#define MC_GATE_WALL_JUMP 2u
#define MC_GATE_SPECIAL_N 4u    /* Fox: blaster */
#define MC_GATE_SPECIAL_S 8u    /* Illusion */
#define MC_GATE_SPECIAL_HI 16u  /* Fire Fox */
#define MC_GATE_SPECIAL_LW 32u  /* Shine */
int mc_set_gates(int port, uint32_t locked);   /* 0 ok, -1 error */
void mc_fox_standin_start(uint8_t out[MC_START_SIZE]);   /* plan 7: Fox, and a second Fox at port 1 (standin.h) */
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
/* Items the fighter owns (his lasers) are read with his hitboxes: slot 4..7, group = the item's GObj (>= 0x80000000).
 * Reporting such a hit ends the item as Melee ends a laser on a wall (life timer 1) and gives no hitlag. */
int mc_hitlag_frames(int damage);   /* Melee's hitlag for a hit of `damage` (ftCommon_CalcHitlag, capped); -1 error */
/* A hit from outside Melee (a Hollow Knight enemy) on the fighter, applied at the hit pause (mc_step_to_hits) through
 * Melee's environmental hit path, so Melee resolves damage, knockback (crouch cancel included), victim hitlag, DI and
 * teching; a shield (unless unblockable) takes it as Melee's shield does an item's; intangibility (dodges, respawn
 * invincibility) avoids it. from_x/from_y: the attacker, for the launch direction. Returns MC_HIT_*, -1 error. */
typedef struct {
  float damage; int32_t angle, growth, set_kb, base_kb; uint32_t element;
  float shield_damage; uint32_t unblockable; float from_x, from_y;
} McHit;
#define MC_HIT_LANDED 0
#define MC_HIT_SHIELDED 1
#define MC_HIT_INTANGIBLE 2
#define MC_HIT_LATER 3       /* this pause already took 4 hits: apply it at the next one */
int mc_apply_hit(int port, const McHit *hit);
int mc_set_percent(int port, float percent);
/* A fighter's attribute (Fighter co_attrs: fp+0x110 + offset, offset 0..0x180 in 4s; e.g. 0x1C dash_initial_velocity,
 * 0x28 dash_max_velocity, 0x88 weight), for the boost charms. It holds until the fighter is spawned again (a new
 * match). 0 done, 1 no fighter, -1 error (a bad offset). */
int mc_get_attribute(int port, uint32_t offset, float *out);
int mc_set_attribute(int port, uint32_t offset, float value);   /* Fighter dmg.x1830 (Focus, benches, deaths); 0 ok, 1 no fighter */
/* Fox's shield bubble, read-only: Melee draws it as an effect on the fighter's shield joint, which it scales by the
 * shield's size (ftCo_80091D58). hard: Melee's lightshield_amount, 0 lightest .. 1 a full press (opaque). */
typedef struct { uint32_t active; float x, y, radius, hard; } McShield;
int mc_read_shield(int port, int follower, McShield *out);   /* 0 ok, 1 no fighter, -1 error */
/* Melee's items (Fox's lasers among them), read-only: kind (ItemKind), position, velocity per frame, facing. */
typedef struct { uint32_t kind; float x, y, vx, vy, facing; } McItem;
int mc_read_items(McItem out[], int max);   /* count, -1 error */
/* GX capture (off by default): each frame's graphics command stream, as the game wrote it to the GX FIFO
 * (0xCC008000), big-endian, plus marks: from `offset` on, the stream was written by `gobj` (0 = no object), an
 * HSD_GObj with that p_link (8 fighters, 9 items, ...) and gx_link. Display lists, vertex arrays and textures it refers
 * to are in MEM1 (mc_mem1). Capturing changes nothing in the game. */
typedef struct { uint32_t offset, gobj; uint16_t classifier; uint8_t p_link, gx_link; float view[12]; uint32_t owner; uint8_t owner_plink, pad[3]; } McGxMark;
/* view: the camera's (HSD_CObj+0x54). owner: an effect's parent GObj (EF_Effect+8) and its p_link (0xFF none) */
int mc_gx_capture(int on);
/* The last complete frame's stream and marks (valid until the next mc_step). */
int mc_gx_frame(const uint8_t **fifo, uint32_t *len, const McGxMark **marks, int *nmarks);
const uint8_t *mc_mem1(void);   /* MEM1 (24 MiB, big-endian), read-only for the host; NULL with no machine */
/* The last captured frame decoded (gx_draw.c): triangles drawn by the objects whose p_link bit is set in plink_mask,
 * in Melee world units, sorted back to front, as runs of one texture and blend mode to draw in order. Colours are
 * Melee's lighting and TEV with a white texture: draw texture x colour. Valid until the next decode. */
typedef struct { float x, y, z; uint8_t r, g, b, a; float u, v; } McGxVert;
#define MC_GX_OPAQUE 0
#define MC_GX_ALPHA 1
#define MC_GX_ADD 2
typedef struct { uint32_t first, count; int32_t tex; uint8_t blend, p_link, wrap_s, wrap_t; uint32_t gobj; } McGxRun;   /* wrap: 0 clamp, 1 repeat, 2 mirror */
#define MC_GX_ATLAS 0x40000000   /* a run's tex: the shared atlas (4096x4096, wraps already laid out: sample clamped) */
int mc_gx_decode(uint64_t plink_mask, const McGxVert **verts, int *nverts, const McGxRun **runs, int *nruns);
/* A texture a run uses (RGBA8, top row first); ids stay the same while the texture does. */
typedef struct { uint32_t w, h; const uint8_t *rgba; uint32_t gen; } McGxTexture;   /* gen: changes when the id is reused */
int mc_gx_texture(int32_t id, McGxTexture *out);
/* The atlas region (texels) changed since the last call, so a host need not upload all 4096x4096 again; 1 if any. */
int mc_gx_atlas_dirty(uint32_t *x, uint32_t *y, uint32_t *w, uint32_t *h);
uint32_t mc_gx_size_mismatches(void);
uint32_t mc_gx_decode_errors(void);   /* draws stepped over: unreadable vertices or no colour written */   /* self-check: vertex sizes computed for skipped draws that a full parse disagreed with */
/* Melee's sound effects, read-only (no audio is produced: the host plays them). Taps on HSD_AudioSFXStartParam,
 * HSD_AudioSFXKeyOff, KeyOffAll and KeyOffTrack queue one event per call that Melee would accept; mc_read_sfx drains
 * the oldest `max` in order (the queue keeps the newest MC_SFX_QUEUE). start: id (bank = id / 10000), vol 0..254, pan
 * 0..254 (0x80 centre), track (a new sound on a nonzero track cuts the last one on it), mix channel, and the handle
 * Melee returns (-1 once its 96 voices are used up: the recompiled game never frees them). keyoff: handle. keyoff
 * track: track. frame: mc_frame() when Melee made the call. */
#define MC_SFX_START 0u
#define MC_SFX_KEYOFF 1u
#define MC_SFX_KEYOFF_ALL 2u
#define MC_SFX_KEYOFF_TRACK 3u
#define MC_SFX_QUEUE 256
typedef struct { uint32_t kind, id; uint8_t vol, pan, track, channel; int32_t handle; uint32_t frame; } McSfx;
int mc_read_sfx(McSfx out[], int max);   /* count, -1 error */
#include "room.h"

#ifdef __cplusplus
}
#endif
