/* room_check.c - validation of McCollision against what mpLibLoad and Melee's queries assume. Pure. */
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include "melee_core.h"

static int fail(char *why, size_t n, const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(why, n, fmt, ap);
  va_end(ap);
  return -1;
}
static const uint16_t KIND[MC_GROUPS] = {MC_LINE_FLOOR, MC_LINE_CEILING, MC_LINE_RIGHT_WALL, MC_LINE_LEFT_WALL, 0};

int mc_check_collision(const McCollision *c, char *why, size_t n) {
  if (c->n_verts < 0 || c->n_verts > MC_MAX_VERTS) return fail(why, n, "%d vertices: the limit is 2048", c->n_verts);
  if (c->n_lines < 0 || c->n_lines > MC_MAX_LINES) return fail(why, n, "%d lines: the limit is 1536", c->n_lines);
  if (c->n_joints < 1 || c->n_joints > MC_MAX_JOINTS) return fail(why, n, "%d joints: need 1 to 256", c->n_joints);
  for (int i = 0; i < c->n_verts; i++)
    if (!isfinite(c->verts[i].x) || !isfinite(c->verts[i].y)) return fail(why, n, "vertex %d is not finite", i);
  int at = 0;   /* groups must tile the line array in order */
  for (int g = 0; g < MC_GROUPS; g++) {
    if (c->count[g] < 0 || (c->count[g] > 0 && c->start[g] != at)) return fail(why, n, "line group %d does not follow group %d", g, g - 1);
    for (int k = 0; k < c->count[g]; k++) {
      const McLine *l = &c->lines[at + k];
      if (g < MC_DYNAMIC && (l->hi_flags & 0xFu) != KIND[g]) return fail(why, n, "line %d is in group %d but its kind is %u", at + k, g, l->hi_flags & 0xFu);
    }
    at += c->count[g];
  }
  if (at != c->n_lines) return fail(why, n, "line groups cover %d of %d lines", at, c->n_lines);
  for (int i = 0; i < c->n_lines; i++) {
    const McLine *l = &c->lines[i];
    if (l->v0 >= c->n_verts || l->v1 >= c->n_verts) return fail(why, n, "line %d uses a missing vertex", i);
    const int16_t link[4] = {l->prev0, l->next0, l->prev1, l->next1};
    for (int k = 0; k < 4; k++)
      if (link[k] < -1 || link[k] >= c->n_lines) return fail(why, n, "line %d has a link to missing line %d", i, link[k]);
    /* id0 links form Melee's chains (islands walk next0/prev0): mutual, touching, never self */
    if (l->next0 >= 0 && (l->next0 == i || c->lines[l->next0].prev0 != i || c->lines[l->next0].v0 != l->v1))
      return fail(why, n, "line %d next0 link to %d is not mutual or does not share vertex %u", i, l->next0, l->v1);
    if (l->prev0 >= 0 && (l->prev0 == i || c->lines[l->prev0].next0 != i || c->lines[l->prev0].v1 != l->v0))
      return fail(why, n, "line %d prev0 link to %d is not mutual or does not share vertex %u", i, l->prev0, l->v0);
    if (l->next1 == i || l->prev1 == i) return fail(why, n, "line %d has a link to itself", i);
    if ((l->lo_flags & 0xFFu) > 19) return fail(why, n, "line %d material %u: must be 0..19", i, l->lo_flags & 0xFFu);
  }
  for (int j = 0; j < c->n_joints; j++) {
    const McJoint *J = &c->joints[j];
    for (int g = 0; g < MC_GROUPS; g++)
      if (J->count[g] < 0 || (J->count[g] > 0 && (J->start[g] < c->start[g] || J->start[g] + J->count[g] > c->start[g] + c->count[g])))
        return fail(why, n, "joint %d range for group %d is outside the group", j, g);
    if (J->vtx_start < 0 || J->vtx_count < 0 || J->vtx_start + J->vtx_count > c->n_verts) return fail(why, n, "joint %d vertex range is outside the vertices", j);
    if (!(J->left <= J->right && J->bottom <= J->top)) return fail(why, n, "joint %d bounds are inverted", j);
  }
  return 0;
}
