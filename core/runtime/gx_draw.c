/* gx_draw.c - decodes a captured GX command stream (gx.c) into world-space triangles, as Melee drew them.
 *
 * The decoder replays the GX state the stream sets (CP vertex formats and arrays, XF matrices, lights and texgens, BP
 * textures, TEV, blending) and turns every primitive drawn by a wanted object (by its GObj p_link) into triangles:
 * positions through the object's position matrix and back through the inverse of the camera's view matrix (Melee
 * world units), per-vertex colour from Melee's lighting and its TEV stages evaluated with a white texture (the host
 * multiplies by the texture: exact for the common texture x colour stages), and texture coordinates through the
 * texgen matrices. Triangles are ordered back to front after Melee's z modes (Hollow Knight's shaders write no depth;
 * see order_keys) and grouped into runs
 * of one texture and blend mode, drawn in order by the host. Textures are decoded once and cached by their source.
 * Approximations: TEV is evaluated per vertex; alpha compare, fog, indirect textures and dst-alpha are ignored. */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "mc_internal.h"
#include "melee_core.h"
#include "gx_draw.h"

typedef struct {
  uint32_t matidx_a, matidx_b, vcd_lo, vcd_hi, vat_a[8], vat_b[8], vat_c[8], base[16], stride[16];
  uint32_t xf[0x800];          /* XF memory 0x0000-0x07FF (matrices, normal matrices, post matrices, lights) */
  uint32_t xfr[0x100];         /* XF registers 0x1000-0x10FF */
  uint32_t bp[256], bpmask;
  uint32_t tev_raw[4][2], konst_raw[4][2];   /* [i][0] RA word, [i][1] BG word (BP 0xE0..0xE7): TEV registers, konsts */
  uint8_t tmem[0x80000];
} GxState;

typedef struct { McGxVert v[3]; float depth; int32_t tex; uint8_t blend, p_link, zmode; uint32_t gobj; } Tri;

typedef struct { uint32_t addr, fmt, w, h, wrap, tlut_hash; uint8_t *rgba; uint32_t gen, last_used; int baked; int ax, ay, atlas; } Tex;   /* atlas: 0 not yet, 1 in it, -1 no room */
#define FMT_BAKED 0x100u     /* a texture with a draw's TEV baked in: addr = source texture id, tlut_hash = TEV hash */
#define MAX_BAKED 256
static uint32_t decode_no;   /* decodes so far: baked textures unused for a while are recycled */

/* HSD sends GX state only when it changes, so state persists across frames: `live` is the state after the last
 * captured frame (gx_track_frame, every frame), `start` the state when that frame began (where a decode replays it) */
static GxState S, live, start;
static int live_ready;
static const uint8_t *M;       /* MEM1 */
static Tri *tris; static int ntris, captris;
static McGxVert *out_v; static int cap_v;
static McGxRun *out_r; static int cap_r;
static Tex *texs; static int ntex, captex;
static int depth_dl;
static uint32_t state_epoch = 1;   /* bumped by every state load: cached vertices are only valid within one epoch */
#define PLAY_DEPTH 500.0f
static uint32_t size_mismatches;   /* vertex_size disagreeing with a full parse (mc_gx_size_mismatches) */
static uint32_t decode_errors;

/* the object drawing now */
static uint64_t want_mask;
static int span_want; static uint32_t span_gobj; static uint8_t span_plink;
static float inv_view[12];

static uint32_t be32(const uint8_t *p) { return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3]; }
static uint16_t be16(const uint8_t *p) { return (uint16_t)(p[0] << 8 | p[1]); }
static float u2f32(uint32_t u) { float f; memcpy(&f, &u, 4); return f; }
static const uint8_t *phys(uint32_t addr, uint32_t n) {
  addr &= 0x01FFFFFFu;
  return n <= MEM1_SIZE && addr <= MEM1_SIZE - n ? M + addr : NULL;
}

/* ---- matrices ---- */
static void invert_view(const float *v, float *o) {   /* rigid 3x4 [R|t]: inverse [R^T | -R^T t] (scale tolerated) */
  float r[9] = {v[0], v[1], v[2], v[4], v[5], v[6], v[8], v[9], v[10]};
  float det = r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6]) + r[2] * (r[3] * r[7] - r[4] * r[6]);
  if (fabsf(det) < 1e-12f) { memset(o, 0, 48); o[0] = o[5] = o[10] = 1; return; }
  float id = 1.0f / det, iv[9] = {
    (r[4] * r[8] - r[5] * r[7]) * id, (r[2] * r[7] - r[1] * r[8]) * id, (r[1] * r[5] - r[2] * r[4]) * id,
    (r[5] * r[6] - r[3] * r[8]) * id, (r[0] * r[8] - r[2] * r[6]) * id, (r[2] * r[3] - r[0] * r[5]) * id,
    (r[3] * r[7] - r[4] * r[6]) * id, (r[1] * r[6] - r[0] * r[7]) * id, (r[0] * r[4] - r[1] * r[3]) * id};
  for (int i = 0; i < 3; i++) {
    o[4 * i + 0] = iv[3 * i]; o[4 * i + 1] = iv[3 * i + 1]; o[4 * i + 2] = iv[3 * i + 2];
    o[4 * i + 3] = -(iv[3 * i] * v[3] + iv[3 * i + 1] * v[7] + iv[3 * i + 2] * v[11]);
  }
}
static float xfm(int a) { return u2f32(S.xf[a & 0x7FF]); }

/* ---- textures ---- */
static uint32_t fnv(const uint8_t *p, uint32_t n) { uint32_t h = 2166136261u; while (n--) h = (h ^ *p++) * 16777619u; return h; }

static int32_t texture_for(int map) {
  int hi = map >= 4, m = map & 3;
  uint32_t img0 = S.bp[(hi ? 0xA8 : 0x88) + m], img3 = S.bp[(hi ? 0xB4 : 0x94) + m];
  uint32_t mode0 = S.bp[(hi ? 0xA0 : 0x80) + m], tlut = S.bp[(hi ? 0xB8 : 0x98) + m];
  uint32_t w = (img0 & 0x3FF) + 1, h = ((img0 >> 10) & 0x3FF) + 1, fmt = (img0 >> 20) & 0xF;
  uint32_t addr = (img3 & 0xFFFFFF) << 5, wrap = mode0 & 0xF;
  if (!gx_tex_supported((int)fmt)) return -1;
  const uint8_t *src = phys(addr, gx_tex_bytes((int)fmt, (int)w, (int)h));
  if (!src) return -1;
  const uint8_t *tl = NULL; uint32_t th = 0; int tfmt = (int)((tlut >> 10) & 3);
  if (fmt == 8 || fmt == 9) {
    uint32_t off = (tlut & 0x3FF) << 9, n = fmt == 8 ? 32 : 512;
    if (off + n > sizeof S.tmem) return -1;
    tl = S.tmem + off; th = fnv(tl, n) ^ (uint32_t)tfmt;
  }
  for (int i = 0; i < ntex; i++) {
    Tex *t = &texs[i];
    if (t->addr == addr && t->fmt == fmt && t->w == w && t->h == h && t->wrap == wrap && t->tlut_hash == th) { t->last_used = decode_no; return i; }
  }
  if (ntex == captex) {
    int cap = captex ? captex * 2 : 256;
    Tex *p = realloc(texs, (size_t)cap * sizeof *p);
    if (!p) return -1;
    texs = p; captex = cap;
  }
  uint8_t *rgba = malloc((size_t)w * h * 4);
  if (!rgba) return -1;
  gx_tex_decode(src, (int)fmt, (int)w, (int)h, tl, tfmt, rgba);
  texs[ntex] = (Tex){addr, fmt, w, h, wrap, th, rgba, 1, decode_no, 0, 0, 0, 0};
  return ntex++;
}

/* ---- per-draw setup ---- */
typedef struct { float r, g, b, a; } C4;

#define STAGES 8
/* what a TEV stage's texture input is: TEX_NONE (the stage has no texture: the hardware gives 0), TEX_PRIMARY (the
 * draw's texture, left to the host: evaluated as white), TEX_SAMPLED (another texture, sampled per vertex) */
enum { TEX_NONE, TEX_PRIMARY, TEX_SAMPLED };
typedef struct {
  int ntev, texmap, texcoord, cull;
  int32_t tex;
  uint8_t blend, zmode;   /* zmode: BP 0x40 (bit 0 compare, bits 1-3 function, bit 4 update) */
  int stage_kind[STAGES]; int32_t stage_tex[STAGES]; int stage_coord[STAGES];
} DrawSetup;

#define tev_raw S.tev_raw
#define konst_raw S.konst_raw
static int s11(uint32_t v) { int x = (int)(v & 0x7FF); return x & 0x400 ? x - 0x800 : x; }
static C4 reg_color(int i) {          /* TEV colour registers PREV, C0, C1, C2: signed 11-bit */
  uint32_t ra = tev_raw[i][0], bg = tev_raw[i][1];
  return (C4){s11(ra) / 255.0f, s11(bg >> 12) / 255.0f, s11(bg) / 255.0f, s11(ra >> 12) / 255.0f};
}
static C4 konst_color(int i) {        /* konst colours K0..K3: unsigned 8-bit */
  uint32_t ra = konst_raw[i][0], bg = konst_raw[i][1];
  return (C4){(ra & 0xFF) / 255.0f, ((bg >> 12) & 0xFF) / 255.0f, (bg & 0xFF) / 255.0f, ((ra >> 12) & 0xFF) / 255.0f};
}
static C4 konst_regs[4], tev_regs[4];

static C4 u32c(uint32_t v) { return (C4){(v >> 24) / 255.0f, ((v >> 16) & 0xFF) / 255.0f, ((v >> 8) & 0xFF) / 255.0f, (v & 0xFF) / 255.0f}; }

static float konst_value(int sel, int alpha, int comp) {
  static const float frac[8] = {1.0f, 0.875f, 0.75f, 0.625f, 0.5f, 0.375f, 0.25f, 0.125f};
  if (sel < 8) return frac[sel];
  if (!alpha && sel >= 0x0C && sel <= 0x0F) { C4 k = konst_regs[sel - 0x0C]; return comp == 0 ? k.r : comp == 1 ? k.g : k.b; }
  if (sel >= 0x10) {
    C4 k = konst_regs[sel & 3];
    switch ((sel - 0x10) >> 2) { case 0: return k.r; case 1: return k.g; case 2: return k.b; default: return k.a; }
  }
  return 1.0f;
}

/* lighting for one channel (0/1): XF channel control, material/ambient colours, lights */
static C4 light_channel(int ch, const float *pv, const float *nv, int has_n, const C4 *vcol) {
  C4 out;
  for (int alpha = 0; alpha < 2; alpha++) {
    uint32_t ctl = S.xfr[(alpha ? 0x10 : 0x0E) + ch];
    C4 mat = (ctl & 1) ? vcol[ch] : u32c(S.xfr[0x0C + ch]);
    float m = alpha ? mat.a : 0;
    float res[3] = {mat.r, mat.g, mat.b};
    if (ctl & 2) {   /* lighting on */
      C4 amb = (ctl & 0x40) ? vcol[ch] : u32c(S.xfr[0x0A + ch]);
      float sum[4] = {amb.r, amb.g, amb.b, amb.a};
      uint32_t mask = ((ctl >> 2) & 0xF) | (((ctl >> 11) & 0xF) << 4);
      int dfn = (int)((ctl >> 7) & 3), attn = (int)((ctl >> 9) & 3);
      for (int l = 0; l < 8; l++) if (mask & (1u << l)) {
        int b = 0x600 + 0x10 * l;
        C4 lc = u32c(S.xf[b + 3]);
        float lp[3] = {xfm(b + 10), xfm(b + 11), xfm(b + 12)};
        float L[3] = {lp[0] - pv[0], lp[1] - pv[1], lp[2] - pv[2]};
        float d2 = L[0] * L[0] + L[1] * L[1] + L[2] * L[2], d = sqrtf(d2);
        if (d > 0) { L[0] /= d; L[1] /= d; L[2] /= d; }
        float ndl = has_n ? nv[0] * L[0] + nv[1] * L[1] + nv[2] * L[2] : 1.0f;
        float diff = dfn == 0 ? 1.0f : dfn == 2 && ndl < 0 ? 0 : ndl;
        float att = 1.0f;
        float dir[3] = {xfm(b + 13), xfm(b + 14), xfm(b + 15)};
        if (attn == 1) {          /* specular (GX_AF_SPEC): the light's dir field holds the half-angle vector */
          float a = ndl >= 0 && has_n ? nv[0] * dir[0] + nv[1] * dir[1] + nv[2] * dir[2] : 0;
          if (a < 0) a = 0;
          float ca = xfm(b + 4) + xfm(b + 5) * a + xfm(b + 6) * a * a;
          float da = xfm(b + 7) + xfm(b + 8) * a + xfm(b + 9) * a * a;
          att = (ca > 0 ? ca : 0) / (fabsf(da) > 1e-6f ? da : 1e-6f);
        } else if (attn == 3) {   /* spot: cos and distance attenuation (the direction is stored negated) */
          float cs = L[0] * dir[0] + L[1] * dir[1] + L[2] * dir[2];
          if (cs < 0) cs = 0;
          float ca = xfm(b + 4) + xfm(b + 5) * cs + xfm(b + 6) * cs * cs;
          float da = xfm(b + 7) + xfm(b + 8) * d + xfm(b + 9) * d2;
          att = (ca > 0 ? ca : 0) / (da > 1e-6f ? da : 1e-6f);
        }
        float k = diff * att;
        sum[0] += lc.r * k; sum[1] += lc.g * k; sum[2] += lc.b * k; sum[3] += lc.a * k;
      }
      for (int k = 0; k < 4; k++) sum[k] = sum[k] < 0 ? 0 : sum[k] > 1 ? 1 : sum[k];
      if (alpha) m *= sum[3]; else { res[0] *= sum[0]; res[1] *= sum[1]; res[2] *= sum[2]; }
    }
    if (alpha) out.a = m; else { out.r = res[0]; out.g = res[1]; out.b = res[2]; }
  }
  return out;
}

static float clampf(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

/* TEV with the draw's texture white (the host multiplies by it; texc: a texel, for baking); a stage's other texture
 * (a shadow or specular map) is the colour sampled for this vertex (stex), a stage without one reads 0 */
static C4 tev(const DrawSetup *d, const C4 *chan, const C4 *texc, const C4 *stex) {
  C4 reg[4];
  memcpy(reg, tev_regs, sizeof reg);
  C4 last = reg[0];
  for (int s = 0; s < d->ntev; s++) {
    uint32_t tref = S.bp[0x28 + s / 2] >> ((s & 1) ? 12 : 0);
    int chsel = (int)((tref >> 7) & 7);
    C4 ras = chsel == 0 ? chan[0] : chsel == 1 ? chan[1] : (C4){0, 0, 0, 0};
    int kind = s < STAGES ? d->stage_kind[s] : TEX_PRIMARY;
    C4 tex = kind == TEX_NONE ? (C4){0, 0, 0, 0} : kind == TEX_SAMPLED && stex ? stex[s] : texc ? *texc : (C4){1, 1, 1, 1};
    uint32_t ksel = S.bp[0xF6 + s / 2];
    int kc = (int)((ksel >> ((s & 1) ? 14 : 4)) & 0x1F), ka = (int)((ksel >> ((s & 1) ? 19 : 9)) & 0x1F);
    uint32_t ce = S.bp[0xC0 + 2 * s], ae = S.bp[0xC1 + 2 * s];
    float res[4];
    for (int comp = 0; comp < 3; comp++) {
      float in[4];
      for (int k = 0; k < 4; k++) {
        int sel = (int)((ce >> (12 - 4 * k)) & 0xF);   /* a b c d */
        float v;
        switch (sel) {
          case 0: v = comp == 0 ? reg[0].r : comp == 1 ? reg[0].g : reg[0].b; break;
          case 1: v = reg[0].a; break;
          case 2: case 4: case 6: { C4 r = reg[sel / 2]; v = comp == 0 ? r.r : comp == 1 ? r.g : r.b; } break;
          case 3: case 5: case 7: v = reg[(sel - 1) / 2].a; break;
          case 8: v = comp == 0 ? tex.r : comp == 1 ? tex.g : tex.b; break;
          case 9: v = tex.a; break;
          case 10: v = comp == 0 ? ras.r : comp == 1 ? ras.g : ras.b; break;
          case 11: v = ras.a; break;
          case 12: v = 1; break;
          case 13: v = 0.5f; break;
          case 14: v = konst_value(kc, 0, comp); break;
          default: v = 0;
        }
        in[k] = v;
      }
      int bias = (int)((ce >> 16) & 3), sub = (int)((ce >> 18) & 1), clamp = (int)((ce >> 19) & 1), scale = (int)((ce >> 20) & 3);
      float l = in[0] * (1 - in[2]) + in[1] * in[2], r;
      if (bias == 3) r = in[3] + ((sub ? fabsf(in[0] - in[1]) < 1e-4f : in[0] > in[1]) ? in[2] : 0);   /* compare (R8) */
      else {
        r = in[3] + (sub ? -l : l) + (bias == 1 ? 0.5f : bias == 2 ? -0.5f : 0);
        r *= scale == 1 ? 2 : scale == 2 ? 4 : scale == 3 ? 0.5f : 1;
      }
      res[comp] = clamp ? clampf(r, 0, 1) : clampf(r, -4, 4);
    }
    {
      float in[4];
      for (int k = 0; k < 4; k++) {
        int sel = (int)((ae >> (13 - 3 * k)) & 7);
        float v;
        switch (sel) {
          case 0: v = reg[0].a; break;
          case 1: v = reg[1].a; break;
          case 2: v = reg[2].a; break;
          case 3: v = reg[3].a; break;
          case 4: v = tex.a; break;
          case 5: v = ras.a; break;
          case 6: v = konst_value(ka, 1, 3); break;
          default: v = 0;
        }
        in[k] = v;
      }
      int bias = (int)((ae >> 16) & 3), sub = (int)((ae >> 18) & 1), clamp = (int)((ae >> 19) & 1), scale = (int)((ae >> 20) & 3);
      float l = in[0] * (1 - in[2]) + in[1] * in[2], r;
      if (bias == 3) r = in[3] + ((sub ? fabsf(in[0] - in[1]) < 1e-4f : in[0] > in[1]) ? in[2] : 0);
      else {
        r = in[3] + (sub ? -l : l) + (bias == 1 ? 0.5f : bias == 2 ? -0.5f : 0);
        r *= scale == 1 ? 2 : scale == 2 ? 4 : scale == 3 ? 0.5f : 1;
      }
      res[3] = clamp ? clampf(r, 0, 1) : clampf(r, -4, 4);
    }
    int cdest = (int)((ce >> 22) & 3), adest = (int)((ae >> 22) & 3);
    reg[cdest].r = res[0]; reg[cdest].g = res[1]; reg[cdest].b = res[2];
    reg[adest].a = res[3];
    last = (C4){res[0], res[1], res[2], res[3]};
  }
  return (C4){clampf(last.r, 0, 1), clampf(last.g, 0, 1), clampf(last.b, 0, 1), clampf(last.a, 0, 1)};
}

static void setup_draw(DrawSetup *d) {
  uint32_t gen = S.bp[0x00];
  d->ntev = (int)((gen >> 10) & 0xF) + 1;
  d->cull = (int)((gen >> 14) & 3);
  d->texmap = d->texcoord = -1;
  d->tex = -1;
  for (int s = 0; s < d->ntev && d->texmap < 0; s++) {
    uint32_t tref = S.bp[0x28 + s / 2] >> ((s & 1) ? 12 : 0);
    if (!((tref >> 6) & 1)) continue;   /* texture disabled for this stage */
    uint32_t ce = S.bp[0xC0 + 2 * s], ae = S.bp[0xC1 + 2 * s];
    int uses = 0;
    for (int k = 0; k < 4; k++) { int sel = (int)((ce >> (12 - 4 * k)) & 0xF); uses |= sel == 8 || sel == 9; }
    for (int k = 0; k < 4; k++) uses |= (int)((ae >> (13 - 3 * k)) & 7) == 4;
    if (!uses) continue;
    d->texmap = (int)(tref & 7); d->texcoord = (int)((tref >> 3) & 7);
  }
  if (d->texmap >= 0) d->tex = texture_for(d->texmap);
  for (int s = 0; s < STAGES; s++) {
    d->stage_kind[s] = TEX_NONE; d->stage_tex[s] = -1; d->stage_coord[s] = -1;
    if (s >= d->ntev) continue;
    uint32_t tref = S.bp[0x28 + s / 2] >> ((s & 1) ? 12 : 0);
    if (!((tref >> 6) & 1)) continue;
    int map = (int)(tref & 7);
    if (map == d->texmap) { d->stage_kind[s] = TEX_PRIMARY; continue; }
    int32_t t = texture_for(map);
    if (t < 0) { d->stage_kind[s] = TEX_PRIMARY; continue; }   /* unreadable: as white, the old way */
    d->stage_kind[s] = TEX_SAMPLED; d->stage_tex[s] = t; d->stage_coord[s] = (int)((tref >> 3) & 7);
  }
  uint32_t cm = S.bp[0x41];
  int blend = (int)(cm & 1), src = (int)((cm >> 8) & 7), dst = (int)((cm >> 5) & 7);
  d->blend = !blend ? MC_GX_OPAQUE : dst == 1 ? MC_GX_ADD : MC_GX_ALPHA;
  d->zmode = (uint8_t)(S.bp[0x40] & 0x1F);
  (void)src;
  for (int i = 0; i < 4; i++) { tev_regs[i] = reg_color(i); konst_regs[i] = konst_color(i); }
}

/* ---- vertices ---- */
typedef struct { const uint8_t *p; const uint8_t *end; } Rd;
static int rd_ok(Rd *r, uint32_t n) { return r->p && r->p + n <= r->end; }

static float comp_read(const uint8_t *p, int type, int frac, int *size) {
  switch (type) {
    case 0: *size = 1; return (float)p[0] / (float)(1 << frac);
    case 1: *size = 1; return (float)(int8_t)p[0] / (float)(1 << frac);
    case 2: *size = 2; return (float)be16(p) / (float)(1 << frac);
    case 3: *size = 2; return (float)(int16_t)be16(p) / (float)(1 << frac);
    default: *size = 4; return u2f32(be32(p));
  }
}
static int comp_size(int type) { return type <= 1 ? 1 : type <= 3 ? 2 : 4; }

/* an attribute's data: direct (in the stream) or indexed (from its array); returns the pointer, advances the stream */
static const uint8_t *attr(Rd *r, int mode, int array, uint32_t direct_size) {
  if (mode == 1) { if (!rd_ok(r, direct_size)) return NULL; const uint8_t *p = r->p; r->p += direct_size; return p; }
  uint32_t idx;
  if (mode == 2) { if (!rd_ok(r, 1)) return NULL; idx = r->p[0]; r->p += 1; }
  else { if (!rd_ok(r, 2)) return NULL; idx = be16(r->p); r->p += 2; }
  return phys(S.base[array] + idx * S.stride[array], direct_size);
}

static uint32_t color_size(int fmt) { return fmt == 0 || fmt == 3 ? 2 : fmt == 1 || fmt == 4 ? 3 : 4; }
static C4 color_read(const uint8_t *p, int fmt) {
  if (!p) return (C4){1, 1, 1, 1};
  switch (fmt) {
    case 0: { uint32_t v = be16(p); return (C4){(v >> 11) / 31.0f, ((v >> 5) & 0x3F) / 63.0f, (v & 0x1F) / 31.0f, 1}; }
    case 1: case 2: return (C4){p[0] / 255.0f, p[1] / 255.0f, p[2] / 255.0f, 1};
    case 3: { uint32_t v = be16(p); return (C4){(v >> 12) / 15.0f, ((v >> 8) & 0xF) / 15.0f, ((v >> 4) & 0xF) / 15.0f, (v & 0xF) / 15.0f}; }
    case 4: { uint32_t v = (uint32_t)p[0] << 16 | (uint32_t)p[1] << 8 | p[2];
              return (C4){(v >> 18) / 63.0f, ((v >> 12) & 0x3F) / 63.0f, ((v >> 6) & 0x3F) / 63.0f, (v & 0x3F) / 63.0f}; }
    default: return (C4){p[0] / 255.0f, p[1] / 255.0f, p[2] / 255.0f, p[3] / 255.0f};
  }
}

typedef struct { float wx, wy, wz, vz, sx, sy; C4 col; float u, v; C4 chan[2]; C4 stex[STAGES]; } Vtx;

static int wrap_i(int i, int n, int mode);
/* a texture's texel nearest (u, v), by its wrap modes */
static C4 sample_tex(const Tex *t, float u, float v) {
  int w = (int)t->w, h = (int)t->h;
  int x = wrap_i((int)floorf(u * (float)w), w, (int)(t->wrap & 3)), y = wrap_i((int)floorf(v * (float)h), h, (int)((t->wrap >> 2) & 3));
  const uint8_t *p = t->rgba + 4 * ((size_t)y * (size_t)w + (size_t)x);
  return (C4){p[0] / 255.0f, p[1] / 255.0f, p[2] / 255.0f, p[3] / 255.0f};
}

/* bytes per vertex in the stream for a vertex format: direct attributes by their format, indexed ones 1 or 2 */
static uint32_t vertex_size(int vat) {
  uint32_t lo = S.vcd_lo, hi = S.vcd_hi, a = S.vat_a[vat], b = S.vat_b[vat], cc = S.vat_c[vat], n = 0;
  if (lo & 1) n++;
  for (int i = 0; i < 8; i++) if (lo & (2u << i)) n++;
  uint32_t ix[4] = {0, 0, 1, 2};
  int pm = (int)((lo >> 9) & 3);
  n += pm == 1 ? (uint32_t)((a & 1) ? 3 : 2) * (uint32_t)comp_size((int)((a >> 1) & 7)) : ix[pm];
  int nm = (int)((lo >> 11) & 3);
  if (nm == 1) n += (uint32_t)(((a >> 9) & 1) ? 9 : 3) * (uint32_t)comp_size((int)((a >> 10) & 7));
  else if (nm) n += ix[nm] * ((((a >> 9) & 1) && ((a >> 31) & 1)) ? 3u : 1u);
  for (int ch = 0; ch < 2; ch++) {
    int cm = (int)((lo >> (13 + 2 * ch)) & 3);
    n += cm == 1 ? color_size((int)((a >> (ch ? 18 : 14)) & 7)) : ix[cm];
  }
  for (int t = 0; t < 8; t++) {
    int tm = (int)((hi >> (2 * t)) & 3);
    if (tm != 1) { n += ix[tm]; continue; }
    int cnt, type;
    switch (t) {
      case 0: cnt = (int)((a >> 21) & 1); type = (int)((a >> 22) & 7); break;
      case 1: cnt = (int)(b & 1); type = (int)((b >> 1) & 7); break;
      case 2: cnt = (int)((b >> 9) & 1); type = (int)((b >> 10) & 7); break;
      case 3: cnt = (int)((b >> 18) & 1); type = (int)((b >> 19) & 7); break;
      case 4: cnt = (int)((b >> 27) & 1); type = (int)((b >> 28) & 7); break;
      case 5: cnt = (int)((cc >> 5) & 1); type = (int)((cc >> 6) & 7); break;
      case 6: cnt = (int)((cc >> 14) & 1); type = (int)((cc >> 15) & 7); break;
      default: cnt = (int)((cc >> 23) & 1); type = (int)((cc >> 24) & 7); break;
    }
    n += (uint32_t)(cnt ? 2 : 1) * (uint32_t)comp_size(type);
  }
  return n;
}

/* texture coordinate `coord` (XF texgen 0..7) for a vertex: from its position, normal or a texture coordinate attribute,
 * through the texture matrix (and the post matrix with dual texgen), or a colour channel */
static void texgen(int coord, const float *pos, const float *nrm, float tc[8][2], const int *tmidx, const C4 *chan, float *ou, float *ov) {
  uint32_t info = S.xfr[0x40 + coord];
  int proj = (int)((info >> 1) & 1), form = (int)((info >> 2) & 1), type = (int)((info >> 4) & 7), row = (int)((info >> 7) & 0x1F);
  float src[4] = {0, 0, 1, 1};
  if (row == 0) { src[0] = pos[0]; src[1] = pos[1]; src[2] = pos[2]; }
  else if (row == 1) { src[0] = nrm[0]; src[1] = nrm[1]; src[2] = nrm[2]; }
  else if (row >= 5 && row <= 12) { src[0] = tc[row - 5][0]; src[1] = tc[row - 5][1]; src[2] = 1; }
  if (!form) src[2] = 1;
  if (type == 0) {
    int ma = tmidx[coord] * 4;
    float s = xfm(ma) * src[0] + xfm(ma + 1) * src[1] + xfm(ma + 2) * src[2] + xfm(ma + 3) * src[3];
    float t = xfm(ma + 4) * src[0] + xfm(ma + 5) * src[1] + xfm(ma + 6) * src[2] + xfm(ma + 7) * src[3];
    float q = proj ? xfm(ma + 8) * src[0] + xfm(ma + 9) * src[1] + xfm(ma + 10) * src[2] + xfm(ma + 11) * src[3] : 1;
    if (S.xfr[0x12] & 1) {   /* dual texgen: post-transform matrix */
      uint32_t post = S.xfr[0x50 + coord];
      int pa2 = 0x500 + (int)(post & 0x3F) * 4;
      float in3[3] = {s, t, q};
      if (post & 0x100) { float l = sqrtf(s * s + t * t + q * q); if (l > 0) { in3[0] /= l; in3[1] /= l; in3[2] /= l; } }
      s = xfm(pa2) * in3[0] + xfm(pa2 + 1) * in3[1] + xfm(pa2 + 2) * in3[2] + xfm(pa2 + 3);
      t = xfm(pa2 + 4) * in3[0] + xfm(pa2 + 5) * in3[1] + xfm(pa2 + 6) * in3[2] + xfm(pa2 + 7);
      q = xfm(pa2 + 8) * in3[0] + xfm(pa2 + 9) * in3[1] + xfm(pa2 + 10) * in3[2] + xfm(pa2 + 11);
      if (!proj) q = 1;
    }
    if (q != 0 && q != 1) { s /= q; t /= q; }
    *ou = s; *ov = t;
  } else if (type == 2 || type == 3) {   /* colour texgen */
    *ou = chan[type - 2].r; *ov = chan[type - 2].g;
  } else { *ou = src[0]; *ov = src[1]; }
}

static int read_vertex(Rd *r, int vat, const DrawSetup *d, Vtx *o) {
  uint32_t lo = S.vcd_lo, hi = S.vcd_hi, a = S.vat_a[vat], b = S.vat_b[vat], cc = S.vat_c[vat];
  int pmidx = (int)(S.matidx_a & 0x3F), tmidx[8];
  for (int i = 0; i < 8; i++) tmidx[i] = (int)(i < 4 ? (S.matidx_a >> (6 + 6 * i)) & 0x3F : (S.matidx_b >> (6 * (i - 4))) & 0x3F);
  if (lo & 1) { if (!rd_ok(r, 1)) return 0; pmidx = r->p[0]; r->p++; }
  for (int i = 0; i < 8; i++) if (lo & (2u << i)) { if (!rd_ok(r, 1)) return 0; tmidx[i] = r->p[0]; r->p++; }
  /* position */
  int pm = (int)((lo >> 9) & 3), pcnt = (int)(a & 1), ptype = (int)((a >> 1) & 7), pfrac = (int)((a >> 4) & 0x1F);
  int ncomp = pcnt ? 3 : 2, psz = comp_size(ptype);
  float pos[3] = {0, 0, 0};
  if (pm) {
    const uint8_t *p = attr(r, pm, 0, (uint32_t)(ncomp * psz));
    if (!p) return 0;
    for (int k = 0; k < ncomp; k++) { int sz; pos[k] = comp_read(p + k * psz, ptype, pfrac, &sz); }
  }
  /* normal */
  int nm = (int)((lo >> 11) & 3), ncnt = (int)((a >> 9) & 1), ntype = (int)((a >> 10) & 7), nidx3 = (int)((a >> 31) & 1);
  float nrm[3] = {0, 0, 1}; int has_n = 0;
  if (nm) {
    int nsz = comp_size(ntype), nfrac = ntype == 1 ? 6 : ntype == 3 ? 14 : 0, total = ncnt ? 9 : 3;
    const uint8_t *p;
    if (nm == 1) p = attr(r, 1, 1, (uint32_t)(total * nsz));
    else if (ncnt && nidx3) { p = attr(r, nm, 1, (uint32_t)(3 * nsz)); attr(r, nm, 1, (uint32_t)(3 * nsz)); attr(r, nm, 1, (uint32_t)(3 * nsz)); }
    else p = attr(r, nm, 1, (uint32_t)(3 * nsz));
    if (!p) return 0;
    for (int k = 0; k < 3; k++) { int sz; nrm[k] = comp_read(p + k * nsz, ntype, nfrac, &sz); }
    has_n = 1;
  }
  /* colours */
  C4 vcol[2] = {{1, 1, 1, 1}, {1, 1, 1, 1}};
  for (int ch = 0; ch < 2; ch++) {
    int cm = (int)((lo >> (13 + 2 * ch)) & 3), cfmt = (int)((a >> (ch ? 18 : 14)) & 7);
    if (!cm) continue;
    const uint8_t *p = attr(r, cm, 2 + ch, color_size(cfmt));
    if (!p) return 0;
    vcol[ch] = color_read(p, cfmt);
  }
  /* texture coordinates */
  float tc[8][2] = {{0}};
  for (int t = 0; t < 8; t++) {
    int tm = (int)((hi >> (2 * t)) & 3);
    if (!tm) continue;
    int cnt, type, frac;
    switch (t) {
      case 0: cnt = (int)((a >> 21) & 1); type = (int)((a >> 22) & 7); frac = (int)((a >> 25) & 0x1F); break;
      case 1: cnt = (int)(b & 1); type = (int)((b >> 1) & 7); frac = (int)((b >> 4) & 0x1F); break;
      case 2: cnt = (int)((b >> 9) & 1); type = (int)((b >> 10) & 7); frac = (int)((b >> 13) & 0x1F); break;
      case 3: cnt = (int)((b >> 18) & 1); type = (int)((b >> 19) & 7); frac = (int)((b >> 22) & 0x1F); break;
      case 4: cnt = (int)((b >> 27) & 1); type = (int)((b >> 28) & 7); frac = (int)(cc & 0x1F); break;
      case 5: cnt = (int)((cc >> 5) & 1); type = (int)((cc >> 6) & 7); frac = (int)((cc >> 9) & 0x1F); break;
      case 6: cnt = (int)((cc >> 14) & 1); type = (int)((cc >> 15) & 7); frac = (int)((cc >> 18) & 0x1F); break;
      default: cnt = (int)((cc >> 23) & 1); type = (int)((cc >> 24) & 7); frac = (int)((cc >> 27) & 0x1F); break;
    }
    int n = cnt ? 2 : 1, sz = comp_size(type);
    const uint8_t *p = attr(r, tm, 4 + t, (uint32_t)(n * sz));
    if (!p) return 0;
    for (int k = 0; k < n; k++) { int s2; tc[t][k] = comp_read(p + k * sz, type, frac, &s2); }
  }
  /* transform */
  int pa = pmidx * 4;
  float pv[3];
  for (int i = 0; i < 3; i++) pv[i] = xfm(pa + 4 * i) * pos[0] + xfm(pa + 4 * i + 1) * pos[1] + xfm(pa + 4 * i + 2) * pos[2] + xfm(pa + 4 * i + 3);
  float nv[3] = {0, 0, 1};
  if (has_n) {
    int na = 0x400 + (pmidx & 31) * 3;
    for (int i = 0; i < 3; i++) nv[i] = xfm(na + 3 * i) * nrm[0] + xfm(na + 3 * i + 1) * nrm[1] + xfm(na + 3 * i + 2) * nrm[2];
    float l = sqrtf(nv[0] * nv[0] + nv[1] * nv[1] + nv[2] * nv[2]);
    if (l > 0) { nv[0] /= l; nv[1] /= l; nv[2] /= l; }
  }
  o->vz = pv[2];
  o->wx = inv_view[0] * pv[0] + inv_view[1] * pv[1] + inv_view[2] * pv[2] + inv_view[3];
  o->wy = inv_view[4] * pv[0] + inv_view[5] * pv[1] + inv_view[6] * pv[2] + inv_view[7];
  o->wz = inv_view[8] * pv[0] + inv_view[9] * pv[1] + inv_view[10] * pv[2] + inv_view[11];
  /* screen position for culling: XF projection (0x1020..0x1026) */
  const float *pj = (const float *)&S.xfr[0x20];
  int ortho = S.xfr[0x26] != 0;                      /* XF 0x1026: 0 perspective, 1 orthographic */
  float w = ortho ? 1.0f : -pv[2];
  if (w == 0) w = 1e-6f;
  o->sx = ortho ? pj[0] * pv[0] + pj[1] : (pj[0] * pv[0] + pj[1] * pv[2]) / w;
  o->sy = ortho ? pj[2] * pv[1] + pj[3] : (pj[2] * pv[1] + pj[3] * pv[2]) / w;
  /* colour: lighting, then TEV */
  int nch = (int)(S.xfr[0x09] & 3);
  C4 chan[2] = {{1, 1, 1, 1}, {1, 1, 1, 1}};
  for (int ch = 0; ch < nch && ch < 2; ch++) chan[ch] = light_channel(ch, pv, nv, has_n, vcol);
  o->chan[0] = chan[0]; o->chan[1] = chan[1];
  /* texture coordinate for the draw's texture, and the other stages' textures sampled here */
  o->u = o->v = 0;
  if (d->texcoord >= 0) texgen(d->texcoord, pos, nrm, tc, tmidx, chan, &o->u, &o->v);
  for (int st = 0; st < STAGES; st++) {
    o->stex[st] = (C4){1, 1, 1, 1};
    if (d->stage_kind[st] != TEX_SAMPLED) continue;
    float u = 0, v = 0;
    if (d->stage_coord[st] >= 0) texgen(d->stage_coord[st], pos, nrm, tc, tmidx, chan, &u, &v);
    o->stex[st] = sample_tex(&texs[d->stage_tex[st]], u, v);
  }
  o->col = tev(d, chan, NULL, o->stex);
  return 1;
}

static void emit_tri(const Vtx *a, const Vtx *b, const Vtx *c, const DrawSetup *d) {
  if (!span_want) return;
  float cross = (b->sx - a->sx) * (c->sy - a->sy) - (b->sy - a->sy) * (c->sx - a->sx);
  if (d->cull == 3) return;
  /* effects far from the play plane are the stage's 3D background (Final Destination's sit 3200+ deep; Fox's own
   * effects stay within a few dozen units) */
  if ((span_plink == 11 || span_plink == 12) && (fabsf(a->wz) > PLAY_DEPTH || fabsf(b->wz) > PLAY_DEPTH || fabsf(c->wz) > PLAY_DEPTH)) return;
  /* BP GENMODE cull (hardware numbering, not the GX API's): 1 back, 2 front; with y up on screen, front faces wind
   * clockwise (cross < 0): Melee's camera-facing shield quad must survive back culling */
  if (d->cull == 1 && cross > 0) return;
  if (d->cull == 2 && cross < 0) return;
  if (ntris == captris) {
    int cap = captris ? captris * 2 : 8192;
    Tri *p = realloc(tris, (size_t)cap * sizeof *p);
    if (!p) return;
    tris = p; captris = cap;
  }
  Tri *t = &tris[ntris++];
  const Vtx *v[3] = {a, b, c};
  for (int i = 0; i < 3; i++) {
    t->v[i] = (McGxVert){v[i]->wx, v[i]->wy, v[i]->wz,
      (uint8_t)(v[i]->col.r * 255 + 0.5f), (uint8_t)(v[i]->col.g * 255 + 0.5f), (uint8_t)(v[i]->col.b * 255 + 0.5f), (uint8_t)(v[i]->col.a * 255 + 0.5f),
      v[i]->u, v[i]->v};
  }
  t->depth = (a->vz + b->vz + c->vz) / 3;
  t->tex = d->tex; t->blend = d->blend; t->zmode = d->zmode; t->p_link = span_plink; t->gobj = span_gobj;
}

/* the TEV's per-vertex inputs (the lit channel colours) are the same on every vertex: the draw's look is then a
 * function of the texture alone, and is baked into a texture with the exact TEV, per texel */
static int same_c4(const C4 *a, const C4 *b) {
  return fabsf(a->r - b->r) <= 0.004f && fabsf(a->g - b->g) <= 0.004f && fabsf(a->b - b->b) <= 0.004f && fabsf(a->a - b->a) <= 0.004f;
}
static int uniform_inputs(const DrawSetup *d, const Vtx *vs, int n) {
  for (int i = 1; i < n; i++) {
    for (int ch = 0; ch < 2; ch++) if (!same_c4(&vs[0].chan[ch], &vs[i].chan[ch])) return 0;
    for (int st = 0; st < STAGES; st++) if (d->stage_kind[st] == TEX_SAMPLED && !same_c4(&vs[0].stex[st], &vs[i].stex[st])) return 0;
  }
  return 1;
}

static int32_t baked_slot(void) {
  int nb = 0, victim = -1;
  for (int i = 0; i < ntex; i++) if (texs[i].baked) {
    nb++;
    if (texs[i].last_used + 2 < decode_no && (victim < 0 || texs[i].last_used < texs[victim].last_used)) victim = i;
  }
  if (nb >= MAX_BAKED && victim >= 0) { free(texs[victim].rgba); texs[victim].rgba = NULL; texs[victim].gen++; return victim; }
  if (ntex == captex) {
    int cap = captex ? captex * 2 : 256;
    Tex *p = realloc(texs, (size_t)cap * sizeof *p);
    if (!p) return -1;
    texs = p; captex = cap;
  }
  texs[ntex] = (Tex){0};
  texs[ntex].gen = 1;
  return ntex++;
}

/* the TEV is texture x colour (the host's multiply) on sampled texels: baking would change nothing but cost a texture
 * per frame for an animated colour (lasers, glows) */
static int tev_is_multiply(const DrawSetup *d, const Vtx *vs, const Tex *src) {
  C4 one = {1, 1, 1, 1};
  C4 w = tev(d, vs[0].chan, &one, vs[0].stex);
  uint32_t nt = src->w * src->h, step = nt / 31 + 1;
  for (uint32_t t = 0; t < nt; t += step) {
    const uint8_t *p = src->rgba + 4 * t;
    C4 tc = {p[0] / 255.0f, p[1] / 255.0f, p[2] / 255.0f, p[3] / 255.0f};
    C4 o = tev(d, vs[0].chan, &tc, vs[0].stex);
    if (fabsf(o.r - tc.r * w.r) > 0.008f || fabsf(o.g - tc.g * w.g) > 0.008f || fabsf(o.b - tc.b * w.b) > 0.008f || fabsf(o.a - tc.a * w.a) > 0.008f)
      return 0;
  }
  C4 zero = {0, 0, 0, 0}, o = tev(d, vs[0].chan, &zero, vs[0].stex);
  return fabsf(o.r) < 0.008f && fabsf(o.g) < 0.008f && fabsf(o.b) < 0.008f && fabsf(o.a) < 0.008f;
}

static void bake(DrawSetup *d, Vtx *vs, int n) {
  const Tex *src = &texs[d->tex];
  if (tev_is_multiply(d, vs, src)) return;
  uint32_t cfg[96]; int k = 0;
  cfg[k++] = (uint32_t)d->tex; cfg[k++] = (uint32_t)d->ntev;
  for (int i = 0; i < 4; i++) cfg[k++] = S.bp[0x28 + i];
  for (int i = 0; i < 2 * d->ntev && i < 32; i++) cfg[k++] = S.bp[0xC0 + i];
  for (int i = 0; i < 4; i++) cfg[k++] = S.bp[0xF6 + i];
  for (int i = 0; i < 4; i++) { cfg[k++] = tev_raw[i][0]; cfg[k++] = tev_raw[i][1]; cfg[k++] = konst_raw[i][0]; cfg[k++] = konst_raw[i][1]; }
  for (int ch = 0; ch < 2 + STAGES; ch++) {
    const C4 *c = ch < 2 ? &vs[0].chan[ch] : &vs[0].stex[ch - 2];
    cfg[k++] = (uint32_t)(c->r * 255 + 0.5f) << 24 | (uint32_t)(c->g * 255 + 0.5f) << 16 | (uint32_t)(c->b * 255 + 0.5f) << 8 | (uint32_t)(c->a * 255 + 0.5f);
  }
  uint32_t h = fnv((const uint8_t *)cfg, (uint32_t)k * 4);
  int32_t id = -1;
  for (int i = 0; i < ntex; i++)
    if (texs[i].baked && texs[i].addr == (uint32_t)d->tex && texs[i].tlut_hash == h && texs[i].rgba) { id = i; break; }
  if (id < 0) {
    id = baked_slot();
    if (id < 0) return;
    src = &texs[d->tex];   /* realloc may have moved it */
    uint8_t *rgba = malloc((size_t)src->w * src->h * 4);
    if (!rgba) return;
    for (uint32_t t = 0; t < src->w * src->h; t++) {
      const uint8_t *p = src->rgba + 4 * t;
      C4 tc = {p[0] / 255.0f, p[1] / 255.0f, p[2] / 255.0f, p[3] / 255.0f};
      C4 o = tev(d, vs[0].chan, &tc, vs[0].stex);
      rgba[4 * t] = (uint8_t)(o.r * 255 + 0.5f); rgba[4 * t + 1] = (uint8_t)(o.g * 255 + 0.5f);
      rgba[4 * t + 2] = (uint8_t)(o.b * 255 + 0.5f); rgba[4 * t + 3] = (uint8_t)(o.a * 255 + 0.5f);
    }
    uint32_t gen = texs[id].gen;
    texs[id] = (Tex){(uint32_t)d->tex, FMT_BAKED, src->w, src->h, src->wrap, h, rgba, gen, decode_no, 1, 0, 0, -1};
  }
  texs[id].last_used = decode_no;
  d->tex = id;
  for (int i = 0; i < n; i++) vs[i].col = (C4){1, 1, 1, 1};
}

/* a draw whose vertices cannot be read (or that writes no colour) is stepped over by its vertex size */
static int skip_draw(Rd *r, const uint8_t *first, uint32_t vsz, int n, int error) {
  if (error) decode_errors++;
  if (!first || (size_t)vsz * (size_t)n > (size_t)(r->end - first)) return 0;
  r->p = first + vsz * (uint32_t)n;
  return 1;
}

#define VCACHE 4096
typedef struct { uint32_t epoch, hash, size; uint8_t bytes[64]; Vtx v; } VCacheEntry;
static VCacheEntry vcache[VCACHE];

/* MC_GX_TRACE=<p_link>: every draw of that kind of object, with the state that coloured it, on stdout (a debugging aid:
 * which TEV, lighting or texture makes a part black) */
static int trace_plink = -2;
static void trace_draw(const DrawSetup *d, const Vtx *vs, int n, int prim) {
  if (trace_plink == -2) { const char *e = getenv("MC_GX_TRACE"); trace_plink = e ? atoi(e) : -1; }
  if (trace_plink < 0 || span_plink != trace_plink || n == 0) return;
  printf("draw gobj %08x prim %02x n %d ntev %d cull %d blend %d cmode %06x zmode %02x vcd_lo %05x nch %u\n", span_gobj, prim, n, d->ntev, d->cull, d->blend, S.bp[0x41], d->zmode, S.vcd_lo, S.xfr[0x09] & 3);
  for (int ch = 0; ch < 2; ch++)
    printf("  chan%d ctl %06x actl %06x mat %08x amb %08x\n", ch, S.xfr[0x0E + ch], S.xfr[0x10 + ch], S.xfr[0x0C + ch], S.xfr[0x0A + ch]);
  for (int st = 0; st < d->ntev; st++) {
    uint32_t tref = S.bp[0x28 + st / 2] >> ((st & 1) ? 12 : 0), ksel = S.bp[0xF6 + st / 2];
    printf("  tev%d ce %06x ae %06x tref %03x (map %u coord %u texen %u chsel %u) kc %u ka %u kind %d tex %d\n", st, S.bp[0xC0 + 2 * st], S.bp[0xC1 + 2 * st], tref & 0xFFF,
           tref & 7, (tref >> 3) & 7, (tref >> 6) & 1, (tref >> 7) & 7, (ksel >> ((st & 1) ? 14 : 4)) & 0x1F, (ksel >> ((st & 1) ? 19 : 9)) & 0x1F,
           st < STAGES ? d->stage_kind[st] : -1, st < STAGES ? d->stage_tex[st] : -1);
  }
  for (int i = 0; i < 4; i++) printf("  reg%d %06x %06x konst%d %06x %06x\n", i, tev_raw[i][0], tev_raw[i][1], i, konst_raw[i][0], konst_raw[i][1]);
  if (d->tex >= 0) {
    const Tex *t = &texs[d->tex];
    double sum[4] = {0, 0, 0, 0}; uint32_t nt = t->w * t->h;
    for (uint32_t k = 0; k < nt; k++) for (int c = 0; c < 4; c++) sum[c] += t->rgba[4 * k + c];
    printf("  tex %d addr %08x fmt %u %ux%u wrap %u tlut %08x mean %.0f %.0f %.0f a %.0f\n", d->tex, t->addr, t->fmt, t->w, t->h, t->wrap, t->tlut_hash, sum[0] / nt, sum[1] / nt, sum[2] / nt, sum[3] / nt);
  } else printf("  tex none (texmap %d)\n", d->texmap);
  printf("  v0 chan0 %.2f %.2f %.2f %.2f chan1 %.2f %.2f %.2f %.2f col %.2f %.2f %.2f %.2f uv %.3f %.3f\n",
         vs[0].chan[0].r, vs[0].chan[0].g, vs[0].chan[0].b, vs[0].chan[0].a, vs[0].chan[1].r, vs[0].chan[1].g, vs[0].chan[1].b, vs[0].chan[1].a,
         vs[0].col.r, vs[0].col.g, vs[0].col.b, vs[0].col.a, vs[0].u, vs[0].v);
  int black = 0, first = -1; float lo = 9, hi = -9;
  for (int i = 0; i < n; i++) {
    float m = (vs[i].col.r + vs[i].col.g + vs[i].col.b) / 3;
    if (m < lo) lo = m;
    if (m > hi) hi = m;
    if (m < 0.02f) { black++; if (first < 0) first = i; }
  }
  printf("  cols mean %.2f..%.2f black %d/%d", lo, hi, black, n);
  if (first >= 0) printf("  first black v%d chan0 %.2f %.2f %.2f %.2f chan1 %.2f %.2f %.2f %.2f uv %.3f %.3f", first,
                         vs[first].chan[0].r, vs[first].chan[0].g, vs[first].chan[0].b, vs[first].chan[0].a,
                         vs[first].chan[1].r, vs[first].chan[1].g, vs[first].chan[1].b, vs[first].chan[1].a, vs[first].u, vs[first].v);
  printf("\n");
  /* the stages one by one, for v0 (and the first black vertex), with the white texture */
  for (int which = 0; which < (first > 0 ? 2 : 1); which++) {
    const Vtx *v = &vs[which ? first : 0];
    C4 reg[4]; memcpy(reg, tev_regs, sizeof reg);
    printf("  stages v%d:", which ? first : 0);
    for (int st = 0; st < d->ntev; st++) {
      DrawSetup one = *d; one.ntev = st + 1;
      C4 o = tev(&one, v->chan, NULL, v->stex);
      printf(" [%d] %.2f %.2f %.2f %.2f", st, o.r, o.g, o.b, o.a);
    }
    printf("\n");
  }
}

static int draw(Rd *r, int op) {
  int prim = op & 0xF8, vat = op & 7;
  if (!rd_ok(r, 2)) return 0;
  int n = be16(r->p); r->p += 2;
  DrawSetup d;
  if (!span_want) {   /* state only: step over the vertices (their size is fixed by the vertex format) */
    uint32_t size = vertex_size(vat) * (uint32_t)n;
    if (!rd_ok(r, size)) return 0;
    r->p += size;
    return 1;
  }
  setup_draw(&d);
  static Vtx *vs; static int capvs;
  if (n > capvs) { Vtx *p = realloc(vs, (size_t)n * sizeof *p); if (!p) return 0; vs = p; capvs = n; }
  uint32_t vsz = vertex_size(vat);
  const uint8_t *first = r->p;
  if ((S.bp[0x41] & 0x18) == 0) return skip_draw(r, first, vsz, n, 0);   /* colour and alpha updates off: draws nothing */
  for (int i = 0; i < n; i++) {
    const uint8_t *at = r->p;
    if (vsz && vsz <= 64 && rd_ok(r, vsz)) {   /* the same vertex bytes under the same state give the same vertex */
      uint32_t h = fnv(at, vsz) ^ (uint32_t)vat * 0x9E3779B9u, slot = h & (VCACHE - 1);
      VCacheEntry *e = &vcache[slot];
      if (e->epoch == state_epoch && e->hash == h && e->size == vsz && memcmp(e->bytes, at, vsz) == 0) {
        vs[i] = e->v; r->p += vsz; continue;
      }
      if (!read_vertex(r, vat, &d, &vs[i])) return skip_draw(r, first, vsz, n, 1);
      if ((uint32_t)(r->p - at) != vsz) { size_mismatches++; continue; }   /* the skip path must agree */
      e->epoch = state_epoch; e->hash = h; e->size = vsz; memcpy(e->bytes, at, vsz); e->v = vs[i];
      continue;
    }
    if (!read_vertex(r, vat, &d, &vs[i])) return skip_draw(r, first, vsz, n, 1);
    if ((uint32_t)(r->p - at) != vsz) size_mismatches++;
  }
  trace_draw(&d, vs, n, prim);
  if (span_want && d.tex >= 0 && n > 0 && d.blend != MC_GX_OPAQUE && uniform_inputs(&d, vs, n)) bake(&d, vs, n);   /* effects; opaque bodies are texture x colour */
  switch (prim) {
    case 0x80: for (int i = 0; i + 3 < n; i += 4) { emit_tri(&vs[i], &vs[i + 1], &vs[i + 2], &d); emit_tri(&vs[i], &vs[i + 2], &vs[i + 3], &d); } break;
    case 0x90: for (int i = 0; i + 2 < n; i += 3) emit_tri(&vs[i], &vs[i + 1], &vs[i + 2], &d); break;
    case 0x98: for (int i = 0; i + 2 < n; i++) { if (i & 1) emit_tri(&vs[i + 1], &vs[i], &vs[i + 2], &d); else emit_tri(&vs[i], &vs[i + 1], &vs[i + 2], &d); } break;
    case 0xA0: for (int i = 1; i + 1 < n; i++) emit_tri(&vs[0], &vs[i], &vs[i + 1], &d); break;
    default: break;   /* lines, points */
  }
  return 1;
}

/* ---- command processor ---- */
static void load_cp(uint32_t reg, uint32_t v) {
  state_epoch++;
  switch (reg & 0xF0) {
    case 0x30: S.matidx_a = v; break;
    case 0x40: S.matidx_b = v; break;
    case 0x50: S.vcd_lo = v; break;
    case 0x60: S.vcd_hi = v; break;
    case 0x70: S.vat_a[reg & 7] = v; break;
    case 0x80: S.vat_b[reg & 7] = v; break;
    case 0x90: S.vat_c[reg & 7] = v; break;
    case 0xA0: S.base[reg & 0xF] = v; break;
    case 0xB0: S.stride[reg & 0xF] = v & 0xFF; break;
  }
}
static void load_xf(uint32_t addr, uint32_t v) {
  state_epoch++;
  if (addr < 0x800) S.xf[addr] = v;
  else if (addr >= 0x1000 && addr < 0x1100) S.xfr[addr - 0x1000] = v;
}
static void load_bp(uint32_t w) {
  state_epoch++;
  uint32_t reg = w >> 24, v = w & 0xFFFFFF;
  if (reg == 0xFE) { S.bpmask = v; return; }
  if (S.bpmask != 0xFFFFFF) { v = (S.bp[reg] & ~S.bpmask) | (v & S.bpmask); S.bpmask = 0xFFFFFF; }
  S.bp[reg] = v;
  if (reg == 0x65) {   /* load TLUT: from main memory (BP 0x64) into TMEM */
    uint32_t src = (S.bp[0x64] & 0xFFFFFF) << 5, dst = (v & 0x3FF) << 9, n = ((v >> 10) & 0x7FF) * 32;
    const uint8_t *p = phys(src, n);
    if (p && dst + n <= sizeof S.tmem) memcpy(S.tmem + dst, p, n);
  }
  if (reg >= 0xE0 && reg <= 0xE7) {   /* bit 23: a konst colour (K0..K3) rather than a TEV register */
    int i = (int)(reg - 0xE0) / 2, w2 = (int)(reg & 1);
    if (v & 0x800000) konst_raw[i][w2] = v; else tev_raw[i][w2] = v;
  }
}

static int run(Rd *r);

static int command(Rd *r) {
  if (!rd_ok(r, 1)) return 0;
  int op = *r->p++;
  if (op == 0x00 || op == 0x44 || op == 0x48) return 1;
  if (op == 0x08) { if (!rd_ok(r, 5)) return 0; load_cp(r->p[0], be32(r->p + 1)); r->p += 5; return 1; }
  if (op == 0x10) {
    if (!rd_ok(r, 4)) return 0;
    uint32_t h = be32(r->p); r->p += 4;
    uint32_t n = (h >> 16) + 1, addr = h & 0xFFFF;
    if (!rd_ok(r, 4 * n)) return 0;
    for (uint32_t i = 0; i < n; i++) load_xf(addr + i, be32(r->p + 4 * i));
    r->p += 4 * n;
    return 1;
  }
  if (op == 0x20 || op == 0x28 || op == 0x30 || op == 0x38) {
    if (!rd_ok(r, 4)) return 0;
    uint32_t h = be32(r->p); r->p += 4;
    int array = 12 + (op - 0x20) / 8;
    uint32_t idx = h >> 16, n = ((h >> 12) & 0xF) + 1, addr = h & 0xFFF;
    const uint8_t *p = phys(S.base[array] + idx * S.stride[array], 4 * n);
    if (p) for (uint32_t i = 0; i < n; i++) load_xf(addr + i, be32(p + 4 * i));
    return 1;
  }
  if (op == 0x40) {
    if (!rd_ok(r, 8)) return 0;
    uint32_t addr = be32(r->p), size = be32(r->p + 4);
    r->p += 8;
    const uint8_t *p = phys(addr, size);
    if (p && depth_dl < 4) { depth_dl++; Rd dl = {p, p + size}; run(&dl); depth_dl--; }
    return 1;
  }
  if (op == 0x61) { if (!rd_ok(r, 4)) return 0; load_bp(be32(r->p)); r->p += 4; return 1; }
  if (op >= 0x80 && op <= 0xBF) return draw(r, op);
  return 0;   /* unknown: stop this stream */
}

static int run(Rd *r) { while (r->p < r->end) if (!command(r)) return 0; return 1; }

/* ---- the atlas: fighters' and items' ordinary textures (not baked ones) share one texture, so a sorted frame needs few runs. Each
 * texture gets a cell of 3x3 copies laid out by its wrap mode (texture coordinates -1..2 stay exact) and a 1-texel
 * border. */
#define ATLAS 4096
static uint8_t *atlas;
static uint32_t atlas_gen;
static int shelf_x, shelf_y, shelf_h;

static int dirty, dirty_x0, dirty_y0, dirty_x1, dirty_y1;   /* atlas texels changed since mc_gx_atlas_dirty */
static void dirty_mark(int x, int y, int w, int h) {
  if (!dirty) { dirty = 1; dirty_x0 = x; dirty_y0 = y; dirty_x1 = x + w; dirty_y1 = y + h; return; }
  if (x < dirty_x0) dirty_x0 = x;
  if (y < dirty_y0) dirty_y0 = y;
  if (x + w > dirty_x1) dirty_x1 = x + w;
  if (y + h > dirty_y1) dirty_y1 = y + h;
}

int mc_gx_atlas_dirty(uint32_t *x, uint32_t *y, uint32_t *w, uint32_t *h) {
  if (!dirty) return 0;
  *x = (uint32_t)dirty_x0; *y = (uint32_t)dirty_y0; *w = (uint32_t)(dirty_x1 - dirty_x0); *h = (uint32_t)(dirty_y1 - dirty_y0);
  dirty = 0;
  return 1;
}

static int wrap_i(int i, int n, int mode) {
  if (mode == 1) return ((i % n) + n) % n;
  if (mode == 2) { int p = ((i % (2 * n)) + 2 * n) % (2 * n); return p < n ? p : 2 * n - 1 - p; }
  return i < 0 ? 0 : i >= n ? n - 1 : i;
}

static int atlas_place(Tex *t) {
  int cw = 3 * (int)t->w + 2, ch = 3 * (int)t->h + 2;
  if (t->w > 256 || t->h > 256) return -1;
  if (!atlas) { atlas = calloc((size_t)ATLAS * ATLAS, 4); if (!atlas) return -1; }
  if (shelf_x + cw > ATLAS) { shelf_x = 0; shelf_y += shelf_h; shelf_h = 0; }
  if (shelf_y + ch > ATLAS) return -1;
  t->ax = shelf_x; t->ay = shelf_y;
  shelf_x += cw; if (ch > shelf_h) shelf_h = ch;
  int ws = (int)(t->wrap & 3), wt = (int)((t->wrap >> 2) & 3), w = (int)t->w, h = (int)t->h;
  for (int y = 0; y < ch; y++)
    for (int x = 0; x < cw; x++) {
      int sx = wrap_i(x - 1 - w, w, ws), sy = wrap_i(y - 1 - h, h, wt);   /* the middle copy starts at (w+1, h+1) */
      memcpy(atlas + 4 * ((size_t)(t->ay + y) * ATLAS + (size_t)(t->ax + x)), t->rgba + 4 * ((size_t)sy * (size_t)w + (size_t)sx), 4);
    }
  atlas_gen++;
  dirty_mark(t->ax, t->ay, cw, ch);
  return 1;
}

/* a triangle's texture coordinates moved by whole wrap periods into the atlas cell's -1..2 (repeat: 1, mirror: 2);
 * false when they span more than the cell (it keeps its own texture) */
static int fit_cell(float *c0, float *c1, float *c2, int wrap) {
  float lo = fminf(*c0, fminf(*c1, *c2)), hi = fmaxf(*c0, fmaxf(*c1, *c2));
  if (wrap == 1 || wrap == 2) {
    float period = wrap == 1 ? 1.0f : 2.0f, shift = floorf(lo / period) * period;
    *c0 -= shift; *c1 -= shift; *c2 -= shift; lo -= shift; hi -= shift;
  }
  return wrap == 0 || (lo >= -1 && hi <= 2);
}

static void to_atlas(Tri *tr) {
  if (tr->tex < 0 || (tr->p_link != 8 && tr->p_link != 9)) return;   /* fighters and items (models): effects keep their own (few, changing) textures */
  Tex *t = &texs[tr->tex];
  if (t->baked) return;
  if (t->atlas == 0) t->atlas = atlas_place(t);
  if (t->atlas != 1) return;
  float us[3] = {tr->v[0].u, tr->v[1].u, tr->v[2].u}, vs[3] = {tr->v[0].v, tr->v[1].v, tr->v[2].v};
  if (!fit_cell(&us[0], &us[1], &us[2], (int)(t->wrap & 3)) || !fit_cell(&vs[0], &vs[1], &vs[2], (int)((t->wrap >> 2) & 3))) return;
  for (int i = 0; i < 3; i++) {
    float u = clampf(us[i], -1, 2), v = clampf(vs[i], -1, 2);   /* clamp wrap: the cell's border copies hold the edge */
    tr->v[i].u = (t->ax + 1 + (u + 1) * t->w) / ATLAS;
    tr->v[i].v = (t->ay + 1 + (v + 1) * t->h) / ATLAS;
  }
  tr->tex = MC_GX_ATLAS;
}

/* Draw order without a depth buffer, after Melee's z modes (BP 0x40):
 *  layer 1: solid depth-tested triangles, each by its own depth (Fox's body); a solid draw that doesn't write depth
 *    (a second pass over the same body) kept whole at its mean depth, so it lands after the pass it covers;
 *  layer 0 / 2: translucent depth-tested draws (most effects), each kept whole and placed by its mean depth, behind
 *    the solid ones when it lies wholly behind their mean depth, else in front;
 *  layer 3: draws whose depth test always passes (the shield bubble, the shine's outline): over everything, in
 *    Melee's order.
 * Keeping a draw whole also keeps the runs few (one per draw, not one per interleaved triangle). */
typedef struct { int layer; float depth; int index; } Key;
static int cmp_key(const void *a, const void *b) {
  const Key *x = a, *y = b;
  if (x->layer != y->layer) return x->layer - y->layer;
  if (x->depth != y->depth) return x->depth < y->depth ? -1 : 1;   /* view z: more negative is farther: drawn first */
  return x->index - y->index;                                       /* stable: equal depths keep Melee's order */
}
static int z_always(uint8_t zmode) { return !(zmode & 1) || ((zmode >> 1) & 7) == 7; }

static void order_keys(Key *k) {
  double solid = 0; int nsolid = 0;
  for (int i = 0; i < ntris; i++)
    if (tris[i].blend == MC_GX_OPAQUE && !z_always(tris[i].zmode)) { solid += tris[i].depth; nsolid++; }
  float mid = nsolid ? (float)(solid / nsolid) : 0;
  for (int i = 0, end; i < ntris; i = end) {
    const Tri *t = &tris[i];
    end = i + 1;   /* one draw: the same object, texture, blend and z mode, in a row */
    while (end < ntris && tris[end].gobj == t->gobj && tris[end].tex == t->tex && tris[end].blend == t->blend && tris[end].zmode == t->zmode) end++;
    if (z_always(t->zmode)) { for (int j = i; j < end; j++) k[j] = (Key){3, 0, j}; continue; }
    if (t->blend == MC_GX_OPAQUE && (t->zmode & 0x10)) { for (int j = i; j < end; j++) k[j] = (Key){1, tris[j].depth, j}; continue; }
    if (t->blend == MC_GX_OPAQUE) {   /* depth-tested but not written (G&W's second pass over his body): whole, at its mean depth, in Melee's order after what it covers */
      float sum = 0;
      for (int j = i; j < end; j++) sum += tris[j].depth;
      for (int j = i; j < end; j++) k[j] = (Key){1, sum / (float)(end - i), j};
      continue;
    }
    float sum = 0, far = -1e30f;
    for (int j = i; j < end; j++) { sum += tris[j].depth; far = fmaxf(far, tris[j].depth); }
    int layer = nsolid && far < mid ? 0 : 2;
    for (int j = i; j < end; j++) k[j] = (Key){layer, sum / (float)(end - i), j};
  }
}
static Key *keys; static int cap_keys;

static void init_live(void) {
  memset(&live, 0, sizeof live);
  live.bpmask = 0xFFFFFF;
  /* loaded once by GXInit at boot, before any capture: GX_IDENTITY (texture matrix 60) and GX_PTIDENTITY (post 125) */
  static const float id[12] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0};
  for (int i = 0; i < 12; i++) { memcpy(&live.xf[60 * 4 + i], &id[i], 4); memcpy(&live.xf[0x500 + 61 * 4 + i], &id[i], 4); }
  /* and dual texgen on (XF 0x1012): HSD's texture matrices (scroll, scale, a mirrored quarter's x2) are post matrices */
  live.xfr[0x12] = 1;
  memcpy(&start, &live, sizeof start);
  live_ready = 1;
}

/* every captured frame, at its end: the state it leaves becomes `live`; `start` keeps where it began */
void gx_track_frame(const uint8_t *fifo, uint32_t len, const uint8_t *mem) {
  if (!live_ready) init_live();
  M = mem;
  memcpy(&start, &live, sizeof start);
  memcpy(&S, &live, sizeof S);
  span_want = 0;
  /* in the same pieces as a decode (each object's part), so one unreadable command loses only the rest of its piece */
  Machine *m = g_m;
  int k = m->gx_cur, nmk = m->gx_nmarks[k];
  uint32_t at = 0;
  for (int i = -1; i < nmk; i++) {
    uint32_t end = i + 1 < nmk ? m->gx_marks[k][i + 1].offset : len;
    if (end > len) end = len;
    if (end > at) { Rd r = {fifo + at, fifo + end}; run(&r); }
    if (end > at) at = end;
  }
  memcpy(&live, &S, sizeof live);
}

int mc_gx_decode(uint64_t plink_mask, const McGxVert **verts, int *nverts, const McGxRun **runs, int *nruns) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  const uint8_t *fifo; uint32_t len; const McGxMark *mk; int nmk;
  mc_gx_frame(&fifo, &len, &mk, &nmk);
  M = m->cpu.mem;
  if (!live_ready) init_live();
  memcpy(&S, &start, sizeof S);
  ntris = 0;
  want_mask = plink_mask;
  decode_no++;
  /* run the stream in pieces between marks, so each piece knows its object */
  uint32_t at = 0;
  for (int i = -1; i < nmk; i++) {
    uint32_t end = i + 1 < nmk ? mk[i + 1].offset : len;
    if (i >= 0) {
      span_gobj = mk[i].gobj; span_plink = mk[i].p_link;
      span_want = mk[i].gobj && mk[i].p_link < 64 && ((plink_mask >> mk[i].p_link) & 1)
                  && !(m->standin_gobj && (mk[i].gobj == m->standin_gobj || mk[i].owner == m->standin_gobj));   /* plan 7 */
      if (span_want) { invert_view(mk[i].view, inv_view); state_epoch++; }   /* cached vertices are in world space */
    } else span_want = 0;
    if (end > at) { Rd r = {fifo + at, fifo + end}; run(&r); }
    at = end > at ? end : at;
  }
  for (int i = 0; i < ntris; i++) to_atlas(&tris[i]);
  if (ntris > cap_keys) { Key *p = realloc(keys, (size_t)ntris * sizeof *p); if (!p) return -1; keys = p; cap_keys = ntris; }
  order_keys(keys);
  qsort(keys, (size_t)ntris, sizeof *keys, cmp_key);
  if (3 * ntris > cap_v) { McGxVert *p = realloc(out_v, (size_t)(3 * ntris) * sizeof *p); if (!p) return -1; out_v = p; cap_v = 3 * ntris; }
  int nr = 0;
  for (int i = 0; i < ntris; i++) {
    const Tri *t = &tris[keys[i].index];
    memcpy(&out_v[3 * i], t->v, sizeof t->v);
    if (nr == 0 || out_r[nr - 1].tex != t->tex || out_r[nr - 1].blend != t->blend) {
      if (nr == cap_r) { int cap = cap_r ? cap_r * 2 : 256; McGxRun *p = realloc(out_r, (size_t)cap * sizeof *p); if (!p) return -1; out_r = p; cap_r = cap; }
      out_r[nr++] = (McGxRun){(uint32_t)(3 * i), 0, t->tex, t->blend, t->p_link, 0, 0, t->gobj};
    }
    out_r[nr - 1].count += 3;
  }
  for (int i = 0; i < nr; i++) if (out_r[i].tex >= 0 && out_r[i].tex != MC_GX_ATLAS) {
    uint32_t wrap = texs[out_r[i].tex].wrap;
    out_r[i].wrap_s = (uint8_t)(wrap & 3); out_r[i].wrap_t = (uint8_t)((wrap >> 2) & 3);
  }
  *verts = out_v; *nverts = 3 * ntris; *runs = out_r; *nruns = nr;
  return 0;
}

int mc_gx_texture(int32_t id, McGxTexture *out) {
  if (id == MC_GX_ATLAS) {
    if (!atlas) return -1;
    out->w = out->h = ATLAS; out->rgba = atlas; out->gen = atlas_gen;
    return 0;
  }
  if (id < 0 || id >= ntex) return -1;
  if (!texs[id].rgba) return -1;
  out->w = texs[id].w; out->h = texs[id].h; out->rgba = texs[id].rgba; out->gen = texs[id].gen;
  return 0;
}

void gx_draw_free(void) {
  for (int i = 0; i < ntex; i++) free(texs[i].rgba);
  free(texs); texs = NULL; ntex = captex = 0;
  live_ready = 0;
  free(atlas); atlas = NULL; shelf_x = shelf_y = shelf_h = 0; atlas_gen++; dirty = 0;
  free(tris); tris = NULL; ntris = captris = 0;
  free(out_v); out_v = NULL; cap_v = 0;
  free(out_r); out_r = NULL; cap_r = 0;
  free(keys); keys = NULL; cap_keys = 0;
}

uint32_t mc_gx_size_mismatches(void) { return size_mismatches; }

uint32_t mc_gx_decode_errors(void) { return decode_errors; }

