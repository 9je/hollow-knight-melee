/* test_room: room data validation and building, without the game. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); exit(1); } } while (0)

/* one floor line on one joint: the smallest valid collision */
static McVec2 V[2] = {{-10, 0}, {10, 0}};
static McLine L[1] = {{0, 1, -1, -1, -1, -1, MC_LINE_FLOOR, 0}};
static McJoint J[1] = {{{0, 1, 1, 1, 1}, {1, 0, 0, 0, 0}, -10, 0, 10, 0, 0, 2}};
static McCollision ok(void) {
  McCollision c = {V, 2, L, 1, {0, 1, 1, 1, 1}, {1, 0, 0, 0, 0}, J, 1, 0};
  return c;
}
static int refused(McCollision c, const char *word) {
  char why[200];
  return mc_check_collision(&c, why, sizeof why) == -1 && strstr(why, word) != NULL;
}

static void test_validation(void) {
  char why[200];
  McCollision c = ok();
  CHECK(mc_check_collision(&c, why, sizeof why) == 0);
  c = ok(); c.n_joints = 0; CHECK(refused(c, "joint"));
  c = ok(); c.n_lines = MC_MAX_LINES + 1; CHECK(refused(c, "1536"));
  c = ok(); c.n_verts = MC_MAX_VERTS + 1; CHECK(refused(c, "2048"));
  static McLine bad_v[1] = {{0, 7, -1, -1, -1, -1, MC_LINE_FLOOR, 0}};
  c = ok(); c.lines = bad_v; CHECK(refused(c, "vertex"));
  static McLine bad_link[1] = {{0, 1, -1, 5, -1, -1, MC_LINE_FLOOR, 0}};
  c = ok(); c.lines = bad_link; CHECK(refused(c, "link"));
  static McLine bad_mat[1] = {{0, 1, -1, -1, -1, -1, MC_LINE_FLOOR, 20}};
  c = ok(); c.lines = bad_mat; CHECK(refused(c, "material"));
  static McVec2 nan_v[2] = {{-10, 0}, {NAN, 0}};
  c = ok(); c.verts = nan_v; CHECK(refused(c, "finite"));
  c = ok(); c.count[MC_FLOOR] = 0; c.count[MC_CEILING] = 1; c.start[MC_CEILING] = 0;   /* line kind mismatches its group */
  CHECK(refused(c, "group"));
  c = ok(); c.start[MC_CEILING] = 0; c.count[MC_CEILING] = 1;                           /* line in two groups */
  CHECK(refused(c, "group"));
  static McJoint bad_j[1] = {{{0, 1, 1, 1, 1}, {2, 0, 0, 0, 0}, -10, 0, 10, 0, 0, 2}};
  c = ok(); c.joints = bad_j; CHECK(refused(c, "joint"));
  /* links must be mutual and share the vertex: a floor chain that loops back hangs Melee's island builder */
  static McVec2 v3[4] = {{0, 0}, {10, 0}, {20, 0}, {30, 0}};
  static McLine loop[3] = {{0, 1, -1, 1, -1, -1, MC_LINE_FLOOR, 0}, {1, 2, 0, 2, -1, -1, MC_LINE_FLOOR, 0}, {2, 3, 1, 1, -1, -1, MC_LINE_FLOOR, 0}};
  static McJoint j3[1] = {{{0, 3, 3, 3, 3}, {3, 0, 0, 0, 0}, 0, 0, 30, 0, 0, 4}};
  McCollision l3 = {v3, 4, loop, 3, {0, 3, 3, 3, 3}, {3, 0, 0, 0, 0}, j3, 1, 0};
  CHECK(refused(l3, "link"));
  loop[2].next0 = -1;
  CHECK(mc_check_collision(&l3, why, sizeof why) == 0);
  static McLine apart[2] = {{0, 1, -1, 1, -1, -1, MC_LINE_FLOOR, 0}, {2, 3, 0, -1, -1, -1, MC_LINE_FLOOR, 0}};   /* linked but not touching */
  McCollision a2 = {v3, 4, apart, 2, {0, 2, 2, 2, 2}, {2, 0, 0, 0, 0}, j3, 1, 0};
  static McJoint j2[1] = {{{0, 2, 2, 2, 2}, {2, 0, 0, 0, 0}, 0, 0, 30, 0, 0, 4}};
  a2.joints = j2;
  CHECK(refused(a2, "link"));
}

static const McLine *line_of_kind(const McCollision *c, uint16_t kind, int nth) {
  for (int i = 0; i < c->n_lines; i++) if ((c->lines[i].hi_flags & 0xF) == kind && nth-- == 0) return &c->lines[i];
  return NULL;
}

static void test_builder(void) {
  const McCollision *c; char why[200];
  /* a box, counter-clockwise on purpose: one floor, one ceiling, two walls; ledges at both floor ends */
  McVec2 box_ccw[4] = {{-50, -20}, {50, -20}, {50, 0}, {-50, 0}};
  McShape box = {MC_SOLID, box_ccw, 4, 0};
  CHECK(mc_build_collision(&box, 1, &c, why, sizeof why) == 0);
  CHECK(mc_check_collision(c, why, sizeof why) == 0);
  CHECK(c->count[MC_FLOOR] == 1 && c->count[MC_CEILING] == 1 && c->count[MC_RIGHT_WALL] == 1 && c->count[MC_LEFT_WALL] == 1);
  const McLine *f = line_of_kind(c, MC_LINE_FLOOR, 0);
  CHECK(c->verts[f->v0].x == -50 && c->verts[f->v1].x == 50 && c->verts[f->v0].y == 0);   /* left to right, on top */
  CHECK(f->lo_flags & MC_LO_LEDGE);
  CHECK((c->lines[f->next0].hi_flags & 0xF) == MC_LINE_RIGHT_WALL && (c->lines[f->prev0].hi_flags & 0xF) == MC_LINE_LEFT_WALL);
  CHECK(c->n_joints == 1 && c->joints[0].left == -50 && c->joints[0].top == 0);
  /* slopes: 59 degrees is a floor, 61 is a wall (Melee: tan 60) */
  float t59 = tanf(59.0f * 3.14159265f / 180), t61 = tanf(61.0f * 3.14159265f / 180);
  McVec2 ramp[3] = {{0, 0}, {10, 10 * t59}, {10, -10}};
  McShape r = {MC_SOLID, ramp, 3, 0};
  CHECK(mc_build_collision(&r, 1, &c, why, sizeof why) == 0 && c->count[MC_FLOOR] == 1);
  McVec2 steep[3] = {{0, 0}, {10, 10 * t61}, {10, -10}};
  McShape s = {MC_SOLID, steep, 3, 0};
  CHECK(mc_build_collision(&s, 1, &c, why, sizeof why) == 0 && c->count[MC_FLOOR] == 0);
  /* undersides: up to 60 degrees is a ceiling, as floors (Melee's rule stops at 30; Hollow Knight's overhangs slope more,
   * and as walls Fox jumped up through them) */
  float t45 = 1.0f;
  McVec2 hang[3] = {{0, 20}, {10, 20 - 10 * t45}, {20, 20}};
  McShape h = {MC_SOLID, hang, 3, 0};
  CHECK(mc_build_collision(&h, 1, &c, why, sizeof why) == 0 && c->count[MC_CEILING] == 2 && c->count[MC_FLOOR] == 1);
  McVec2 spike[3] = {{0, 20}, {10, 20 - 10 * t61}, {20, 20}};
  McShape sp = {MC_SOLID, spike, 3, 0};
  CHECK(mc_build_collision(&sp, 1, &c, why, sizeof why) == 0 && c->count[MC_CEILING] == 0);
  /* L-shape: the inner corner (floor meets an upward wall) is concave: no ledge there */
  McVec2 ell[6] = {{-100, 0}, {60, 0}, {60, 100}, {80, 100}, {80, -50}, {-100, -50}};
  McShape l = {MC_SOLID, ell, 6, 0};
  CHECK(mc_build_collision(&l, 1, &c, why, sizeof why) == 0);
  const McLine *ground = NULL;
  for (int i = 0; i < c->n_lines; i++) if ((c->lines[i].hi_flags & 0xF) == MC_LINE_FLOOR && c->verts[c->lines[i].v0].y == 0) ground = &c->lines[i];
  CHECK(ground && (ground->lo_flags & MC_LO_LEDGE));   /* the left end (-100, 0) is a ledge ... */
  CHECK((c->lines[ground->next0].hi_flags & 0xF) == MC_LINE_LEFT_WALL);   /* ... the right end meets the wall going up */
  /* a small bump (4 units) on the ground: no ledges on it (Hollow Knight's terrain is full of these); the ground's
   * far ends, 50 units above the bottom, keep theirs */
  McVec2 bump[8] = {{-100, 0}, {-10, 0}, {-10, 4}, {10, 4}, {10, 0}, {100, 0}, {100, -50}, {-100, -50}};
  McShape bs = {MC_SOLID, bump, 8, 0};
  CHECK(mc_build_collision(&bs, 1, &c, why, sizeof why) == 0);
  int top = 0, ends = 0;
  for (int i = 0; i < c->n_lines; i++) {
    const McLine *li = &c->lines[i];
    if ((li->hi_flags & 0xF) != MC_LINE_FLOOR) continue;
    if (c->verts[li->v0].y == 4) { top++; CHECK(!(li->lo_flags & MC_LO_LEDGE)); }
    else { ends++; CHECK(li->lo_flags & MC_LO_LEDGE); }
  }
  CHECK(top == 1 && ends == 2);
  /* a drop made of several wall segments counts as one drop */
  McVec2 cliff[6] = {{-50, 0}, {50, 0}, {50, -9}, {52, -19}, {52, -40}, {-50, -40}};
  McShape cs = {MC_SOLID, cliff, 6, 0};
  CHECK(mc_build_collision(&cs, 1, &c, why, sizeof why) == 0);
  f = line_of_kind(c, MC_LINE_FLOOR, 0);
  CHECK(f && (f->lo_flags & MC_LO_LEDGE));
  /* a thin pillar's top (3 wide) is no ledge, however tall */
  McVec2 pillar[8] = {{-100, 0}, {0, 0}, {0, 40}, {3, 40}, {3, 0}, {100, 0}, {100, -50}, {-100, -50}};
  McShape ps = {MC_SOLID, pillar, 8, 0};
  CHECK(mc_build_collision(&ps, 1, &c, why, sizeof why) == 0);
  for (int i = 0; i < c->n_lines; i++)
    if ((c->lines[i].hi_flags & 0xF) == MC_LINE_FLOOR && c->verts[c->lines[i].v0].y == 40) CHECK(!(c->lines[i].lo_flags & MC_LO_LEDGE));
  /* a wide floor ending in a short bevel segment (Hollow Knight's colliders do this) still has its ledge: the width
   * is the floor's, not its last segment's */
  McVec2 bevel[6] = {{-50, 0}, {47, 0}, {50, -0.5f}, {50, -40}, {-50, -40}, {-50, -20}};
  McShape bv = {MC_SOLID, bevel, 6, 0};
  CHECK(mc_build_collision(&bv, 1, &c, why, sizeof why) == 0);
  int bevel_ledge = 0;
  for (int i = 0; i < c->n_lines; i++)
    if ((c->lines[i].hi_flags & 0xF) == MC_LINE_FLOOR && c->verts[c->lines[i].v1].x == 50) bevel_ledge = c->lines[i].lo_flags & MC_LO_LEDGE;
  CHECK(bevel_ledge);
  /* platform drawn right to left, plus a repeated point */
  McVec2 plat[3] = {{20, 30}, {20, 30}, {-40, 30}};
  McShape p = {MC_PLATFORM, plat, 3, 7};
  CHECK(mc_build_collision(&p, 1, &c, why, sizeof why) == 0 && c->n_lines == 1);
  CHECK(c->lines[0].lo_flags == (MC_LO_PLATFORM | 7) && c->verts[c->lines[0].v0].x == -40 && c->lines[0].next0 == -1);
  /* a "platform" that is a wall is refused */
  McVec2 vert[2] = {{0, 0}, {0, 50}};
  McShape pv = {MC_PLATFORM, vert, 2, 0};
  CHECK(mc_build_collision(&pv, 1, &c, why, sizeof why) == -1 && strstr(why, "platform"));
  /* a hole: the empty inside of a room walled in on all sides. Floor at the bottom, ceiling at the top,
   * walls facing inwards, no ledges (every corner is concave for the solid) */
  McVec2 cave_cw[4] = {{-50, 0}, {-50, 100}, {50, 100}, {50, 0}};
  McShape cave = {MC_HOLE, cave_cw, 4, 0};
  CHECK(mc_build_collision(&cave, 1, &c, why, sizeof why) == 0);
  CHECK(c->count[MC_FLOOR] == 1 && c->count[MC_CEILING] == 1 && c->count[MC_RIGHT_WALL] == 1 && c->count[MC_LEFT_WALL] == 1);
  const McLine *hf = line_of_kind(c, MC_LINE_FLOOR, 0);
  CHECK(c->verts[hf->v0].y == 0 && c->verts[hf->v0].x == -50 && !(hf->lo_flags & MC_LO_LEDGE));
  const McLine *hc = line_of_kind(c, MC_LINE_CEILING, 0);
  CHECK(c->verts[hc->v0].y == 100);
  /* a solid and its hole together */
  McVec2 outer[4] = {{-80, -20}, {80, -20}, {80, 120}, {-80, 120}};
  McShape both[2] = {{MC_SOLID, outer, 4, 0}, {MC_HOLE, cave_cw, 4, 0}};
  CHECK(mc_build_collision(both, 2, &c, why, sizeof why) == 0 && c->count[MC_FLOOR] == 2 && c->n_joints == 2);
  /* limits */
  static McShape many[257];
  for (int i = 0; i < 257; i++) many[i] = box;
  CHECK(mc_build_collision(many, 257, &c, why, sizeof why) == -1 && strstr(why, "256"));
  static McVec2 big[1600];
  for (int i = 0; i < 1600; i++) { float a = 6.2831853f * (float)i / 1600; big[i] = (McVec2){100 * cosf(a), 100 * sinf(a)}; }
  McShape circle = {MC_SOLID, big, 1600, 0};
  CHECK(mc_build_collision(&circle, 1, &c, why, sizeof why) == -1 && strstr(why, "1536"));
  McShape bad_mat = {MC_SOLID, box_ccw, 4, 20};
  CHECK(mc_build_collision(&bad_mat, 1, &c, why, sizeof why) == -1 && strstr(why, "material"));
}

int main(void) {
  test_validation();
  test_builder();
  puts("room tests passed");
  return 0;
}
