/* standin.c - the stand-in fighter (standin.h) */
#include <stdio.h>
#include <string.h>
#include "mc_internal.h"
#include "melee_core.h"
#include "standin.h"

#define PLAYER_BLOCK(p) (0x80453080u + 0xE90u * (uint32_t)(p))
#define FN_FIGHTER_SPAWN 0x80068354u     /* Fighter_Spawn(gobj): placed from the player block, camera and hurtboxes on */
#define FN_WAIT_OR_FALL 0x8007D92Cu      /* ftCommon_8007D92C(gobj): Wait if grounded, else Fall */
#define FN_SLEEP 0x800D4F24u             /* ftCo_800D4F24(gobj, 0): release grabs, drop items, Sleep */
#define ACT_SLEEP 11u
#define ACT_CAPTURE_FIRST 0xDFu          /* CapturePulledHi */
#define ACT_CAPTURE_LAST 0xE4u           /* CaptureDamageLw */
#define ACT_DAMAGE_FIRST 0x4Bu           /* DamageHi1 */
#define ACT_DAMAGE_LAST 0x5Bu            /* DamageFlyRoll */
#define ACT_THROWN_FIRST 0xEFu           /* ThrownF */
#define ACT_THROWN_LAST 0xF3u            /* ThrownLwWomen */

static int set_error(const char *why) { snprintf(g_m->error, sizeof g_m->error, "%s", why); return -1; }
static void stf(Cpu *c, uint32_t ea, float f) { uint32_t v; memcpy(&v, &f, 4); st32(c, ea, v); }

int mc_standin_set(int port) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  if (port < -1 || port > 3 || port == 0) return set_error("stand-in: port must be 1..3, or -1");
  m->standin_port = port;
  m->standin_gobj = 0;
  m->standin_req = port >= 0 ? 2 : 0;
  return 0;
}
int mc_standin_wake(float x, float y, float facing) {
  Machine *m = g_m;
  if (!m || m->dead || m->standin_port < 0) return -1;
  m->standin_req = 1; m->standin_x = x; m->standin_y = y; m->standin_facing = facing < 0 ? -1.0f : 1.0f;
  return 0;
}
int mc_standin_sleep(void) {
  Machine *m = g_m;
  if (!m || m->dead || m->standin_port < 0) return -1;
  m->standin_req = 2;
  return 0;
}
int mc_standin_held(void) {
  Machine *m = g_m;
  if (!m || m->dead || m->standin_port < 0) return -1;
  uint32_t gobj = ld32(&m->cpu, PLAYER_BLOCK(m->standin_port) + 0xB0u);
  if (!gobj) return 0;
  return ld32(&m->cpu, ld32(&m->cpu, gobj + 0x2C) + 0x1A58u) != 0;
}
int standin_captured(Cpu *c) {
  uint32_t a = ld32(c, ld32(c, g_m->standin_gobj + 0x2C) + 0x10u);
  return a >= ACT_CAPTURE_FIRST && a <= ACT_CAPTURE_LAST;
}

/* A common-bank sound (hits, impacts: id < 10000) started while the stand-in is held, thrown or reeling from a hit: a throw's or
 * pummel's impact, which Melee plays from the victim's side. Its own voice, steps and landings stay silent. */
int standin_hit_sound(Cpu *c, int32_t id) {
  if (id < 0 || id >= 10000) return 0;
  uint32_t a = ld32(c, ld32(c, g_m->standin_gobj + 0x2C) + 0x10u);
  return standin_captured(c) || (a >= ACT_DAMAGE_FIRST && a <= ACT_DAMAGE_LAST) || (a >= ACT_THROWN_FIRST && a <= ACT_THROWN_LAST);
}

void standin_boundary_jobs(Cpu *c) {
  Machine *m = g_m;
  if (m->standin_port < 0) { m->standin_gobj = 0; return; }
  uint32_t blk = PLAYER_BLOCK(m->standin_port), gobj = ld32(c, blk + 0xB0u);
  m->standin_gobj = gobj;
  if (!gobj || !m->standin_req) return;
  uint32_t fp = ld32(c, gobj + 0x2C);
  if (m->standin_req == 1) {
    for (uint32_t slot = 0; slot < 2; slot++) {
      stf(c, blk + 0x10u + 12u * slot, m->standin_x); stf(c, blk + 0x14u + 12u * slot, m->standin_y); stf(c, blk + 0x18u + 12u * slot, 0.0f);
      st16(c, blk + 0x60u + 2u * slot, 0);
    }
    stf(c, blk + 0x40u, m->standin_facing);
    uint32_t percent = ld32(c, fp + 0x1830u);   /* mc_set_percent's, which Fighter_Spawn would reload from the player block */
    call_guest(c, FN_FIGHTER_SPAWN, gobj, 0);
    st32(c, fp + 0x1830u, percent);
    /* Fighter_Spawn's own floor check fails for a fighter that slept (it leaves it airborne): ground it as a placement does */
    room_ground_fighter(c, gobj, m->standin_x, m->standin_y);
    call_guest(c, FN_WAIT_OR_FALL, gobj, 0);
  } else if (ld32(c, fp + 0x10u) != ACT_SLEEP) {
    call_guest(c, FN_SLEEP, gobj, 0);
  }
  m->standin_req = 0;
}
