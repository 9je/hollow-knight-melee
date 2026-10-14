/* raster.h: a decoded frame (mc_gx_decode) drawn as the mod draws it, into an RGBA image with a transparent background,
 * for art made of a render (Sheik's select tile and portraits, which Melee's select doesn't have). The runs are painted
 * back to front, each triangle's colour the vertex colour times its texture (nearest texel, the run's wrap modes), with
 * the four blends (opaque, alpha, add, multiply: a decal pass); alpha is how much of the pixel was drawn. The view fits
 * the drawn vertices. Header-only: frame_dump and the tests include it. */
#ifndef RASTER_H
#define RASTER_H
#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "melee_core.h"

static const McGxTexture *raster_texture(int32_t id) {
  static int32_t ids[512]; static McGxTexture tex[512]; static int n;
  for (int i = 0; i < n; i++) if (ids[i] == id) return tex[i].w ? &tex[i] : NULL;
  if (n == 512) return NULL;
  ids[n] = id;
  if (mc_gx_texture(id, &tex[n]) != 0) memset(&tex[n], 0, sizeof tex[n]);
  return tex[n++].w ? &tex[n - 1] : NULL;
}

static float raster_wrap(float c, int mode) {
  if (mode == 1) return c - floorf(c);
  if (mode == 2) { float t = c - 2 * floorf(c / 2); return t > 1 ? 2 - t : t; }
  return c < 0 ? 0 : c > 1 ? 1 : c;
}

/* out: size x size RGBA, top row first */
static void raster_frame(const McGxVert *v, int nv, const McGxRun *r, int nr, int size, uint8_t *out) {
  float *img = calloc((size_t)size * size * 4, sizeof *img);   /* r, g, b (premultiplied by the coverage), coverage */
  float x0 = 1e30f, x1 = -1e30f, y0 = 1e30f, y1 = -1e30f;
  for (int i = 0; i < nr; i++) for (uint32_t k = r[i].first; k < r[i].first + r[i].count && (int)k < nv; k++) {
    x0 = fminf(x0, v[k].x); x1 = fmaxf(x1, v[k].x); y0 = fminf(y0, v[k].y); y1 = fmaxf(y1, v[k].y);
  }
  float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, sp = fmaxf(x1 - x0, y1 - y0) * 0.6f + 1;
  float half = size / 2.0f;
  for (int i = 0; i < nr; i++) {
    const McGxTexture *t = raster_texture(r[i].tex);
    for (uint32_t k = r[i].first; k + 2 < r[i].first + r[i].count && (int)k + 2 < nv; k += 3) {
      const McGxVert *p = &v[k];
      float sx[3], sy[3];
      for (int j = 0; j < 3; j++) { sx[j] = (p[j].x - cx) / sp * half + half; sy[j] = half - (p[j].y - cy) / sp * half; }
      float d = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
      if (fabsf(d) < 1e-9f) continue;
      int bx0 = (int)floorf(fminf(sx[0], fminf(sx[1], sx[2]))), bx1 = (int)ceilf(fmaxf(sx[0], fmaxf(sx[1], sx[2])));
      int by0 = (int)floorf(fminf(sy[0], fminf(sy[1], sy[2]))), by1 = (int)ceilf(fmaxf(sy[0], fmaxf(sy[1], sy[2])));
      bx0 = bx0 < 0 ? 0 : bx0; by0 = by0 < 0 ? 0 : by0; bx1 = bx1 > size - 1 ? size - 1 : bx1; by1 = by1 > size - 1 ? size - 1 : by1;
      for (int y = by0; y <= by1; y++) for (int x = bx0; x <= bx1; x++) {
        float px = x + 0.5f, py = y + 0.5f;
        float l1 = ((sx[1] - px) * (sy[2] - py) - (sx[2] - px) * (sy[1] - py)) / d;
        float l2 = ((sx[2] - px) * (sy[0] - py) - (sx[0] - px) * (sy[2] - py)) / d;
        float l3 = 1 - l1 - l2;
        if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
        float col[3] = { (l1 * p[0].r + l2 * p[1].r + l3 * p[2].r) / 255, (l1 * p[0].g + l2 * p[1].g + l3 * p[2].g) / 255,
                         (l1 * p[0].b + l2 * p[1].b + l3 * p[2].b) / 255 };
        float al = (l1 * p[0].a + l2 * p[1].a + l3 * p[2].a) / 255;
        if (t) {
          float u = raster_wrap(l1 * p[0].u + l2 * p[1].u + l3 * p[2].u, r[i].wrap_s);
          float w = raster_wrap(l1 * p[0].v + l2 * p[1].v + l3 * p[2].v, r[i].wrap_t);
          int xi = (int)(u * (t->w - 1)), yi = (int)(w * (t->h - 1));
          xi = xi < 0 ? 0 : xi > (int)t->w - 1 ? (int)t->w - 1 : xi; yi = yi < 0 ? 0 : yi > (int)t->h - 1 ? (int)t->h - 1 : yi;
          const uint8_t *tx = t->rgba + 4 * ((size_t)yi * t->w + xi);
          col[0] *= tx[0] / 255.0f; col[1] *= tx[1] / 255.0f; col[2] *= tx[2] / 255.0f; al *= tx[3] / 255.0f;
        }
        al = al < 0 ? 0 : al > 1 ? 1 : al;
        float *dst = img + 4 * ((size_t)y * size + x), cov = dst[3];
        switch (r[i].blend) {
          case MC_GX_MULTIPLY: for (int c = 0; c < 3; c++) dst[c] *= 1 - al + col[c] * al; break;
          case MC_GX_ADD: for (int c = 0; c < 3; c++) dst[c] += col[c] * al; dst[3] = fmaxf(cov, al); break;
          case MC_GX_ALPHA: for (int c = 0; c < 3; c++) dst[c] = dst[c] * (1 - al) + col[c] * al; dst[3] = cov + al * (1 - cov); break;
          default: for (int c = 0; c < 3; c++) dst[c] = col[c]; dst[3] = 1; break;
        }
      }
    }
  }
  for (size_t i = 0; i < (size_t)size * size; i++) {
    float cov = img[4 * i + 3];
    for (int c = 0; c < 3; c++) {
      float f = cov > 0 ? img[4 * i + c] / cov : 0;   /* the colour, with the coverage taken back out */
      out[4 * i + c] = (uint8_t)(f < 0 ? 0 : f > 1 ? 255 : f * 255 + 0.5f);
    }
    out[4 * i + 3] = (uint8_t)(cov * 255 + 0.5f);
  }
  free(img);
}
#endif
