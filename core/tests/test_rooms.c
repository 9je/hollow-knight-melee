/* test_rooms: room collision, bounds and placement against the real game. usage: test_rooms <mem1.bin> <iso> */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL %s:%d: %s (%s)\n", __FILE__, __LINE__, #x, mc_error()); exit(1); } } while (0)
static const char *g_mem1, *g_iso;

/* boot, start solo Fox on FD, run past the countdown */
static void solo_fox(void) {
  uint8_t start[MC_START_SIZE];
  CHECK(mc_init(g_mem1, g_iso) == 0);
  mc_solo_fox_start(start);
  CHECK(mc_start_match(start, 1, 0) == 0);
  McInput idle = {0};
  mc_set_input(0, &idle);
  for (int p = 1; p < 4; p++) mc_set_input(p, NULL);
}
static void steps(int n, const McInput *in) { mc_set_input(0, in); for (int i = 0; i < n; i++) CHECK(mc_step() == 0); }
static McFighter fox(void) { McFighter f; CHECK(mc_read_fighter(0, 0, &f) == 0); return f; }
static int same(McFighter a, McFighter b) { return memcmp(&a, &b, sizeof a) == 0; }

/* copy of a live McCollision (mc_get_collision's storage is reused) */
static McVec2 cv[MC_MAX_VERTS]; static McLine cl[MC_MAX_LINES]; static McJoint cj[MC_MAX_JOINTS];
static McCollision copy(const McCollision *c) {
  McCollision o = *c;
  memcpy(cv, c->verts, sizeof *cv * (size_t)c->n_verts); o.verts = cv;
  memcpy(cl, c->lines, sizeof *cl * (size_t)c->n_lines); o.lines = cl;
  memcpy(cj, c->joints, sizeof *cj * (size_t)c->n_joints); o.joints = cj;
  return o;
}

static void test_reload_is_invisible(void) {
  McInput run = {0}; run.stick_x = 127;
  McInput jump = {0}; jump.buttons = 0x0800;   /* Y */
  /* A: no reload */
  solo_fox();
  steps(180, NULL);
  steps(40, &run); steps(5, &jump); steps(60, &run);
  McFighter a = fox();
  /* B: FD's own collision reloaded at the first boundary, then the same inputs */
  solo_fox();
  CHECK(mc_peek32(mc_peek32(0x8049E6C8u + 0x6B0u)) == 0x3F800000u);   /* FD world scale 1.0 */
  const McCollision *fd = mc_get_collision();
  CHECK(fd && fd->n_lines > 0 && fd->n_joints >= 1);
  char why[200];
  CHECK(mc_check_collision(fd, why, sizeof why) == 0);
  McCollision mine = copy(fd);
  uint32_t stage_data = mc_peek32(0x804D64B4u);
  CHECK(mc_load_collision(&mine) == 0);
  steps(180, NULL);
  CHECK(mc_peek32(0x804D64B4u) != stage_data && mc_get_collision()->n_lines == mine.n_lines);   /* ours now */
  steps(40, &run); steps(5, &jump); steps(60, &run);
  CHECK(same(a, fox()));
  /* no leak: 100 more reloads and the line array is allocated at the same address each time */
  uint32_t after_first = mc_peek32(0x804D64BCu);
  for (int i = 0; i < 100; i++) { CHECK(mc_load_collision(&mine) == 0); steps(1, NULL); }
  CHECK(mc_peek32(0x804D64BCu) == after_first);
  /* refused data never reaches the game */
  McCollision bad = mine; bad.n_joints = 0;
  CHECK(mc_load_collision(&bad) == -1 && strstr(mc_error(), "joint"));
  steps(1, NULL);
  mc_shutdown();
}

static void test_bounds(void) {
  solo_fox();
  steps(180, NULL);
  McBox blast, cam;
  CHECK(mc_get_bounds(&blast, &cam) == 0 && blast.left < cam.left && cam.right < blast.right);
  McBox bad = {10, -10, 50, -50};
  CHECK(mc_set_bounds(&bad, &cam) == -1 && strstr(mc_error(), "left"));
  /* a blast zone just under FD's floor: walking off the edge is a KO almost at once */
  McBox low = {-300, 300, 300, -8}, low_cam = {-200, 200, 200, -4};
  CHECK(mc_set_bounds(&low, &low_cam) == 0);
  McBox b2, c2;
  CHECK(mc_get_bounds(&b2, &c2) == 0 && b2.bottom == -8.0f && c2.bottom == -4.0f);
  McInput run = {0}; run.stick_x = 127;
  int dead = 0;
  for (int i = 0; i < 240 && !dead; i++) { steps(1, &run); dead = fox().action <= 10; }
  CHECK(dead);   /* DeadDown..: KO'd by the new bottom blast zone */
  mc_shutdown();
}

static void test_place(void) {
  solo_fox();
  steps(180, NULL);
  CHECK(mc_place_fighter(4, 0, 0) == -1);
  McInput run = {0}; run.stick_x = 127;
  steps(40, &run);
  McFighter before = fox();
  CHECK(before.action == 21 && before.airborne == 0);   /* Run */
  CHECK(mc_place_fighter(0, -40.0f, 0.0f) == 0);
  steps(1, &run);
  McFighter after = fox();
  CHECK(after.action == 21 && after.airborne == 0 && fabsf(after.x - (-40.0f + before.ground_vel)) < 3.0f);
  /* airborne: keeps action and velocity */
  McInput jump = {0}; jump.buttons = 0x0800;
  steps(8, &jump);
  McFighter air = fox();
  CHECK(air.airborne == 1);
  CHECK(mc_place_fighter(0, 0.0f, 60.0f) == 0);
  steps(1, NULL);
  McFighter air2 = fox();
  CHECK(air2.airborne == 1 && air2.action == air.action && fabsf(air2.y - 60.0f) < 5.0f);
  /* grounded fighter placed over nothing: airborne, then falls */
  steps(120, NULL);
  CHECK(fox().airborne == 0);
  CHECK(mc_place_fighter(0, 0.0f, 120.0f) == 0);
  steps(1, NULL);
  CHECK(fox().airborne == 1);
  steps(30, NULL);
  CHECK(fox().y < 120.0f);
  mc_shutdown();
}

int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: test_rooms <mem1.bin> <iso>\n"); return 2; }
  g_mem1 = argv[1]; g_iso = argv[2];
  test_reload_is_invisible();
  test_bounds();
  test_place();
  puts("rooms tests passed");
  return 0;
}
