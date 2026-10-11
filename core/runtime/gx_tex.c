/* gx_tex.c - GameCube GX texture formats to RGBA8 (row-major, top row first); see tools/.../gxtex.py. */
#include <string.h>
#include "gx_draw.h"

static void block_dims(int fmt, int *bw, int *bh, int *bytes) {
  *bytes = 32;
  switch (fmt) {
    case 0: case 8: case 14: *bw = 8; *bh = 8; break;          /* I4, CI4, CMPR */
    case 1: case 2: case 9: *bw = 8; *bh = 4; break;           /* I8, IA4, CI8 */
    case 6: *bw = 4; *bh = 4; *bytes = 64; break;              /* RGBA8 */
    default: *bw = 4; *bh = 4; break;                          /* IA8, RGB565, RGB5A3 */
  }
}

int gx_tex_supported(int fmt) { return fmt <= 6 || fmt == 8 || fmt == 9 || fmt == 14; }

uint32_t gx_tex_bytes(int fmt, int w, int h) {
  int bw, bh, bytes;
  block_dims(fmt, &bw, &bh, &bytes);
  return (uint32_t)((w + bw - 1) / bw) * (uint32_t)((h + bh - 1) / bh) * (uint32_t)bytes;
}

static void rgb565(uint32_t v, uint8_t *o) {
  uint32_t r = v >> 11, g = (v >> 5) & 0x3F, b = v & 0x1F;
  o[0] = (uint8_t)(r << 3 | r >> 2); o[1] = (uint8_t)(g << 2 | g >> 4); o[2] = (uint8_t)(b << 3 | b >> 2); o[3] = 255;
}
static void rgb5a3(uint32_t v, uint8_t *o) {
  if (v & 0x8000) {
    uint32_t r = (v >> 10) & 0x1F, g = (v >> 5) & 0x1F, b = v & 0x1F;
    o[0] = (uint8_t)(r << 3 | r >> 2); o[1] = (uint8_t)(g << 3 | g >> 2); o[2] = (uint8_t)(b << 3 | b >> 2); o[3] = 255;
  } else {
    uint32_t a = (v >> 12) & 7;
    o[0] = (uint8_t)(((v >> 8) & 0xF) * 17); o[1] = (uint8_t)(((v >> 4) & 0xF) * 17); o[2] = (uint8_t)((v & 0xF) * 17);
    o[3] = (uint8_t)(a << 5 | a << 2 | a >> 1);
  }
}
static void ia8(uint32_t v, uint8_t *o) { o[0] = o[1] = o[2] = (uint8_t)(v & 0xFF); o[3] = (uint8_t)(v >> 8); }
static void pal(const uint8_t *tlut, int tfmt, uint32_t i, uint8_t *o) {
  uint32_t v = (uint32_t)tlut[2 * i] << 8 | tlut[2 * i + 1];
  if (tfmt == 0) ia8(v, o); else if (tfmt == 1) rgb565(v, o); else rgb5a3(v, o);
}

/* tlut: 2 bytes per entry, big-endian (16 entries for CI4, 256 for CI8) */
void gx_tex_decode(const uint8_t *src, int fmt, int w, int h, const uint8_t *tlut, int tlut_fmt, uint8_t *out) {
  int bw, bh, bytes;
  block_dims(fmt, &bw, &bh, &bytes);
  memset(out, 0, (size_t)w * (size_t)h * 4);
  const uint8_t *s = src;
  uint8_t px[4];
#define PUT(X, Y) do { int x_ = (X), y_ = (Y); if (x_ < w && y_ < h) memcpy(out + 4 * ((size_t)y_ * (size_t)w + (size_t)x_), px, 4); } while (0)
  for (int by = 0; by < h; by += bh)
    for (int bx = 0; bx < w; bx += bw) {
      if (fmt == 14) {   /* CMPR: an 8x8 tile of four 4x4 DXT1 blocks */
        for (int sub = 0; sub < 4; sub++, s += 8) {
          uint32_t c0 = (uint32_t)s[0] << 8 | s[1], c1 = (uint32_t)s[2] << 8 | s[3];
          uint32_t bits = (uint32_t)s[4] << 24 | (uint32_t)s[5] << 16 | (uint32_t)s[6] << 8 | s[7];
          uint8_t p[4][4];
          rgb565(c0, p[0]); rgb565(c1, p[1]);
          for (int k = 0; k < 3; k++) {
            if (c0 > c1) { p[2][k] = (uint8_t)((2 * p[0][k] + p[1][k]) / 3); p[3][k] = (uint8_t)((p[0][k] + 2 * p[1][k]) / 3); }
            else { p[2][k] = (uint8_t)((p[0][k] + p[1][k]) / 2); p[3][k] = 0; }
          }
          p[2][3] = 255; p[3][3] = c0 > c1 ? 255 : 0;
          int sx = bx + (sub & 1) * 4, sy = by + (sub >> 1) * 4;
          for (int i = 0; i < 16; i++) { memcpy(px, p[(bits >> (30 - 2 * i)) & 3], 4); PUT(sx + i % 4, sy + i / 4); }
        }
        continue;
      }
      if (fmt == 6) {    /* RGBA8: AR plane then GB plane */
        for (int i = 0; i < 16; i++) {
          px[3] = s[2 * i]; px[0] = s[2 * i + 1]; px[1] = s[32 + 2 * i]; px[2] = s[33 + 2 * i];
          PUT(bx + i % 4, by + i / 4);
        }
        s += 64;
        continue;
      }
      for (int y = 0; y < bh; y++)
        for (int x = 0; x < bw; x++) {
          uint32_t v;
          switch (fmt) {
            case 0: case 8:                 /* 4 bits per texel */
              v = (x & 1) ? (uint32_t)(*s++ & 0xF) : (uint32_t)(*s >> 4);
              if (fmt == 8) pal(tlut, tlut_fmt, v, px); else { px[0] = px[1] = px[2] = px[3] = (uint8_t)(v * 17); }
              break;
            case 1: v = *s++; px[0] = px[1] = px[2] = px[3] = (uint8_t)v; break;   /* I8: alpha is the intensity */
            case 2: v = *s++; px[0] = px[1] = px[2] = (uint8_t)((v & 0xF) * 17); px[3] = (uint8_t)((v >> 4) * 17); break;
            case 9: v = *s++; pal(tlut, tlut_fmt, v, px); break;
            default:
              v = (uint32_t)s[0] << 8 | s[1]; s += 2;
              if (fmt == 3) ia8(v, px); else if (fmt == 4) rgb565(v, px); else rgb5a3(v, px);
          }
          PUT(bx + x, by + y);
        }
    }
#undef PUT
}
