/* room.c - room collision at runtime: export the live MapCollData, load a new one at the frame boundary
 * through the game's own mpLibLoad (after freeing the previous arrays), and the jobs that need the guest. */
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include "mc_internal.h"

#define FN_MPLIBLOAD 0x8004D288u
#define FN_HSD_MEMALLOC 0x8037F1E4u
#define FN_HSD_FREE 0x8037F1B0u
#define ADDR_COLL_DATA 0x804D64B4u       /* mpLib_804D64B4: the loaded MapCollData */
#define ADDR_COLL_ARRAYS 0x804D64B8u     /* groundCollVtx, groundCollLine, groundCollJoint */
#define ADDR_ISLANDS 0x80458E88u         /* mpIsland_80458E88 */

static int set_error(const char *why) { snprintf(g_m->error, sizeof g_m->error, "%s", why); return -1; }

const McCollision *mc_get_collision(void) {
  static McVec2 v[MC_MAX_VERTS]; static McLine l[MC_MAX_LINES]; static McJoint j[MC_MAX_JOINTS];
  static McCollision out;
  Machine *m = g_m;
  if (!m || m->dead) return NULL;
  Cpu *c = &m->cpu;
  uint32_t d = ld32(c, ADDR_COLL_DATA);
  if (!d) return NULL;
  out.n_verts = (int)ld32(c, d + 4); out.n_lines = (int)ld32(c, d + 0xC); out.n_joints = (int)ld32(c, d + 0x28);
  if (out.n_verts > MC_MAX_VERTS || out.n_lines > MC_MAX_LINES || out.n_joints > MC_MAX_JOINTS) return NULL;
  uint32_t vp = ld32(c, d), lp = ld32(c, d + 8), jp = ld32(c, d + 0x24);
  for (int i = 0; i < out.n_verts; i++) { uint32_t x = ld32(c, vp + 8u * i), y = ld32(c, vp + 8u * i + 4); memcpy(&v[i].x, &x, 4); memcpy(&v[i].y, &y, 4); }
  for (int i = 0; i < out.n_lines; i++) {
    uint32_t a = lp + 16u * (uint32_t)i;
    l[i] = (McLine){(uint16_t)ld16(c, a), (uint16_t)ld16(c, a + 2), (int16_t)ld16(c, a + 4), (int16_t)ld16(c, a + 6),
                    (int16_t)ld16(c, a + 8), (int16_t)ld16(c, a + 10), (uint16_t)ld16(c, a + 12), (uint16_t)ld16(c, a + 14)};
  }
  for (int g = 0; g < MC_GROUPS; g++) { out.start[g] = (int16_t)ld16(c, d + 0x10 + 4u * g); out.count[g] = (int16_t)ld16(c, d + 0x12 + 4u * g); }
  for (int i = 0; i < out.n_joints; i++) {
    uint32_t a = jp + 0x28u * (uint32_t)i, f;
    for (int g = 0; g < MC_GROUPS; g++) { j[i].start[g] = (int16_t)ld16(c, a + 4u * g); j[i].count[g] = (int16_t)ld16(c, a + 2 + 4u * g); }
    f = ld32(c, a + 0x14); memcpy(&j[i].left, &f, 4);
    f = ld32(c, a + 0x18); memcpy(&j[i].bottom, &f, 4);
    f = ld32(c, a + 0x1C); memcpy(&j[i].right, &f, 4);
    f = ld32(c, a + 0x20); memcpy(&j[i].top, &f, 4);
    j[i].vtx_start = (int16_t)ld16(c, a + 0x24); j[i].vtx_count = (int16_t)ld16(c, a + 0x26);
  }
  out.x2c = ld32(c, d + 0x2C);
  out.verts = v; out.lines = l; out.joints = j;
  return &out;
}

int mc_load_collision(const McCollision *col) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  if (!m->in_match) return set_error("no match is running");
  char why[200];
  if (mc_check_collision(col, why, sizeof why) != 0) return set_error(why);
  if (!m->room_verts) {
    m->room_verts = malloc(sizeof *m->room_verts * MC_MAX_VERTS);
    m->room_lines = malloc(sizeof *m->room_lines * MC_MAX_LINES);
    m->room_joints = malloc(sizeof *m->room_joints * MC_MAX_JOINTS);
    if (!m->room_verts || !m->room_lines || !m->room_joints) return set_error("out of memory");
  }
  memcpy(m->room_verts, col->verts, sizeof *col->verts * (size_t)col->n_verts);
  memcpy(m->room_lines, col->lines, sizeof *col->lines * (size_t)col->n_lines);
  memcpy(m->room_joints, col->joints, sizeof *col->joints * (size_t)col->n_joints);
  m->room = *col;
  m->room.verts = m->room_verts; m->room.lines = m->room_lines; m->room.joints = m->room_joints;
  m->room_pending = 1;
  return 0;
}

static float ldf(Cpu *c, uint32_t ea) { uint32_t u = ld32(c, ea); float f; memcpy(&f, &u, 4); return f; }
static void stf(Cpu *c, uint32_t ea, float f) { uint32_t u; memcpy(&u, &f, 4); st32(c, ea, u); }

/* Writes the pending room as one guest block: MapCollData, verts, lines, joints. */
static uint32_t write_room(Cpu *c, const McCollision *r) {
  uint32_t size = 0x30u + 8u * (uint32_t)r->n_verts + 16u * (uint32_t)r->n_lines + 0x28u * (uint32_t)r->n_joints;
  uint32_t d = call_guest(c, FN_HSD_MEMALLOC, size, 0);
  if (!d) mc_die("room: HSD_MemAlloc(%u) failed", size);
  uint32_t vp = d + 0x30u, lp = vp + 8u * (uint32_t)r->n_verts, jp = lp + 16u * (uint32_t)r->n_lines;
  st32(c, d, vp); st32(c, d + 4, (uint32_t)r->n_verts); st32(c, d + 8, lp); st32(c, d + 0xC, (uint32_t)r->n_lines);
  for (int g = 0; g < MC_GROUPS; g++) { st16(c, d + 0x10 + 4u * g, (uint16_t)r->start[g]); st16(c, d + 0x12 + 4u * g, (uint16_t)r->count[g]); }
  st32(c, d + 0x24, jp); st32(c, d + 0x28, (uint32_t)r->n_joints); st32(c, d + 0x2C, r->x2c);
  for (int i = 0; i < r->n_verts; i++) { stf(c, vp + 8u * i, r->verts[i].x); stf(c, vp + 8u * i + 4, r->verts[i].y); }
  for (int i = 0; i < r->n_lines; i++) {
    const McLine *l = &r->lines[i]; uint32_t a = lp + 16u * (uint32_t)i;
    st16(c, a, l->v0); st16(c, a + 2, l->v1); st16(c, a + 4, (uint16_t)l->prev0); st16(c, a + 6, (uint16_t)l->next0);
    st16(c, a + 8, (uint16_t)l->prev1); st16(c, a + 10, (uint16_t)l->next1); st16(c, a + 12, l->hi_flags); st16(c, a + 14, l->lo_flags);
  }
  for (int i = 0; i < r->n_joints; i++) {
    const McJoint *J = &r->joints[i]; uint32_t a = jp + 0x28u * (uint32_t)i;
    for (int g = 0; g < MC_GROUPS; g++) { st16(c, a + 4u * g, (uint16_t)J->start[g]); st16(c, a + 2 + 4u * g, (uint16_t)J->count[g]); }
    stf(c, a + 0x14, J->left); stf(c, a + 0x18, J->bottom); stf(c, a + 0x1C, J->right); stf(c, a + 0x20, J->top);
    st16(c, a + 0x24, (uint16_t)J->vtx_start); st16(c, a + 0x26, (uint16_t)J->vtx_count);
  }
  return d;
}

/* Frees every island node (each list once; the tails at +8/+C point into the lists). */
static void free_islands(Cpu *c) {
  static const uint32_t lists[] = {0x00, 0x04, 0x10, 0x14, 0x18, 0x1C, 0x20};
  uint32_t seen[4096]; int n = 0;
  for (size_t k = 0; k < sizeof lists / sizeof lists[0]; k++)
    for (uint32_t p = ld32(c, ADDR_ISLANDS + lists[k]); p; p = ld32(c, p)) {
      int dup = 0;
      for (int i = 0; i < n; i++) dup |= seen[i] == p;
      if (dup) break;
      if (n == 4096) mc_die("room: more than 4096 islands");
      seen[n++] = p;
    }
  for (int i = 0; i < n; i++) call_guest(c, FN_HSD_FREE, seen[i], 0);
}

/* Free everything the previous load allocated first, then allocate and load: with the same sizes the
 * allocator hands back the same blocks every time (the leak check relies on it). Nothing reads the old
 * collision between the frees and mpLibLoad: this runs between frames, inside one host job. */
#define FN_MPCOLL_SET_POS 0x80043680u    /* mpColl_80043680(CollData *, Vec3 *) */
#define FN_CHECK_FLOOR 0x8004F008u       /* mpCheckFloor */
#define FN_CLEAR_FLOOR_SKIP 0x8004CBF4u  /* mpClearFloorSkip(CollData *) */
#define FN_FALL_ENTER 0x800CC730u        /* ftCo_Fall_Enter(gobj) */
#define MS_CLIFF_FIRST 252u              /* CliffCatch */
#define MS_CLIFF_LAST 263u               /* CliffJumpQuick2 */
#define PLAYER_BLOCK(p) (0x80453080u + 0xE90u * (uint32_t)(p))
#define FN_SET_AIRBORNE 0x8007D5D4u      /* ftCommon_8007D5D4(fp) */
#define FN_CAMERA_SNAP 0x80076064u       /* ftCamera_80076064(fp) */
#define FN_DAMAGEFALL_ENTER 0x80090780u /* ftCo_80090780(gobj): DamageFall, tumbling */
#define FN_HITLAG_EXIT 0x8006D10Cu       /* Fighter_8006D10C(gobj): leave hitlag (post-hitlag callback, clears x2219_b5) */

int mc_place_fighter(int port, float x, float y) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  if (port < 0 || port > 3) return set_error("port must be 0..3");
  if (!m->in_match) return set_error("no match is running");
  if (!isfinite(x) || !isfinite(y)) return set_error("position must be finite");
  if (!ld32(&m->cpu, 0x80453080u + 0xE90u * (uint32_t)port + 0xB0u)) return set_error("no fighter on that port");
  m->place_pending[port] = 1; m->place_x[port] = x; m->place_y[port] = y;
  return 0;
}

int mc_respawn_fighter(int port, float x, float y) {
  int rc = mc_place_fighter(port, x, y);
  if (rc == 0) g_m->place_pending[port] = 2;
  return rc;
}

int mc_tumble_fighter(int port, float x, float y) {
  int rc = mc_place_fighter(port, x, y);
  if (rc == 0) g_m->place_pending[port] = 3;
  return rc;
}

/* bool mpCheckFloor(ax, ay, bx, by, y_offset, Vec3 *out, int *line, u32 *flags, Vec3 *normal,
 *                   line_skip, joint_skip, joint_only, cb, gobj): the floor crossed by segment a->b.
 * Nine integer arguments: the ninth goes on the stack, so the call runs on a temporary guest frame
 * that also holds the outputs. Returns the line id, or -1; *y_out gets the contact height. */
static int floor_between(Cpu *c, float x, float top, float bottom, float *y_out, uint32_t *flags, float normal[3]) {
  uint32_t sp = c->gpr[1], f = (sp - 0x60u) & ~15u;   /* frame: +0 back chain, +8 arg 9, +0x10 vec, +0x1C line, +0x20 flags, +0x30 normal */
  st32(c, f, sp);
  st32(c, f + 8, 0);                                   /* gobj = NULL */
  const float a[5] = {x, top, x, bottom, 0.0f};
  for (int k = 0; k < 5; k++) c->fpr[1 + k].ps0 = c->fpr[1 + k].ps1 = (double)a[k];
  c->gpr[1] = f;
  c->gpr[5] = f + 0x20;                                /* flags_out */
  c->gpr[6] = f + 0x30;                                /* normal_out */
  c->gpr[7] = c->gpr[8] = c->gpr[9] = 0xFFFFFFFFu;    /* line_id_skip, joint_id_skip, joint_id_only */
  c->gpr[10] = 0;                                      /* no callback */
  uint32_t hit = call_guest(c, FN_CHECK_FLOOR, f + 0x10, f + 0x1C);   /* vec_out, line_id_out */
  c->gpr[1] = sp;
  if (!hit) return -1;
  *y_out = ldf(c, f + 0x14);
  *flags = ld32(c, f + 0x20);
  for (int k = 0; k < 3; k++) normal[k] = ldf(c, f + 0x30 + 4u * k);
  return (int)ld32(c, f + 0x1C);
}
static int floor_under(Cpu *c, float x, float y, float *y_out, uint32_t *flags, float normal[3]) {
  return floor_between(c, x, y + 10.0f, y - 10.0f, y_out, flags, normal);
}
/* callable from inside a guest call (frame.c's hooks): the caller's registers are put back */
int room_floor_between(Cpu *c, float x, float top, float bottom) {
  uint32_t gpr[32], cr = c->cr, fpscr = c->fpscr, lr = c->spr[SPR_LR], ctr = c->spr[SPR_CTR];
  GekkoPS fpr[32];
  memcpy(gpr, c->gpr, sizeof gpr); memcpy(fpr, c->fpr, sizeof fpr);
  float y, n[3]; uint32_t fl;
  int hit = floor_between(c, x, top, bottom, &y, &fl, n) >= 0;
  memcpy(c->gpr, gpr, sizeof gpr); memcpy(c->fpr, fpr, sizeof fpr);
  c->cr = cr; c->fpscr = fpscr; c->spr[SPR_LR] = lr; c->spr[SPR_CTR] = ctr;
  return hit;
}

/* Moves one fighter GObj to (x, y), keeping its action and velocity where Melee allows it:
 * - ledge actions (CliffCatch..CliffJumpQuick2) hold a ledge line id from the old room: they become Fall;
 * - a grounded fighter stands on the floor within 10 units (mpCheckFloor), else falls.
 * moved == 0 (a fighter not placed after a room load): only the line ids the old room invalidated are
 * renewed; position, ECB and camera stay as they are. */
static void place_gobj(Cpu *c, uint32_t gobj, float x, float y, int moved, int respawn) {   /* respawn: 1 Fall, 2 tumbling */
  uint32_t fp = ld32(c, gobj + 0x2C);
  uint32_t action = ld32(c, fp + 0x10);
  call_guest(c, FN_CLEAR_FLOOR_SKIP, fp + 0x6F0u, 0);   /* a drop-through in progress named an old line */
  if (respawn) {                                        /* falling, still: no momentum, knockback or hitlag */
    /* hitlag is left through Melee's own exit (its counter alone would leave x2219_b5, "frozen in hitlag", set:
     * a fighter respawned mid-hit would never move again) */
    if ((ld8(c, fp + 0x2219u) & 0x04u) || ldf(c, fp + 0x195Cu) > 0.0f) {
      stf(c, fp + 0x195Cu, 0.0f);
      call_guest(c, FN_HITLAG_EXIT, gobj, 0);
    }
    call_guest(c, FN_SET_AIRBORNE, fp, 0);
    call_guest(c, respawn == 2 ? FN_DAMAGEFALL_ENTER : FN_FALL_ENTER, gobj, 0);
    static const uint32_t vel[] = {0x80, 0x84, 0x88, 0x8C, 0x90, 0x94, 0xEC, 0x195C};   /* self, kb, ground, hitlag */
    for (unsigned k = 0; k < sizeof vel / sizeof vel[0]; k++) stf(c, fp + vel[k], 0.0f);
  } else if (action >= MS_CLIFF_FIRST && action <= MS_CLIFF_LAST) {
    call_guest(c, FN_SET_AIRBORNE, fp, 0);
    call_guest(c, FN_FALL_ENTER, gobj, 0);
  } else if (ld32(c, fp + 0xE0) == 0) {
    float fy, n[3]; uint32_t fl;
    int line = floor_under(c, x, y, &fy, &fl, n);
    if (line >= 0) {
      if (moved) y = fy;
      st32(c, fp + 0x83C, (uint32_t)line);          /* CollData.floor: index, flags, normal */
      st32(c, fp + 0x840, fl);
      for (int k = 0; k < 3; k++) stf(c, fp + 0x844 + 4u * k, n[k]);
    } else {   /* the floor went (a collapse, a rebuilt room): Fall, as off a ledge. A grounded action left running
                * would follow its floor line id, now another line of the new room, and carry the fighter onto it */
      call_guest(c, FN_SET_AIRBORNE, fp, 0);
      call_guest(c, FN_FALL_ENTER, gobj, 0);
    }
  }
  if (!moved) return;
  stf(c, fp + 0xB0, x); stf(c, fp + 0xB4, y);
  stf(c, fp + 0xBC, x); stf(c, fp + 0xC0, y);      /* prev_pos */
  call_guest(c, FN_MPCOLL_SET_POS, fp + 0x6F0u, fp + 0xB0u);
  call_guest(c, FN_CAMERA_SNAP, fp, 0);
}

#define FN_SET_GROUNDED 0x8007D6A4u      /* ftCommon_8007D6A4(fp) */
/* plan 7: a freshly spawned stand-in onto the floor within 10 units of (x, y), as a placement does; left airborne if
 * none. Its floor line is set first: ftCommon_8007D6A4 asserts the fighter stands on a line */
void room_ground_fighter(Cpu *c, uint32_t gobj, float x, float y) {
  uint32_t fp = ld32(c, gobj + 0x2C);
  float fy, n[3]; uint32_t fl;
  int line = floor_under(c, x, y, &fy, &fl, n);
  if (line < 0) return;
  st32(c, fp + 0x83C, (uint32_t)line);
  st32(c, fp + 0x840, fl);
  for (int k = 0; k < 3; k++) stf(c, fp + 0x844 + 4u * k, n[k]);
  call_guest(c, FN_SET_GROUNDED, fp, 0);
  place_gobj(c, gobj, x, fy, 1, 0);
}

#define FN_EF_SPAWN 0x8005FDDCu         /* efSync_Spawn(kind, gobj, ...): an effect */
#define FN_FT_PLAY_SFX 0x80088148u      /* ft_PlaySFX(fp, id, vol, pan) */
#define FN_FT_VOICE 0x800D38B8u         /* ftCo_800D38B8(fp, id): a voice line */
#define FN_FT_SFX_VOICE 0x800881D8u     /* ft_800881D8(fp, id, vol, pan) */
#define EF_KO_BLAST 0x42Bu
#define ADDR_FT_COMMON_DATA 0x804D6554u /* p_ftCommonData */
/* mc_ko: what ftCo_800D3BC8 (DeadDown) does that shows: the blast effect pointing up from the fighter, the blast sound, the
 * KO voice; the fighter then goes invisible (setDeadInvisible). Its action, stocks and Melee's camera are left alone. While
 * KO'd it is held where it was. */
static void ko_boundary_jobs(Cpu *c) {
  Machine *m = g_m;
  for (int p = 0; p < 4; p++) {
    uint32_t gobj = ld32(c, PLAYER_BLOCK(p) + 0xB0u);
    if (!gobj) { m->ko_pending[p] = m->ko[p] = 0; continue; }
    uint32_t fp = ld32(c, gobj + 0x2C);
    if (m->ko_pending[p] && !m->ko[p]) {
      m->ko[p] = 1;
      m->ko_x[p] = ldf(c, fp + 0xB0); m->ko_y[p] = ldf(c, fp + 0xB4);
      uint32_t sp = c->gpr[1], f = (sp - 0x40u) & ~15u;   /* frame: +0 back chain, +0x10 pos, +0x1C angle */
      st32(c, f, sp);
      stf(c, f + 0x10, m->ko_x[p]); stf(c, f + 0x14, m->ko_y[p]); stf(c, f + 0x18, ldf(c, fp + 0xB8));
      stf(c, f + 0x1C, 0.0f);
      uint32_t c1 = ld32(c, 0x804D650Cu), c2 = ld32(c, 0x804D6508u);   /* the blast's colours, Player_GetUnk45's slot 0 */
      c->gpr[1] = f;
      c->gpr[5] = f + 0x10; c->gpr[6] = f + 0x1C; c->gpr[7] = ld32(c, ADDR_FT_COMMON_DATA) + 0x4F4u;
      c->gpr[8] = (uint32_t)ld8(c, c1) << 16 | (uint32_t)ld8(c, c1 + 1) << 8 | ld8(c, c1 + 2);
      c->gpr[9] = (uint32_t)ld8(c, c2) << 16 | (uint32_t)ld8(c, c2 + 1) << 8 | ld8(c, c2 + 2);
      call_guest(c, FN_EF_SPAWN, EF_KO_BLAST, gobj);
      c->gpr[1] = sp;
      c->gpr[5] = 0x7F; c->gpr[6] = 0x40;
      call_guest(c, FN_FT_PLAY_SFX, fp, 0x61);
      uint32_t sfx = ld32(c, ld32(c, fp + 0x10Cu) + 0x4Cu);
      if (m->ko_pending[p] == 2) {                       /* the star KO's scream (ftCo_800D40B8's DeadUpStar) */
        c->gpr[5] = 127; c->gpr[6] = 64;
        call_guest(c, FN_FT_SFX_VOICE, fp, ld32(c, sfx + 0xC));
      } else {
        call_guest(c, FN_FT_VOICE, fp, ld32(c, sfx + 4));
        call_guest(c, FN_FT_VOICE, fp, ld32(c, sfx + 8));
      }
      st8(c, fp + 0x221Eu, (uint8_t)(ld8(c, fp + 0x221Eu) | 0x80u));
      st8(c, fp + 0x221Fu, (uint8_t)(ld8(c, fp + 0x221Fu) | 0x40u));
    }
    m->ko_pending[p] = 0;
    if (!m->ko[p]) continue;
    static const uint32_t vel[] = {0x80, 0x84, 0x88, 0x8C, 0x90, 0x94, 0xEC};   /* self, kb, ground */
    for (unsigned k = 0; k < sizeof vel / sizeof vel[0]; k++) stf(c, fp + vel[k], 0.0f);
    stf(c, fp + 0xB0, m->ko_x[p]); stf(c, fp + 0xB4, m->ko_y[p]);
    call_guest(c, FN_MPCOLL_SET_POS, fp + 0x6F0u, fp + 0xB0u);
  }
}

void room_boundary_jobs(Cpu *c) {
  Machine *m = g_m;
  standin_boundary_jobs(c);   /* plan 7: wake or sleep the stand-in before this frame's placements */
  int loaded = m->room_pending;
  if (m->room_pending) {
    m->room_pending = 0;
    for (int k = 0; k < 3; k++) call_guest(c, FN_HSD_FREE, ld32(c, ADDR_COLL_ARRAYS + 4u * k), 0);
    free_islands(c);
    if (m->room_guest) call_guest(c, FN_HSD_FREE, m->room_guest, 0);   /* our previous MapCollData (not the stage file's) */
    uint32_t d = write_room(c, &m->room);
    call_guest(c, FN_MPLIBLOAD, d, 0);
    m->room_guest = d;
  }
  /* placements; after a new room, every fighter not placed explicitly is placed where it is, so no
   * fighter keeps a floor or ledge line id from the old room */
  for (int p = 0; p < 4; p++)
    for (int follower = 0; follower < 2; follower++) {
      uint32_t gobj = ld32(c, PLAYER_BLOCK(p) + (follower ? 0xB4u : 0xB0u));
      if (!gobj) continue;
      uint32_t fp = ld32(c, gobj + 0x2C);
      if (gobj == m->standin_gobj && (ld8(c, fp + 0x221Fu) & 0x10u)) continue;   /* asleep: Fall would wake it */
      if (m->place_pending[p] && !follower && m->ko[p]) {   /* back from mc_ko */
        m->ko[p] = 0;
        st8(c, fp + 0x221Eu, (uint8_t)(ld8(c, fp + 0x221Eu) & ~0x80u));   /* invisible */
        st8(c, fp + 0x221Fu, (uint8_t)(ld8(c, fp + 0x221Fu) & ~0x40u));
      }
      if (m->place_pending[p] && !follower) place_gobj(c, gobj, m->place_x[p], m->place_y[p], 1, m->place_pending[p] - 1);
      else if (loaded) place_gobj(c, gobj, ldf(c, fp + 0xB0), ldf(c, fp + 0xB4), 0, 0);
    }
  for (int p = 0; p < 4; p++) m->place_pending[p] = 0;
  /* In a room Melee's camera is never shown, and it can lag behind a fighter (it glides after a placement): an
   * off-screen fighter is not drawn (ftLib_UpdateScreenVisibility). Fighter x2229_b3, Master Hand's "never
   * off-screen, never magnified" flag, has no other effect; Fighter init clears it, so it is set every frame. */
  if (m->room_guest)
    for (int p = 0; p < 4; p++)
      for (int follower = 0; follower < 2; follower++) {
        uint32_t gobj = ld32(c, PLAYER_BLOCK(p) + (follower ? 0xB4u : 0xB0u));
        if (!gobj) continue;
        uint32_t fp = ld32(c, gobj + 0x2C);
        if (gobj == m->standin_gobj && (ld8(c, fp + 0x221Fu) & 0x10u)) continue;   /* asleep */
        st8(c, fp + 0x2229u, (uint8_t)(ld8(c, fp + 0x2229u) | 0x10u));
      }
  edge_assist_boundary(c);
  ko_boundary_jobs(c);
  for (int p = 0; p < 4; p++) {   /* mc_bounce (hazard pogo): the aerial ends in Fall, going up; no fast fall; double jump back */
    if (!m->bounce_pending[p]) continue;
    m->bounce_pending[p] = 0;
    uint32_t gobj = ld32(c, PLAYER_BLOCK(p) + 0xB0u);
    if (!gobj) continue;
    uint32_t fp = ld32(c, gobj + 0x2C);
    if (ld32(c, fp + 0xE0) == 0) continue;                       /* landed meanwhile */
    call_guest(c, FN_FALL_ENTER, gobj, 0);
    stf(c, fp + 0x84, m->bounce_vy[p]);                          /* self_vel.y */
    st8(c, fp + 0x221A, (uint8_t)(ld8(c, fp + 0x221A) & ~0x08u)); /* fall_fast */
    if (ld8(c, fp + 0x1968) > 1) st8(c, fp + 0x1968, 1);         /* x1968_jumpsUsed: the double jump is back */
  }
  gates_boundary_jobs(c);   /* spec §6: locked moves, after placements (a placed fighter's jumps) */
}
#define ADDR_STAGE_INFO 0x8049E6C8u

int mc_get_bounds(McBox *blast, McBox *cam) {
  Machine *m = g_m;
  if (!m || m->dead || !m->in_match) return -1;
  Cpu *c = &m->cpu;
  float ox = ldf(c, ADDR_STAGE_INFO + 0x10), oy = ldf(c, ADDR_STAGE_INFO + 0x14);
  *cam = (McBox){ldf(c, ADDR_STAGE_INFO) + ox, ldf(c, ADDR_STAGE_INFO + 4) + ox, ldf(c, ADDR_STAGE_INFO + 8) + oy, ldf(c, ADDR_STAGE_INFO + 0xC) + oy};
  *blast = (McBox){ldf(c, ADDR_STAGE_INFO + 0x74) + ox, ldf(c, ADDR_STAGE_INFO + 0x78) + ox, ldf(c, ADDR_STAGE_INFO + 0x7C) + oy, ldf(c, ADDR_STAGE_INFO + 0x80) + oy};
  return 0;
}

static int box_ok(const McBox *b) { return isfinite(b->left) && isfinite(b->right) && isfinite(b->top) && isfinite(b->bottom); }

int mc_set_bounds(const McBox *blast, const McBox *cam) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  if (!m->in_match) return set_error("no match is running");
  if (!box_ok(blast) || !box_ok(cam)) return set_error("bounds must be finite");
  if (!(blast->left < blast->right && cam->left < cam->right)) return set_error("bounds: left must be less than right");
  if (!(blast->bottom < blast->top && cam->bottom < cam->top)) return set_error("bounds: bottom must be less than top");
  if (!(blast->left < cam->left && cam->right < blast->right && blast->bottom < cam->bottom && cam->top < blast->top))
    return set_error("bounds: the camera box must be inside the blast zone");
  Cpu *c = &m->cpu;
  stf(c, ADDR_STAGE_INFO + 0x00, cam->left); stf(c, ADDR_STAGE_INFO + 0x04, cam->right);
  stf(c, ADDR_STAGE_INFO + 0x08, cam->top); stf(c, ADDR_STAGE_INFO + 0x0C, cam->bottom);
  stf(c, ADDR_STAGE_INFO + 0x10, 0.0f); stf(c, ADDR_STAGE_INFO + 0x14, 0.0f);
  stf(c, ADDR_STAGE_INFO + 0x74, blast->left); stf(c, ADDR_STAGE_INFO + 0x78, blast->right);
  stf(c, ADDR_STAGE_INFO + 0x7C, blast->top); stf(c, ADDR_STAGE_INFO + 0x80, blast->bottom);
  return 0;
}
