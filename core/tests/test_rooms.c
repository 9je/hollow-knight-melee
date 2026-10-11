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
  /* a respawn (Hollow Knight's spikes, death): Fox comes back still, falling, with no momentum */
  steps(120, NULL);
  steps(40, &run);
  CHECK(fox().action == 21);
  CHECK(mc_respawn_fighter(0, -20.0f, 30.0f) == 0);
  steps(1, NULL);
  McFighter r = fox();
  CHECK(r.airborne == 1 && r.self_vel_x == 0 && r.ground_vel == 0 && r.kb_vel_x == 0 && r.hitlag == 0);
  CHECK(fabsf(r.x - (-20.0f)) < 0.01f);
  steps(60, NULL);
  r = fox();
  CHECK(r.airborne == 0 && fabsf(r.x - (-20.0f)) < 0.01f && r.action == 14);   /* landed in place: Wait */
  CHECK(mc_respawn_fighter(4, 0, 0) == -1);
  mc_shutdown();
}

/* Grounded fighters stand 0.0001 above their floor (Melee), hence the tolerances below.
 * L-shaped ground (floor y=0 from x=-100 to 60, a wall up to y=100 at x=60..80) and a platform at y=30 */
static McVec2 ell[6] = {{-100, 0}, {60, 0}, {60, 100}, {80, 100}, {80, -50}, {-100, -50}};
static McVec2 plat[2] = {{-40, 30}, {20, 30}};
static const McCollision *build(float dx) {
  static McVec2 e2[6], p2[2];
  for (int i = 0; i < 6; i++) e2[i] = (McVec2){ell[i].x + dx, ell[i].y};
  for (int i = 0; i < 2; i++) p2[i] = (McVec2){plat[i].x + dx, plat[i].y};
  McShape s[2] = {{MC_SOLID, e2, 6, 0}, {MC_PLATFORM, p2, 2, 0}};
  const McCollision *c; char why[200];
  CHECK(mc_build_collision(s, 2, &c, why, sizeof why) == 0);
  return c;
}
static void enter_room(float dx, float x, float y) {
  McBox blast = {-300 + dx, 300 + dx, 300, -200}, cam = {-250 + dx, 250 + dx, 250, -150};
  CHECK(mc_load_collision(build(dx)) == 0);
  CHECK(mc_set_bounds(&blast, &cam) == 0);
  CHECK(mc_place_fighter(0, x, y) == 0);
  steps(1, NULL);
}

static void test_room(void) {
  solo_fox();
  steps(180, NULL);
  McInput right = {0}; right.stick_x = 127;
  McInput down = {0}; down.stick_y = -127;
  McInput drift_right = {0}; drift_right.stick_x = 60;
  /* wall: running right stops at x < 60 */
  enter_room(0, 0, 0);
  CHECK(fox().airborne == 0 && fabsf(fox().y) < 0.001f);
  steps(90, &right);
  CHECK(fox().x < 60.0f && fox().x > 40.0f && fox().airborne == 0);
  /* platform: stand on it, press down: Pass, then land on the floor below */
  enter_room(0, -10, 30);
  steps(20, NULL);
  CHECK(fox().airborne == 0 && fabsf(fox().y - 30.0f) < 0.001f);
  int passed = 0;
  for (int i = 0; i < 10; i++) { steps(1, &down); passed |= fox().action == 244; }
  CHECK(passed);
  steps(60, NULL);
  CHECK(fox().airborne == 0 && fabsf(fox().y) < 0.001f);
  /* ledge: falling just left of the ledge at (-100, 0) while facing it: CliffCatch */
  enter_room(0, -112, 25);
  int caught = 0;
  for (int i = 0; i < 120 && !caught; i++) { steps(1, &drift_right); caught = fox().action == 252 || fox().action == 253; }
  CHECK(caught);
  /* placed while hanging on the ledge (CliffCatch/CliffWait keep a ledge line id): falls at the new spot */
  CHECK(mc_load_collision(build(0)) == 0);
  CHECK(mc_place_fighter(0, -10.0f, 60.0f) == 0);
  steps(1, NULL);
  CHECK(fox().action == 29 && fox().airborne == 1 && fabsf(fox().x - (-10.0f)) < 3.0f);
  steps(60, NULL);
  CHECK(fox().airborne == 0);
  /* a room loaded without placing Fox: he is placed where he stands. His old floor is line 0; in the new
   * room line 0 is a far box's floor and the floor under him is line 1, so a stale index would drop him. */
  enter_room(0, 0, 0);
  steps(5, NULL);
  {
    McVec2 far_box[4] = {{-250, -20}, {-200, -20}, {-200, 0}, {-250, 0}}, under[4] = {{-50, -20}, {50, -20}, {50, 0}, {-50, 0}};
    McShape b[2] = {{MC_SOLID, far_box, 4, 0}, {MC_SOLID, under, 4, 0}};
    const McCollision *c; char why[200];
    CHECK(mc_build_collision(b, 2, &c, why, sizeof why) == 0);
    CHECK(mc_load_collision(c) == 0);
    steps(5, NULL);
    CHECK(fox().airborne == 0 && fabsf(fox().y) < 0.001f && fox().action == 14);   /* still standing (Wait) */
  }
  /* many switches between two rooms: no leak, still standing on the right floor */
  uint32_t lines_at = 0;
  for (int i = 0; i < 200; i++) {
    enter_room(i % 2 ? 500.0f : 0.0f, (i % 2 ? 500.0f : 0.0f) - 20.0f, 0);
    if (i == 2) lines_at = mc_peek32(0x804D64BCu);
  }
  CHECK(mc_peek32(0x804D64BCu) == lines_at);
  steps(30, NULL);
  CHECK(fox().airborne == 0 && fabsf(fox().y) < 0.001f && fox().x > 470.0f);
  mc_shutdown();
}

static void test_bones(void) {
  static float b[128][12];
  CHECK(mc_init(g_mem1, g_iso) == 0);
  CHECK(mc_read_bones(0, 0, b, 128) == 0);                 /* no match yet */
  mc_shutdown();
  solo_fox();
  steps(180, NULL);
  int n = mc_read_bones(0, 0, b, 128);
  CHECK(n == 73);                                         /* Fox's skeleton (PlyFox5K_Share_joint) */
  McFighter f = fox();
  CHECK(fabsf(b[0][3] - f.x) < 0.01f && fabsf(b[0][7] - f.y) < 0.01f);   /* root joint at Fox */
  float head_y = b[40][7];
  McInput run = {0}; run.stick_x = 127;
  steps(25, &run);
  CHECK(mc_read_bones(0, 0, b, 128) == 73 && fabsf(b[0][3] - fox().x) < 0.01f && b[40][7] != head_y);
  CHECK(mc_read_bones(0, 0, b, 10) == 10);                 /* respects max */
  /* mesh parts (DObjs) in the same depth-first order: Melee hides alternates (e.g. low-detail parts) */
  static uint8_t hidden[256], joint[256];
  int nd = mc_read_dobjs(0, 0, hidden, joint, 256), nh = 0, on_root = 0;
  for (int i = 0; i < nd; i++) { nh += hidden[i]; on_root += joint[i] == 0; }
  CHECK(nd == 86 && nh > 0 && nh < nd);
  CHECK(on_root == 81 && joint[81] == 41);   /* 72 costume parts + 9 appended by the game on the root joint */
  CHECK(mc_read_dobjs(0, 0, hidden, joint, 5) == 5 && mc_read_dobjs(1, 0, hidden, joint, 256) == 0);
  CHECK(mc_read_bones(1, 0, b, 128) == 0 && mc_read_bones(5, 0, b, 128) == -1);
  mc_shutdown();
}

static uint32_t fighter_ptr(void) { return mc_peek32(mc_peek32(0x80453080u + 0xB0u) + 0x2Cu); }
static float hitlag(void) { uint32_t u = mc_peek32(fighter_ptr() + 0x195Cu); float f; memcpy(&f, &u, 4); return f; }

static void test_hitboxes(void) {
  McHitbox h[8]; McHurtbox hb[16];
  solo_fox();
  steps(180, NULL);
  CHECK(mc_read_hitboxes(0, 0, h, 8) == 0);               /* standing: nothing active */
  int nh = mc_read_hurtboxes(0, 0, hb, 16);
  McFighter f = fox();
  CHECK(nh >= 5 && nh <= 15);
  for (int i = 0; i < nh; i++)
    CHECK(hb[i].radius > 0 && fabsf(hb[i].ax - f.x) < 20 && hb[i].ay > f.y - 5 && hb[i].ay < f.y + 25);
  McInput a = {0}; a.buttons = 0x0100;                     /* PAD_BUTTON_A: jab */
  steps(1, &a);
  int n = 0;
  for (int i = 0; i < 10 && (n = mc_read_hitboxes(0, 0, h, 8)) == 0; i++) steps(1, NULL);
  CHECK(n > 0);
  f = fox();
  CHECK(h[0].damage > 0 && h[0].radius > 0 && h[0].fresh == 1 && h[0].slot < 4);
  CHECK(h[0].element == 0 && (h[0].flags & MC_HIT_FIGHTERS) && (h[0].flags & MC_HIT_GROUNDED) && (h[0].flags & MC_HIT_AIRBORNE));
  CHECK(fabsf(h[0].x - f.x) < 20 && fabsf(h[0].y - f.y) < 20 && (h[0].x - f.x) * f.facing > 0);
  uint32_t crc = mc_mem_crc32();
  mc_read_hitboxes(0, 0, h, 8); mc_read_hurtboxes(0, 0, hb, 16);
  CHECK(mc_mem_crc32() == crc);                            /* reading is invisible to the game */
  CHECK(hitlag() == 0);
  CHECK(mc_report_hit(0, 0, h[0].group + 77, h[0].damage) == 1);   /* no hitbox in that group */
  CHECK(mc_mem_crc32() == crc);
  CHECK(mc_report_hit(0, 0, h[0].group, h[0].damage) == 0);
  CHECK(mc_read_hitboxes(0, 0, h, 8) == n && h[0].fresh == 0);    /* Melee now remembers a victim */
  CHECK(mc_report_hit(0, 0, h[0].group, h[0].damage) == 0);       /* the same victim twice: still one */
  steps(1, NULL);
  CHECK(hitlag() > 0);                                     /* Melee's own hitlag for the attacker */
  CHECK(mc_report_hit(1, 0, 0, 1) == 1 && mc_report_hit(7, 0, 0, 1) == -1);
  /* a grab's hitbox is Melee's Catch element and does no damage */
  steps(60, NULL);
  McInput z = {0}; z.buttons = 0x0010;                    /* PAD_TRIGGER_Z: grab */
  steps(1, &z);
  n = 0;
  for (int i = 0; i < 15 && (n = mc_read_hitboxes(0, 0, h, 8)) == 0; i++) steps(1, NULL);
  CHECK(n > 0 && h[0].element == 8 && h[0].damage == 0);
  mc_shutdown();
}

/* Melee resolves hits mid-frame (Fighter_procCollResolve, proc 14, after hitbox positions and attack collision):
 * mc_step_to_hits pauses there so a hit reported then lands in the same frame, as a Melee victim's would. */
static void test_hit_pause(void) {
  McHitbox h[8];
  /* pausing and resuming changes nothing about the game */
  McInput idle = {0};
  solo_fox(); steps(180, NULL); steps(20, &idle);
  uint32_t plain = mc_mem_crc32(); uint64_t fr = mc_frame();
  mc_shutdown();
  solo_fox(); steps(180, NULL);
  mc_set_input(0, &idle);
  for (int i = 0; i < 20; i++) {
    uint64_t before = mc_frame();
    CHECK(mc_step_to_hits() == 0);
    CHECK(mc_frame() == before);                         /* mid-frame: the boundary has not been reached */
    CHECK(mc_step_to_hits() == 0);                       /* already there */
    CHECK(mc_step() == 0 && mc_frame() == before + 1);
  }
  CHECK(mc_frame() == fr && mc_mem_crc32() == plain);
  /* a jab's hit reported at the pause gives Fox hitlag in that same frame */
  McInput a = {0}; a.buttons = 0x0100;
  steps(1, &a);
  mc_set_input(0, &idle);
  int n = 0;
  for (int i = 0; i < 10; i++) {
    CHECK(mc_step_to_hits() == 0);
    if ((n = mc_read_hitboxes(0, 0, h, 8)) > 0) break;
    CHECK(mc_step() == 0);
  }
  CHECK(n > 0 && h[0].fresh);
  CHECK(mc_report_hit(0, 0, h[0].group, h[0].damage) == 0);
  CHECK(mc_step() == 0);
  CHECK(hitlag() == 4.0f);                                 /* floor(4/3 + 3): Melee's hitlag for a 4% jab */
  McFighter f; CHECK(mc_read_fighter(0, 0, &f) == 0 && f.hitlag == 4.0f);
  mc_shutdown();
  CHECK(mc_step_to_hits() == -1);
}

int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: test_rooms <mem1.bin> <iso>\n"); return 2; }
  g_mem1 = argv[1]; g_iso = argv[2];
  test_reload_is_invisible();
  test_bounds();
  test_place();
  test_room();
  test_bones();
  test_hitboxes();
  test_hit_pause();
  puts("rooms tests passed");
  return 0;
}
