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

/* In a room Melee's camera is never what the player sees: Fox is drawn the frame he is placed, however far its
 * camera is behind (in Melee an off-screen fighter is not drawn, only shown in the magnifier) */
static void test_room_draws_fox_offscreen(void) {
  solo_fox();
  steps(180, NULL);
  CHECK(mc_gx_capture(1) == 0);
  enter_room(3000.0f, 3000.0f, 0);
  const McGxVert *v; int nv; const McGxRun *r; int nr;
  CHECK(mc_gx_decode(1ull << 8, &v, &nv, &r, &nr) == 0);
  CHECK(nv > 3000);
  float x0 = 1e9f, x1 = -1e9f;
  for (int i = 0; i < nv; i++) { x0 = fminf(x0, v[i].x); x1 = fmaxf(x1, v[i].x); }
  CHECK(x0 > 2985.0f && x1 < 3015.0f);
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

/* the shield bubble: Melee scales Fox's shield joint by his shield size (ftCo_80091D58) */
static void test_shield(void) {
  McShield sh;
  solo_fox(); steps(180, NULL);
  CHECK(mc_read_shield(0, 0, &sh) == 0 && sh.active == 0);
  McInput r = {0}; r.buttons = 0x0020; r.trigger_r = 255;   /* R held: shield */
  steps(5, &r);
  CHECK(mc_read_shield(0, 0, &sh) == 0 && sh.active == 1);
  McFighter f = fox();
  CHECK(fabsf(sh.x - f.x) < 6 && sh.y > f.y && sh.y < f.y + 20);
  CHECK(sh.radius > 6 && sh.radius < 20 && sh.hard > 0.99f);   /* a full press: hard shield */
  float full = sh.radius;
  steps(120, &r);                                          /* shield health drains while held */
  CHECK(mc_read_shield(0, 0, &sh) == 0 && sh.active == 1 && sh.radius < full - 0.5f);
  McInput lite = {0}; lite.trigger_r = 60;                  /* light shield: analog only */
  steps(30, NULL); steps(10, &lite);
  CHECK(mc_read_shield(0, 0, &sh) == 0 && sh.active == 1 && sh.hard < 0.9f);
  steps(30, NULL);
  CHECK(mc_read_shield(0, 0, &sh) == 0 && sh.active == 0);
  CHECK(mc_read_shield(1, 0, &sh) == 1 && mc_read_shield(7, 0, &sh) == -1);
  mc_shutdown();
}

/* items (Fox's lasers), read-only from Melee's item list */
static void test_items(void) {
  McItem it[16];
  solo_fox(); steps(180, NULL);
  CHECK(mc_read_items(it, 16) == 0);
  McInput b = {0}; b.buttons = 0x0200;                     /* neutral B: blaster */
  steps(1, &b);
  int n = 0, laser = -1, gun = 0;
  for (int i = 0; i < 40 && laser < 0; i++) {
    steps(1, NULL);
    n = mc_read_items(it, 16);
    for (int k = 0; k < n; k++) { if (fabsf(it[k].vx) > 1) laser = k; else gun = 1; }
  }
  CHECK(gun && laser >= 0);                                /* the blaster (held, still) and a laser */
  McFighter f = fox();
  CHECK(fabsf(it[laser].x - f.x) < 60 && it[laser].vx * f.facing > 1 && fabsf(it[laser].vy) < 0.01f);
  uint32_t kind = it[laser].kind; float x0 = it[laser].x;
  steps(1, NULL);
  n = mc_read_items(it, 16);
  int moved = 0;
  for (int k = 0; k < n; k++) moved |= it[k].kind == kind && (it[k].x - x0) * f.facing > 1;
  CHECK(moved);                                            /* it flies */
  CHECK(mc_read_items(it, 0) == 0);
  mc_shutdown();
  CHECK(mc_read_items(it, 16) == -1);
}

/* GX capture: each frame's graphics command stream (writes to the GX FIFO at 0xCC008000), with marks saying which
 * Melee object drew which part. Capturing changes nothing in the game. */
static void test_gx_capture(void) {
  const uint8_t *fifo; uint32_t len; const McGxMark *mk; int nmk;
  McInput idle = {0};
  solo_fox(); steps(180, NULL); steps(20, &idle);
  uint32_t plain = mc_mem_crc32();
  mc_shutdown();
  solo_fox(); steps(180, NULL);
  CHECK(mc_gx_capture(1) == 0);
  steps(20, &idle);
  CHECK(mc_mem_crc32() == plain);                          /* capture is invisible to the game */
  CHECK(mc_gx_frame(&fifo, &len, &mk, &nmk) == 0);
  CHECK(len > 1000 && nmk > 4);
  int fighter = 0, ok_ops = 0;
  for (int i = 0; i < nmk; i++) {
    CHECK(mk[i].offset <= len && (i == 0 || mk[i].offset >= mk[i - 1].offset));
    if (mk[i].gobj && mk[i].p_link == 8) fighter++;        /* HSD_GOBJ_PLINK_FIGHTER */
  }
  CHECK(fighter >= 1);
  for (int i = 0; i < nmk; i++) if (mk[i].gobj && mk[i].offset < len) {   /* a drawing object's stream starts with a GX command */
    uint8_t op = fifo[mk[i].offset];
    ok_ops += op == 0x00 || op == 0x08 || op == 0x10 || (op >= 0x20 && op <= 0x38) || op == 0x40 || op == 0x48 || op == 0x61 || op >= 0x80;
  }
  CHECK(ok_ops >= 1);
  uint32_t len1 = len;
  steps(1, &idle);                                         /* the next frame replaces it */
  CHECK(mc_gx_frame(&fifo, &len, &mk, &nmk) == 0 && len > 1000 && len < 4 * len1);
  CHECK(mc_gx_capture(0) == 0);
  steps(1, &idle);
  CHECK(mc_gx_frame(&fifo, &len, &mk, &nmk) == 0 && len == 0);
  mc_shutdown();
}

/* the captured frame decoded: Fox's body where Fox is, in a few runs on the atlas; his shield bubble as a translucent
 * effect at the shield; his laser as an item. Decoding changes nothing in the game. */
static void test_gx_decode(void) {
  const McGxVert *v; int nv; const McGxRun *r; int nr;
  const uint64_t FIGHTER = 1ull << 8, ITEM = 1ull << 9, EFFECTS = (1ull << 11) | (1ull << 12);
  McInput idle = {0};
  solo_fox(); steps(180, NULL);
  CHECK(mc_gx_capture(1) == 0);
  steps(3, &idle);
  uint32_t crc = mc_mem_crc32();
  CHECK(mc_gx_decode(FIGHTER, &v, &nv, &r, &nr) == 0);
  CHECK(mc_mem_crc32() == crc);
  McFighter f = fox();
  CHECK(nv > 9000 && nv % 3 == 0 && nr >= 1 && nr < 40);
  float x0 = 1e9f, x1 = -1e9f, y0 = 1e9f, y1 = -1e9f;
  for (int i = 0; i < nv; i++) { x0 = fminf(x0, v[i].x); x1 = fmaxf(x1, v[i].x); y0 = fminf(y0, v[i].y); y1 = fmaxf(y1, v[i].y); }
  CHECK(x0 > f.x - 15 && x1 < f.x + 15 && y0 > f.y - 2 && y1 < f.y + 20 && y1 > f.y + 10);   /* Fox, standing */
  int atlas = 0;
  for (int i = 0; i < nr; i++) atlas += r[i].tex == MC_GX_ATLAS && r[i].p_link == 8;
  McGxTexture t;
  CHECK(atlas >= 1 && mc_gx_texture(MC_GX_ATLAS, &t) == 0 && t.w == 4096 && t.gen > 0);
  /* the atlas reports what changed since it was last asked: the host uploads only that */
  uint32_t ax, ay, aw, ah;
  CHECK(mc_gx_atlas_dirty(&ax, &ay, &aw, &ah) == 1 && aw > 0 && ah > 0 && ax + aw <= 4096 && ay + ah <= 4096);
  CHECK(mc_gx_atlas_dirty(&ax, &ay, &aw, &ah) == 0);
  CHECK(r[nr - 1].first + r[nr - 1].count == (uint32_t)nv);
  /* shield: a translucent effect around the shield's centre */
  McInput sh = {0}; sh.buttons = 0x0020; sh.trigger_r = 255;
  steps(6, &sh);
  McShield s; CHECK(mc_read_shield(0, 0, &s) == 0 && s.active);
  CHECK(mc_gx_decode(EFFECTS, &v, &nv, &r, &nr) == 0);
  int bubble = 0;
  for (int i = 0; i < nr; i++) {
    if (r[i].blend != MC_GX_ALPHA || r[i].count < 6) continue;
    float cx = 0, cy = 0;
    for (uint32_t k = r[i].first; k < r[i].first + r[i].count; k++) { cx += v[k].x; cy += v[k].y; }
    cx /= (float)r[i].count; cy /= (float)r[i].count;
    if (fabsf(cx - s.x) < 3 && fabsf(cy - s.y) < 3) bubble = 1;
    CHECK(r[i].tex < 0 || mc_gx_texture(r[i].tex, &t) == 0);
  }
  CHECK(bubble);
  /* the shield is drawn with z compare ALWAYS: Melee puts it over Fox, so it comes after all of Fox's triangles */
  CHECK(mc_gx_decode(FIGHTER | EFFECTS, &v, &nv, &r, &nr) == 0);
  int last_fox = -1, bubble_at = -1;
  for (int i = 0; i < nr; i++) if (r[i].p_link == 8) last_fox = i; else if (r[i].blend == MC_GX_ALPHA) bubble_at = i;
  CHECK(last_fox >= 0 && bubble_at > last_fox);
  /* laser: an item */
  steps(30, &idle);
  McInput b = {0}; b.buttons = 0x0200;
  steps(1, &b); steps(15, &idle);
  /* lasers and their effects stay whole draws: Fox's body is not cut into pieces between them (a few runs, not
   * hundreds: each run is a draw call in Hollow Knight) */
  for (int k = 0; k < 6; k++) { steps(2, &b); steps(4, &idle); }
  CHECK(mc_gx_decode(FIGHTER | ITEM | EFFECTS, &v, &nv, &r, &nr) == 0);
  int fox_pieces = 0;
  for (int i = 0; i < nr; i++) fox_pieces += r[i].p_link == 8 && (i == 0 || r[i - 1].p_link != 8);
  CHECK(fox_pieces <= 2 && nr < 80);
  CHECK(mc_gx_decode(ITEM, &v, &nv, &r, &nr) == 0 && nv > 0);
  for (int i = 0; i < nr; i++) CHECK(r[i].p_link == 9);
  CHECK(mc_gx_decode(EFFECTS, &v, &nv, &r, &nr) == 0);
  for (int i = 0; i < nv; i++) CHECK(fabsf(v[i].z) < 500);   /* not the stage's background effects */
  CHECK(mc_gx_decode(0, &v, &nv, &r, &nr) == 0 && nv == 0 && nr == 0);
  CHECK(mc_gx_decode(~0ull, &v, &nv, &r, &nr) == 0 && nv > 0);   /* everything (stage, HUD): every draw fully parsed */
  CHECK(mc_gx_size_mismatches() == 0);
  mc_shutdown();
  CHECK(mc_gx_decode(FIGHTER, &v, &nv, &r, &nr) == -1);
}

static void test_sfx(void) {
  McSfx ev[64];
  CHECK(mc_read_sfx(ev, 64) == -1);                         /* no machine */
  solo_fox(); steps(180, NULL);
  while (mc_read_sfx(ev, 64) > 0) {}                       /* the countdown's sounds */
  McInput b = {0}; b.buttons = 0x0200;                     /* neutral B: blaster */
  steps(1, &b);
  int laser = -1, n = 0;
  for (int i = 0; i < 30 && laser < 0; i++) {
    steps(1, NULL);
    n = mc_read_sfx(ev, 64);
    for (int k = 0; k < n; k++) if (ev[k].kind == MC_SFX_START && ev[k].id == 110103) laser = k;
  }
  CHECK(laser >= 0);                                       /* Fox's laser, facing right */
  McSfx l = ev[laser];
  CHECK(l.vol > 0 && l.channel == 7 && l.pan == 0x80 && l.frame > 0 && l.frame <= mc_frame());
  CHECK(l.handle >= 0 && (l.handle & 0x7F) < 0x60);         /* a predicted voice handle: the one Melee gave it */
  uint32_t voice = 0x804C45A0u + 0x34u * (uint32_t)(l.handle & 0x7F);
  CHECK(mc_peek32(voice + 0x0C) == (uint32_t)l.handle && (mc_peek32(voice + 0x14) & 0xFFFF) == (110103u & 0xFFFF));
  for (int k = 0; k < n; k++)                              /* live handles are distinct */
    for (int j = k + 1; j < n; j++)
      if (ev[k].kind == MC_SFX_START && ev[j].kind == MC_SFX_START && ev[k].handle >= 0) CHECK(ev[k].handle != ev[j].handle);
  CHECK(mc_read_sfx(ev, 64) == 0);                         /* reading drains */
  /* more than fit: the oldest are kept in order, the rest wait for the next read */
  McInput none = {0};
  for (int i = 0; i < 6; i++) { steps(1, &b); steps(20, &none); }
  int n1 = mc_read_sfx(ev, 1), n2 = mc_read_sfx(ev + 1, 63);
  CHECK(n1 == 1 && n2 >= 1 && ev[0].frame <= ev[1].frame);
  CHECK(mc_read_sfx(ev, 0) == 0);
  mc_shutdown();
  CHECK(mc_read_sfx(ev, 64) == -1);
}

/* Fox's lasers: their hitboxes are read with his (an item's group is the item); a reported hit ends the laser the way
 * Melee ends one that hits a wall (life timer to 1), and gives Fox no hitlag. mc_hitlag_frames: Melee's formula. */
static void test_laser_hits(void) {
  McHitbox h[16]; McItem it[16];
  CHECK(mc_hitlag_frames(4) == -1);                        /* no machine */
  solo_fox(); steps(180, NULL);
  CHECK(mc_hitlag_frames(4) == 4 && mc_hitlag_frames(3) == 4 && mc_hitlag_frames(0) == 3 && mc_hitlag_frames(30) == 13);
  CHECK(mc_hitlag_frames(200) == 20);                      /* Melee's cap */
  McInput b = {0}; b.buttons = 0x0200;
  steps(1, &b);
  int laser = -1, n = 0;
  for (int i = 0; i < 40 && laser < 0; i++) {
    steps(1, NULL);
    n = mc_read_hitboxes(0, 0, h, 16);
    for (int k = 0; k < n; k++) if (h[k].group >= 0x80000000u) laser = k;
  }
  CHECK(laser >= 0 && h[laser].damage > 0 && h[laser].fresh && h[laser].slot >= 4);
  int ni = mc_read_items(it, 16), seen = 0;
  for (int k = 0; k < ni; k++) seen |= it[k].kind == 54 && fabsf(it[k].x - h[laser].x) < 15;
  CHECK(seen);                                             /* the hitbox is on the laser */
  CHECK(mc_report_hit(0, 0, h[laser].group, h[laser].damage) == 0);
  n = mc_read_hitboxes(0, 0, h, 16);
  int fresh = 1;
  for (int k = 0; k < n; k++) if (h[k].group >= 0x80000000u) fresh = h[k].fresh;
  CHECK(!fresh);
  steps(1, NULL);
  CHECK(hitlag() == 0);                                    /* lasers give Fox no hitlag */
  steps(2, NULL);
  ni = mc_read_items(it, 16);
  for (int k = 0; k < ni; k++) CHECK(it[k].kind != 54);   /* the laser is gone */
  CHECK(mc_report_hit(0, 0, 0x80000000u, 3) == 1);        /* not an item of Fox's */
  mc_shutdown();
}

/* Enemies hitting Fox (spec 5.2): at the hit pause, a hit from outside Melee goes through Melee's own environmental
 * hit path (ftCo_800C08A0 + ftColl_8007AB48), so Melee resolves damage, knockback, hitlag, DI and teching; shields and
 * intangibility are checked as Melee's collision does. */
/* A respawn in the middle of knockback (Hollow Knight's spikes or death took him): Fox comes back able to act, as a
 * fresh fighter would, not stuck in the hit's hitstun */
static void test_respawn_after_hit(void) {
  McInput idle = {0}, right = {0}; right.stick_x = 127;
  solo_fox(); steps(180, NULL); steps(10, &idle);
  McHit hit = {0};
  hit.damage = 60; hit.angle = 45; hit.growth = 100; hit.base_kb = 60;
  McFighter f = fox();
  hit.from_x = f.x - 10; hit.from_y = f.y;
  CHECK(mc_step_to_hits() == 0);
  CHECK(mc_apply_hit(0, &hit) == MC_HIT_LANDED);
  CHECK(mc_step() == 0);
  steps(15, &idle);                                        /* flying */
  CHECK(mc_respawn_fighter(0, 0.0f, 20.0f) == 0);
  steps(40, &idle);                                        /* falls and lands on Final Destination */
  CHECK(fox().airborne == 0);
  float x0 = fox().x;
  steps(30, &right);
  CHECK(fox().x > x0 + 20.0f);                             /* runs */
  mc_shutdown();
}

/* Hollow Knight's opening drop: Fox enters tumbling (DamageFall) and lands in a knockdown unless he techs */
static void test_tumble_fighter(void) {
  solo_fox(); steps(180, NULL);
  CHECK(mc_tumble_fighter(4, 0, 0) == -1);
  CHECK(mc_tumble_fighter(0, 0.0f, 60.0f) == 0);
  steps(1, NULL);
  CHECK(fox().action == 38 && fox().airborne == 1);   /* DamageFall */
  int down = 0;
  for (int i = 0; i < 90 && !down; i++) { steps(1, NULL); down = fox().action >= 183 && fox().action <= 198; }
  CHECK(down);                                        /* DownBound..: landed hard */
  int wait = 0;
  for (int i = 0; i < 90 && !wait; i++) { steps(1, NULL); wait = fox().action == 184 || fox().action == 192; }
  CHECK(wait);                                        /* DownWaitU / DownWaitD: lying still */
  McInput up = {0}; up.stick_y = 127;
  steps(1, &up);
  CHECK(fox().action == 186 || fox().action == 194); /* DownStandU / DownStandD: getting up */
  int stood = 0;
  for (int i = 0; i < 90 && !stood; i++) { steps(1, NULL); stood = fox().action == 14; }
  CHECK(stood);
  mc_shutdown();
}

static void test_apply_hit(void) {
  McHit hit = {0};
  hit.damage = 10; hit.angle = 45; hit.growth = 100; hit.base_kb = 20; hit.element = 0;
  McInput idle = {0};
  CHECK(mc_apply_hit(0, &hit) == -1);                      /* no machine */
  solo_fox(); steps(180, NULL); steps(10, &idle);
  CHECK(mc_apply_hit(0, &hit) == -1);                      /* only at the hit pause */
  McFighter f = fox();
  hit.from_x = f.x - 10; hit.from_y = f.y + 5;            /* the enemy is to Fox's left */
  CHECK(mc_step_to_hits() == 0);
  CHECK(mc_apply_hit(0, &hit) == MC_HIT_LANDED);
  CHECK(mc_step() == 0);
  McFighter g = fox();
  CHECK(fabsf(g.percent - 10) < 0.01f && g.hitlag == mc_hitlag_frames(10));   /* Melee's victim hitlag */
  for (int i = 0; i < 12; i++) steps(1, &idle);
  g = fox();
  CHECK(g.kb_vel_x > 0 && g.x > f.x);                      /* launched away from the enemy */
  /* intangible: a spot dodge (shield + down) */
  steps(120, &idle);
  McInput dodge = {0}; dodge.buttons = 0x0020; dodge.trigger_r = 255; dodge.stick_y = -127;
  McInput sh = {0}; sh.buttons = 0x0020; sh.trigger_r = 255;
  steps(4, &sh); mc_set_input(0, &dodge);
  for (int i = 0; i < 6; i++) CHECK(mc_step() == 0);
  f = fox();
  CHECK(mc_step_to_hits() == 0);
  int r = mc_apply_hit(0, &hit);
  CHECK(mc_step() == 0);
  CHECK(r == MC_HIT_INTANGIBLE && fabsf(fox().percent - f.percent) < 0.01f);
  /* shield */
  steps(60, &idle); steps(6, &sh);
  f = fox();
  mc_set_input(0, &sh);
  CHECK(mc_step_to_hits() == 0);
  CHECK(mc_apply_hit(0, &hit) == MC_HIT_SHIELDED);
  CHECK(mc_step() == 0);
  McFighter s1 = fox();
  CHECK(fabsf(s1.percent - f.percent) < 0.01f && s1.shield < f.shield - 5);   /* the shield took it */
  /* unblockable: through the shield */
  hit.unblockable = 1;
  CHECK(mc_step_to_hits() == 0);
  CHECK(mc_apply_hit(0, &hit) == MC_HIT_LANDED);
  CHECK(mc_step() == 0 && fox().percent > f.percent + 9);
  /* two hits in one pause: both damages count, the stronger knockback wins (each keeps its own descriptor) */
  CHECK(mc_set_percent(0, 0) == 0);
  steps(200, &idle);
  f = fox();
  McHit strong = {0}, weak = {0};
  strong.damage = 20; strong.angle = 45; strong.growth = 100; strong.base_kb = 80; strong.from_x = f.x - 10;
  weak.damage = 1; weak.angle = 45; weak.from_x = f.x - 10;
  CHECK(mc_step_to_hits() == 0);
  CHECK(mc_apply_hit(0, &strong) == MC_HIT_LANDED && mc_apply_hit(0, &weak) == MC_HIT_LANDED);
  CHECK(mc_step() == 0);
  CHECK(fabsf(fox().percent - 21) < 0.01f);
  for (int i = 0; i < 12; i++) steps(1, &idle);
  CHECK(fox().kb_vel_x > 1.0f);                            /* launched by the strong hit */
  /* Focus, benches and deaths reset Fox's percent (spec 5.3) */
  CHECK(mc_set_percent(0, 0) == 0);
  CHECK(fox().percent == 0);
  CHECK(mc_set_percent(1, 0) == 1 && mc_set_percent(9, 0) == -1);
  mc_shutdown();
}

/* HSD's texture matrices are post-transform matrices (GX_PTTEXMTX), live only with dual texgen, which GXInit turns on
 * before any capture. Firefox's flare and the shield's glint are a quarter texture mirrored by a x2 post matrix: without it
 * one quarter filled the quad (a square). */
static float mirror_uv_span(uint64_t mask) {
  const McGxVert *v; int nv; const McGxRun *r; int nr; float span = 0;
  CHECK(mc_gx_decode(mask, &v, &nv, &r, &nr) == 0);
  for (int i = 0; i < nr; i++) {
    if (r[i].wrap_s != 2 || r[i].wrap_t != 2) continue;
    float u0 = 1e9f, u1 = -1e9f;
    for (uint32_t k = r[i].first; k < r[i].first + r[i].count; k++) { u0 = fminf(u0, v[k].u); u1 = fmaxf(u1, v[k].u); }
    if (u1 - u0 > span) span = u1 - u0;
  }
  return span;
}
static void test_gx_post_texture_matrices(void) {
  const uint64_t EFFECTS = (1ull << 11) | (1ull << 12);
  McInput idle = {0}, upb = {0};
  upb.buttons = 0x0200; upb.stick_y = 127;
  solo_fox(); steps(180, &idle);
  CHECK(mc_gx_capture(1) == 0);
  steps(1, &upb); steps(44, &idle);   /* Firefox charging: the flare */
  CHECK(mirror_uv_span(EFFECTS) > 1.5f);
  mc_shutdown();
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
  test_shield();
  test_items();
  test_gx_capture();
  test_gx_decode();
  test_gx_post_texture_matrices();
  test_room_draws_fox_offscreen();
  test_sfx();
  test_laser_hits();
  test_apply_hit();
  test_respawn_after_hit();
  test_tumble_fighter();
  puts("rooms tests passed");
  return 0;
}
