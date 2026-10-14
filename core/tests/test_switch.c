#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"
#include "standin.h"
/* test_switch: the mod's character change, as it happens in play. One character is played with the Fox stand-in, Melee's
 * drawing captured and decoded every frame, KO'd and respawned (Hollow Knight's death); then the core is started again
 * (mc_init, as FoxRunner.StartCoreWork does) with the next character, and that one plays too. Sheik then Link crashed the
 * worker thread in play (2026-10-13). usage: test_switch <mem1.bin> <iso> <from id> <to id> */
static void die(const char *w) { printf("FAIL %s: %s\n", w, mc_error()); exit(1); }
static void st(int n, McInput in, int decode) {
  for (int i = 0; i < n; i++) {
    mc_set_input(0, &in);
    if (mc_step()) die("step");
    if (decode) { const McGxVert *v; int nv; const McGxRun *r; int nr; if (mc_gx_decode(~0ull, &v, &nv, &r, &nr)) die("decode"); }
  }
}
static void start(const char *mem1, const char *iso, int id) {
  uint8_t s[MC_START_SIZE];
  if (mc_init(mem1, iso)) die("init");
  mc_standin_start(s, id);
  if (mc_start_match(s, 1, 0)) die("start");
  McInput z = {0};
  mc_set_input(1, &z); mc_set_input(2, NULL); mc_set_input(3, NULL);
  st(130, z, 0);
  if (mc_set_edge_assist(1)) die("edge assist");
  if (mc_standin_set(1)) die("standin");
  if (mc_gx_capture(1)) die("capture");
}
int main(int argc, char **v) {
  if (argc < 5) { printf("usage: test_switch <mem1.bin> <iso> <from id> <to id>\n"); return 2; }
  int from = atoi(v[3]), to = atoi(v[4]);
  McInput z = {0}, B = {.buttons = 0x0200, .stick_y = -127}, A = {.buttons = 0x0100};
  start(v[1], v[2], from);
  if (mc_place_fighter(0, 0, 0)) die("place");
  st(60, z, 1);
  st(2, B, 1); st(60, z, 1);     /* down-B: Zelda/Sheik's transform */
  st(2, A, 1); st(40, z, 1);
  if (mc_ko(0, 0)) die("ko");
  st(60, z, 1);
  if (mc_respawn_fighter(0, 0, 50)) die("respawn");
  st(90, z, 1);
  McFighter f; if (mc_read_fighter(0, 0, &f) < 0) die("read");
  printf("%d: action %u at (%.1f, %.1f) before the change\n", from, f.action, f.x, f.y);
  start(v[1], v[2], to);
  if (mc_place_fighter(0, 0, 0)) die("place");
  st(120, z, 1);
  st(2, A, 1); st(60, z, 1);
  if (mc_read_fighter(0, 0, &f) < 0) die("read");
  printf("%d: action %u at (%.1f, %.1f) after the change\n", to, f.action, f.x, f.y);
  mc_shutdown();
  printf("switch %d -> %d ok\n", from, to);
  return 0;
}
