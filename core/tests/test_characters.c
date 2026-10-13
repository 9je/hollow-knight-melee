#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"
#include "standin.h"
/* test_characters: one Melee character (external id 0..25) with the Fox stand-in awake in front of it: every move both ways
 * (jab, tilts, smashes, specials, shield, roll, taunt; aerials and air specials; grab and the four throws), with Melee's
 * drawing captured and decoded each frame. Fails on any core error or undecodable draw. usage: test_characters <mem1.bin>
 * <iso> <id>; `make characters-test` runs all 26. */
static int id;
static void die(const char *w) { printf("%d FAIL %s: %s\n", id, w, mc_error()); exit(1); }
static int frames, maxseen, specials;
static void st(int n, McInput in) {
  for (int i = 0; i < n; i++) {
    mc_set_input(0, &in);
    if (mc_step()) die("step");
    const McGxVert *v; int nv; const McGxRun *r; int nr;
    if (mc_gx_decode(~0ull, &v, &nv, &r, &nr)) die("decode");
    frames++;
    McFighter ff; mc_read_fighter(0, 0, &ff); if ((int)ff.action > maxseen) maxseen = ff.action; if (ff.action >= 341) specials++;
  }
}
int main(int argc, char **v) {
  id = atoi(v[3]);
  uint8_t s[MC_START_SIZE];
  if (mc_init(v[1], v[2])) die("init");
  mc_standin_start(s, id);
  if (mc_start_match(s, 1, 0)) die("start");
  McInput z = {0};
  mc_set_input(1, &z); mc_set_input(2, NULL); mc_set_input(3, NULL);
  st(180, z);
  if (mc_gx_capture(1)) die("capture");
  if (mc_standin_set(1)) die("standin");
  int maxact = 0;
  for (int dir = 1; dir >= -1; dir -= 2) {
    McFighter f; mc_read_fighter(0, 0, &f);
    int8_t fw = (int8_t)(127 * dir), bk = (int8_t)(-127 * dir);
    McInput A = {.buttons = 0x0100}, B = {.buttons = 0x0200}, X = {.buttons = 0x0800}, Z = {.buttons = 0x0010}, R = {.buttons = 0x0020, .trigger_r = 255}, D = {.buttons = 0x0008};
    /* turn to face dir */
    McInput t = {.stick_x = fw}; st(3, t); st(30, z);
    McInput seq[][2] = {
      {A, z}, {{.buttons=0x0100,.stick_x=(int8_t)(60*dir)}, z}, {{.buttons=0x0100,.stick_y=60}, z}, {{.buttons=0x0100,.stick_y=-60}, z},
      {{.cstick_x=fw}, z}, {{.cstick_y=127}, z}, {{.cstick_y=-127}, z},
      {B, z}, {{.buttons=0x0200,.stick_x=fw}, z}, {{.buttons=0x0200,.stick_y=127}, z}, {{.buttons=0x0200,.stick_y=-127}, z},
      {R, z}, {{.buttons=0x0020,.trigger_r=255,.stick_x=fw}, z}, {D, z},
    };
    for (unsigned k = 0; k < sizeof seq / sizeof seq[0]; k++) {
      mc_read_fighter(0, 0, &f);
      if (mc_standin_wake(f.x + 12 * dir, f.y, -dir)) die("wake");
      st(1, seq[k][0]); st(4, seq[k][0]); st(90, seq[k][1]);
      for (int w = 0; w < 6; w++) { McInput up = {.stick_y = 127}; st(5, up); st(25, z); }   /* recover upward if launched */
      mc_read_fighter(0, 0, &f); if ((int)f.action > maxact) maxact = f.action;
    }
    /* aerials and air specials from a jump */
    McInput air[] = { A, {.cstick_x=fw}, {.cstick_x=bk}, {.cstick_y=127}, {.cstick_y=-127}, B, {.buttons=0x0200,.stick_x=fw}, {.buttons=0x0200,.stick_y=127}, {.buttons=0x0200,.stick_y=-127}, {.buttons=0x0020,.trigger_r=255} };
    for (unsigned k = 0; k < sizeof air / sizeof air[0]; k++) {
      st(1, X); st(6, z); st(3, air[k]); st(80, z);
      for (int w = 0; w < 6; w++) { McInput up = {.stick_y = 127}; st(5, up); st(25, z); }
    }
    /* grab and four throws, with the stand-in in front */
    McInput thr[] = { {.stick_x=fw}, {.stick_x=bk}, {.stick_y=127}, {.stick_y=-127} };
    for (int k = 0; k < 4; k++) {
      mc_read_fighter(0, 0, &f);
      if (mc_standin_wake(f.x + 10 * dir, f.y, -dir)) die("wake");
      st(2, z); st(2, Z); st(10, z); st(2, A); st(15, z); st(3, thr[k]); st(90, z);
    }
    McHitbox h[8]; int n = mc_read_hitboxes(0, 0, h, 8); (void)n;
    if (mc_standin_sleep()) die("sleep");
    st(5, z);
  }
  printf("%d ok %d frames, max action %d, %d special frames, decode errors %u\n", id, frames, maxseen, specials, mc_gx_decode_errors());
  return mc_gx_decode_errors() != 0 || specials == 0;
}
