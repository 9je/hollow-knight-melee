/* room.c - room collision at runtime: export the live MapCollData, load a new one at the frame boundary
 * through the game's own mpLibLoad (after freeing the previous arrays), and the jobs that need the guest. */
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
void room_boundary_jobs(Cpu *c) {
  Machine *m = g_m;
  if (!m->room_pending) return;
  m->room_pending = 0;
  for (int k = 0; k < 3; k++) call_guest(c, FN_HSD_FREE, ld32(c, ADDR_COLL_ARRAYS + 4u * k), 0);
  free_islands(c);
  if (m->room_guest) call_guest(c, FN_HSD_FREE, m->room_guest, 0);   /* our previous MapCollData (not the stage file's) */
  uint32_t d = write_room(c, &m->room);
  call_guest(c, FN_MPLIBLOAD, d, 0);
  m->room_guest = d;
}
