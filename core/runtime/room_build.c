/* room_build.c - room outlines to Melee collision (McCollision), with Melee's own classification. Pure. */
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "melee_core.h"

#define TAN60 1.7320508f
static McVec2 V[MC_MAX_VERTS]; static McLine L[MC_MAX_LINES]; static McJoint J[MC_MAX_JOINTS];
static McLine raw[MC_MAX_LINES]; static int raw_shape[MC_MAX_LINES];
static McCollision C;

static int fail(char *why, size_t n, const char *fmt, ...) { va_list ap; va_start(ap, fmt); vsnprintf(why, n, fmt, ap); va_end(ap); return -1; }

/* mpJointUpdateDynamics' rule (mplib.c:4637), in float like the game, except ceilings: Melee's stop at 30 degrees, these
 * at 60 like floors. Hollow Knight's overhangs slope more than Melee's stages do, and as walls (which do not stop a
 * fighter going up) Fox jumped up through them. */
static uint16_t classify(McVec2 a, McVec2 b) {
  float dx = b.x - a.x, dy = b.y - a.y;
  if (dx > 0.0f) return dy / dx > TAN60 ? MC_LINE_LEFT_WALL : dy / dx < -TAN60 ? MC_LINE_RIGHT_WALL : MC_LINE_FLOOR;
  if (dx < 0.0f) return dy / dx > TAN60 ? MC_LINE_RIGHT_WALL : dy / dx < -TAN60 ? MC_LINE_LEFT_WALL : MC_LINE_CEILING;
  return dy > 0.0f ? MC_LINE_LEFT_WALL : MC_LINE_RIGHT_WALL;
}
static int group_of(uint16_t kind) { return kind == MC_LINE_FLOOR ? MC_FLOOR : kind == MC_LINE_CEILING ? MC_CEILING : kind == MC_LINE_RIGHT_WALL ? MC_RIGHT_WALL : MC_LEFT_WALL; }
static float cross(McVec2 a, McVec2 b, McVec2 c) { return (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x); }

/* Hollow Knight's terrain is full of small steps and bumps, and a ledge on each would catch Fox everywhere: a floor end
 * is a ledge only above a drop of at least Fox's height. The drop is measured down the walls that follow the corner
 * (several wall segments count as one). */
#define MIN_LEDGE_DROP 18.0f    /* Melee units: about the Knight's height at the default scale */
#define MIN_LEDGE_WIDTH 6.0f    /* a floor narrower than this (a thin pillar's top) has no ledges */
/* the x-extent of the run of connected floor lines a line belongs to (a wide floor may end in a short bevel) */
static float floor_width(const McLine *raw, const McVec2 *V, int floor) {
  float lo = fminf(V[raw[floor].v0].x, V[raw[floor].v1].x), hi = fmaxf(V[raw[floor].v0].x, V[raw[floor].v1].x);
  for (int dir = -1; dir <= 1; dir += 2) {
    int k = dir < 0 ? raw[floor].prev0 : raw[floor].next0;
    for (int n = 0; k >= 0 && k != floor && n < MC_MAX_LINES && raw[k].hi_flags == MC_LINE_FLOOR; n++) {
      lo = fminf(lo, fminf(V[raw[k].v0].x, V[raw[k].v1].x)); hi = fmaxf(hi, fmaxf(V[raw[k].v0].x, V[raw[k].v1].x));
      k = dir < 0 ? raw[k].prev0 : raw[k].next0;
    }
  }
  return hi - lo;
}

static float drop(const McLine *raw, const McVec2 *V, int floor, int dir) {
  float top = dir < 0 ? V[raw[floor].v0].y : V[raw[floor].v1].y, low = top;
  int k = dir < 0 ? raw[floor].prev0 : raw[floor].next0;
  for (int n = 0; k >= 0 && k != floor && n < MC_MAX_LINES; n++) {
    uint16_t kind = raw[k].hi_flags;
    if (kind != MC_LINE_LEFT_WALL && kind != MC_LINE_RIGHT_WALL) break;
    float y = dir < 0 ? V[raw[k].v0].y : V[raw[k].v1].y;   /* the wall's far end, away from the corner */
    if (y > low) break;                                     /* the wall turns back up: the drop ends */
    low = y;
    k = dir < 0 ? raw[k].prev0 : raw[k].next0;
  }
  return top - low;
}

int mc_build_collision(const McShape *s, int ns, const McCollision **out, char *why, size_t wn) {
  if (ns < 1 || ns > MC_MAX_JOINTS) return fail(why, wn, "%d shapes: need 1 to 256", ns);
  int nv = 0, nl = 0;
  static int16_t shape_v0[MC_MAX_JOINTS], shape_nv[MC_MAX_JOINTS];
  for (int k = 0; k < ns; k++) {
    if (s[k].kind < MC_SOLID || s[k].kind > MC_HOLE) return fail(why, wn, "shape %d: unknown kind %d", k, s[k].kind);
    int closed = s[k].kind != MC_PLATFORM;
    if (s[k].material > 19) return fail(why, wn, "shape %d material %u: must be 0..19", k, s[k].material);
    /* the shape's points without repeats (and, for solids, without a closing repeat) */
    int v0 = nv, n = 0;
    for (int i = 0; i < s[k].n; i++) {
      McVec2 p = s[k].pts[i];
      if (!isfinite(p.x) || !isfinite(p.y)) return fail(why, wn, "shape %d point %d is not finite", k, i);
      if (n > 0 && p.x == V[nv - 1].x && p.y == V[nv - 1].y) continue;
      if (nv == MC_MAX_VERTS) return fail(why, wn, "more than 2048 vertices");
      V[nv++] = p; n++;
    }
    if (closed && n > 1 && V[v0].x == V[nv - 1].x && V[v0].y == V[nv - 1].y) { nv--; n--; }
    if ((closed && n < 3) || (s[k].kind == MC_PLATFORM && n < 2)) return fail(why, wn, "shape %d has too few distinct points", k);
    if (closed) {   /* solid on the right of every edge: solids clockwise, holes counter-clockwise (y-up) */
      float area = 0;
      for (int i = 0; i < n; i++) { McVec2 a = V[v0 + i], b = V[v0 + (i + 1) % n]; area += a.x * b.y - b.x * a.y; }
      if ((s[k].kind == MC_SOLID && area > 0) || (s[k].kind == MC_HOLE && area < 0)) for (int i = 0; i < n / 2; i++) { McVec2 t = V[v0 + i]; V[v0 + i] = V[v0 + n - 1 - i]; V[v0 + n - 1 - i] = t; }
    } else if (V[v0].x > V[nv - 1].x) {
      for (int i = 0; i < n / 2; i++) { McVec2 t = V[v0 + i]; V[v0 + i] = V[v0 + n - 1 - i]; V[v0 + n - 1 - i] = t; }
    }
    shape_v0[k] = (int16_t)v0; shape_nv[k] = (int16_t)n;
    int edges = closed ? n : n - 1, l0 = nl;
    if (nl + edges > MC_MAX_LINES) return fail(why, wn, "more than 1536 lines");
    for (int i = 0; i < edges; i++) {
      int a = v0 + i, b = v0 + (i + 1) % n;
      uint16_t kind = classify(V[a], V[b]);
      if (s[k].kind == MC_PLATFORM && kind != MC_LINE_FLOOR) return fail(why, wn, "shape %d: platform segment %d is not a floor", k, i);
      int16_t prev = (int16_t)(closed ? l0 + (i + edges - 1) % edges : (i ? l0 + i - 1 : -1));
      int16_t next = (int16_t)(closed ? l0 + (i + 1) % edges : (i + 1 < edges ? l0 + i + 1 : -1));
      raw[nl] = (McLine){(uint16_t)a, (uint16_t)b, prev, next, -1, -1, kind,
                         (uint16_t)(s[k].material | (s[k].kind == MC_PLATFORM ? MC_LO_PLATFORM : 0))};
      raw_shape[nl++] = k;
    }
    if (closed)   /* ledges: floor ends at a convex corner whose neighbour is not a floor */
      for (int i = l0; i < nl; i++) {
        McLine *l = &raw[i];
        if (l->hi_flags != MC_LINE_FLOOR || floor_width(raw, V, i) < MIN_LEDGE_WIDTH) continue;
        const McLine *p = &raw[l->prev0], *q = &raw[l->next0];
        int left = p->hi_flags != MC_LINE_FLOOR && cross(V[p->v0], V[l->v0], V[l->v1]) < 0 && drop(raw, V, i, -1) >= MIN_LEDGE_DROP;
        int right = q->hi_flags != MC_LINE_FLOOR && cross(V[l->v0], V[l->v1], V[q->v1]) < 0 && drop(raw, V, i, 1) >= MIN_LEDGE_DROP;
        if (left || right) l->lo_flags |= MC_LO_LEDGE;
      }
  }
  /* group by kind, shape order kept inside each group; remap links */
  static int16_t new_id[MC_MAX_LINES];
  int at = 0;
  for (int g = 0; g < MC_GROUPS; g++) {
    C.start[g] = (int16_t)at;
    for (int i = 0; i < nl; i++) if (g < MC_DYNAMIC && group_of(raw[i].hi_flags) == g) new_id[i] = (int16_t)at++;
    C.count[g] = (int16_t)(at - C.start[g]);
  }
  for (int i = 0; i < nl; i++) {
    McLine l = raw[i];
    l.prev0 = l.prev0 < 0 ? -1 : new_id[l.prev0];
    l.next0 = l.next0 < 0 ? -1 : new_id[l.next0];
    L[new_id[i]] = l;
  }
  /* one joint per shape: its lines in each group are contiguous because of the stable grouping */
  for (int k = 0; k < ns; k++) {
    McJoint *j = &J[k];
    memset(j, 0, sizeof *j);
    for (int g = 0; g < MC_GROUPS; g++) { j->start[g] = C.start[g]; j->count[g] = 0; }
    int first = 1;
    for (int i = 0; i < nl; i++) {
      if (raw_shape[i] != k) continue;
      int g = group_of(raw[i].hi_flags);
      if (j->count[g] == 0) j->start[g] = new_id[i];
      j->count[g]++;
      for (int e = 0; e < 2; e++) {
        McVec2 p = V[e ? raw[i].v1 : raw[i].v0];
        if (first) { j->left = j->right = p.x; j->bottom = j->top = p.y; first = 0; }
        j->left = fminf(j->left, p.x); j->right = fmaxf(j->right, p.x); j->bottom = fminf(j->bottom, p.y); j->top = fmaxf(j->top, p.y);
      }
    }
    j->vtx_start = shape_v0[k];
    j->vtx_count = shape_nv[k];
  }
  C.verts = V; C.n_verts = nv; C.lines = L; C.n_lines = nl; C.joints = J; C.n_joints = ns; C.x2c = 0;
  *out = &C;
  return mc_check_collision(&C, why, wn);
}
