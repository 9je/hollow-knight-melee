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
}

int main(void) {
  test_validation();
  puts("room tests passed");
  return 0;
}
