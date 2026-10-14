#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"
#include "standin.h"
#include "raster.h"
/* frame_dump: one character, a few frames in, Melee's drawing captured and decoded, dumped for ssbmhk-frame-png (tools/src/ssbmhk_tools/framepng.py): the frame's
 * triangles, their vertex colours and the textures they sample; and, given a raster path, the frame drawn as the mod draws it
 * (raster.h) over a transparent background: u32 width, u32 height, then RGBA (top row first), for art made of a render.
 * usage: frame_dump <mem1> <iso> <id> <script> <out.bin> [costume [raster.rgba [size]]]
 * script: a string of per-frame letters, '.' idle, 'b' down-B, 'B' neutral B, 'a' A, 'd' down tilt (stick down), 'r' right,
 * 'l' left, 't' taunt (D-pad up), 'j' jump; 180 idle frames first. costume: the character's costume number (0: its first).
 * size: the raster's width and height in pixels (400 by default) */
static void die(const char *w) { printf("FAIL %s: %s\n", w, mc_error()); exit(1); }
int main(int argc, char **v) {
  int id = atoi(v[3]);
  uint8_t s[MC_START_SIZE];
  if (mc_init(v[1], v[2])) die("init");
  mc_solo_start(s, id);
  if (argc > 6) s[0x60 + 3] = (uint8_t)atoi(v[6]);   /* the player block's costume byte */
  if (mc_start_match(s, 1, 0)) die("start");
  McInput z = {0};
  mc_set_input(1, NULL); mc_set_input(2, NULL); mc_set_input(3, NULL);
  for (int i = 0; i < 180; i++) { mc_set_input(0, &z); if (mc_step()) die("step"); }
  if (mc_gx_capture(1)) die("capture");
  for (const char *p = v[4]; *p; p++) {
    McInput in = {0};
    switch (*p) {
      case 'b': in.buttons = 0x0200; in.stick_y = -127; break;
      case 'B': in.buttons = 0x0200; break;
      case 'a': in.buttons = 0x0100; break;
      case 'd': in.stick_y = -127; break;
      case 'r': in.stick_x = 127; break;
      case 'l': in.stick_x = -127; break;
      case 't': in.buttons = 0x0008; break;
      case 'j': in.buttons = 0x0800; break;
      default: break;
    }
    mc_set_input(0, &in);
    if (mc_step()) die("step");
  }
  const McGxVert *vt; int nv; const McGxRun *r; int nr;
  /* what the mod decodes: fighters, items and effects (MC_GX_MASK=<hex> for another set, ~0 for everything) */
  uint64_t mask = (1ull << 8) | (1ull << 9) | (1ull << 11) | (1ull << 12);
  if (getenv("MC_GX_MASK")) mask = strtoull(getenv("MC_GX_MASK"), NULL, 16);
  if (mc_gx_decode(mask, &vt, &nv, &r, &nr)) die("decode");
  McFighter f; mc_read_fighter(0, 0, &f);
  FILE *o = fopen(v[5], "wb");
  if (!o) die("open");
  fwrite(&nv, 4, 1, o); fwrite(&nr, 4, 1, o);
  fwrite(&f.x, 4, 1, o); fwrite(&f.y, 4, 1, o);
  fwrite(vt, sizeof *vt, (size_t)nv, o);
  fwrite(r, sizeof *r, (size_t)nr, o);
  /* the textures used, each once */
  int32_t seen[512]; int ns = 0;
  for (int i = 0; i < nr; i++) {
    int dup = 0;
    for (int k = 0; k < ns; k++) if (seen[k] == r[i].tex) dup = 1;
    if (dup || ns == 512) continue;
    seen[ns++] = r[i].tex;
  }
  fwrite(&ns, 4, 1, o);
  for (int k = 0; k < ns; k++) {
    McGxTexture t;
    int ok = mc_gx_texture(seen[k], &t) == 0;
    uint32_t w = ok ? t.w : 0, h = ok ? t.h : 0;
    fwrite(&seen[k], 4, 1, o); fwrite(&w, 4, 1, o); fwrite(&h, 4, 1, o);
    if (ok) fwrite(t.rgba, 4, (size_t)w * h, o);
    if (!ok) printf("texture %d: %s\n", seen[k], mc_error());
  }
  fclose(o);
  printf("%d: action %u, %d verts, %d runs, %d textures -> %s\n", id, f.action, nv, nr, ns, v[5]);
  if (argc > 7) {
    uint32_t size = argc > 8 ? (uint32_t)atoi(v[8]) : 400;
    if (size < 16 || size > 4096) { printf("FAIL raster: size %u\n", size); return 1; }
    uint8_t *img = malloc((size_t)size * size * 4);
    raster_frame(vt, nv, r, nr, (int)size, img);
    FILE *ro = fopen(v[7], "wb");
    if (!ro) die("open raster");
    fwrite(&size, 4, 1, ro); fwrite(&size, 4, 1, ro); fwrite(img, 4, (size_t)size * size, ro);
    fclose(ro); free(img);
    printf("raster %ux%u -> %s\n", size, size, v[7]);
  }
  return 0;
}
