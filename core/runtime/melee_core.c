/* melee_core.c - public API: boot, step, inspect. */
#include <stdarg.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <xmmintrin.h>
#include "melee_core.h"
#include "mc_internal.h"
#include "guest_table.h"
#include "guest_meta.h"

static char init_error[300];   /* room for "boot failed: " + Machine.error */

static uint32_t crc32_buf(const uint8_t *p, size_t n) {
  uint32_t c = 0xFFFFFFFFu;
  while (n--) {
    c ^= *p++;
    for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1u)));
  }
  return ~c;
}
static uint32_t be32(const uint8_t *p) { return (uint32_t)p[0] << 24 | (uint32_t)p[1] << 16 | (uint32_t)p[2] << 8 | p[3]; }

static int init_fail(const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(init_error, sizeof init_error, fmt, ap);
  va_end(ap);
  mc_shutdown();
  return -1;
}
static void guest_main(void *arg) {
  (void)arg;
  guest_lookup(GUEST_ENTRY)(&g_m->cpu);
  mc_die("__start returned");
}
/* Resume the guest until its next frame boundary, with the guest's MXCSR swapped in (on the worker). */
static void resume_on_worker(void *arg) {
  Machine *m = arg;
  unsigned host = _mm_getcsr();
  _mm_setcsr(m->guest_csr);
  coro_resume(m->guest);
  m->guest_csr = _mm_getcsr();
  _mm_setcsr(host);
}

static int run_frame(void) {
  Machine *m = g_m;
  if (!m) { snprintf(init_error, sizeof init_error, "libmelee_core is not initialised"); return -1; }
  if (m->dead) return -1;
  m->error[0] = 0;                      /* a live machine's message only describes the last call */
  worker_call(resume_on_worker, m);     /* guest code runs on the core's own thread (worker.c) */
  return m->dead ? -1 : 0;
}

int mc_init(const char *mem1_path, const char *iso_path) {
  mc_shutdown();
  init_error[0] = 0;
  gekko_slippi_nmsub = 1;   /* the reference is Slippi Dolphin's JIT: zero signs of nmsub (gekko_fp.h) */
  Machine *m = calloc(1, sizeof *m);
  if (!m) return init_fail("out of memory");
  g_m = m;
  sfx_reset();
  Cpu *c = &m->cpu;
  c->mem = malloc(MEM1_SIZE);
  m->aram = calloc(1, ARAM_SIZE);
  if (!c->mem || !m->aram) return init_fail("out of memory");
  FILE *f = fopen(mem1_path, "rb");
  if (!f) return init_fail("cannot open %s", mem1_path);
  size_t n = fread(c->mem, 1, MEM1_SIZE, f);
  int extra = fgetc(f) != EOF;
  fclose(f);
  if (n != MEM1_SIZE || extra) return init_fail("%s is not a %u-byte MEM1 image", mem1_path, MEM1_SIZE);
  if (crc32_buf(c->mem, MEM1_SIZE) != GUEST_MEM1_CRC32) return init_fail("%s does not match this build (regenerate it)", mem1_path);
  uint8_t hdr[0x440];
  m->iso = fopen(iso_path, "rb");
  if (!m->iso || fread(hdr, 1, sizeof hdr, m->iso) != sizeof hdr) return init_fail("cannot open %s", iso_path);
  if (memcmp(hdr, "GALE01", 6) != 0 || hdr[7] != 2) return init_fail("%s is not Melee NTSC 1.02 (GALE01 revision 2)", iso_path);
  /* What the IPL/apploader leave in low memory (values as Dolphin's BS2 emulation writes them). */
  memcpy(c->mem, hdr, 0x20);                                   /* disc ID */
  uint32_t fst_off = be32(hdr + 0x424), fst_size = be32(hdr + 0x428);
  uint32_t fst_addr = (0x81800000u - fst_size) & ~31u;
  if (fst_size > 0x100000u || fseek(m->iso, (long)fst_off, SEEK_SET) != 0
      || fread(c->mem + (fst_addr & 0x01FFFFFFu), 1, fst_size, m->iso) != fst_size)
    return init_fail("cannot read the file table of %s", iso_path);
  st32(c, 0x80000020u, 0x0D15EA5Eu); st32(c, 0x80000024u, 1);
  st32(c, 0x80000028u, MEM1_SIZE);   st32(c, 0x8000002Cu, 3);
  st32(c, 0x80000030u, 0);           st32(c, 0x80000034u, fst_addr);
  st32(c, 0x80000038u, fst_addr);    st32(c, 0x8000003Cu, fst_size);
  st32(c, 0x800000CCu, 0);           st32(c, 0x800000F0u, MEM1_SIZE);
  st32(c, 0x800000F8u, 162000000u);  st32(c, 0x800000FCu, 486000000u);
  c->msr = 0x00002032u;
  c->spr[SPR_HID2] = 0xE0000000u;
  c->spr[SPR_LR] = HOST_RETURN;
  m->guest_csr = 0x1F80u;
  for (int i = 0; i < 4; i++) m->pad[i][10] = i == 0 ? 0 : 0xFF;   /* port 1 connected and idle */
  m->guest = coro_new(guest_main, NULL, GUEST_STACK);
  if (!m->guest) return init_fail("out of memory");
  if (run_frame() != 0) {                                      /* boot to the first frame boundary */
    snprintf(init_error, sizeof init_error, "boot failed: %s", m->error);
    return -1;
  }
  return 0;
}

void mc_shutdown(void) {
  Machine *m = g_m;
  if (!m) return;
  worker_stop();
  coro_free(m->guest);
  if (m->iso) fclose(m->iso);
  gx_free(m);
  gx_draw_free();
  free(m->cpu.mem);
  free(m->aram);
  free(m->room_verts);
  free(m->room_lines);
  free(m->room_joints);
  free(m);
  g_m = NULL;
}
const char *mc_error(void) { return g_m && g_m->error[0] ? g_m->error : init_error; }
int mc_step(void) { return run_frame(); }
int mc_step_to_hits(void) {
  Machine *m = g_m;
  if (m && !m->dead && m->at_hits) return 0;
  if (m) m->hit_pause = 1;
  int rc = run_frame();
  if (m && !m->dead) m->hit_pause = 0;
  return rc < 0 ? -1 : g_m->at_hits ? 0 : 1;
}
uint64_t mc_frame(void) { return g_m ? g_m->frames : 0; }
uint32_t mc_mem_crc32(void) { return g_m ? crc32_buf(g_m->cpu.mem, MEM1_SIZE) : 0; }
static int in_mem1(uint32_t ea, uint32_t n) { return ea >= 0x80000000u && ea - 0x80000000u <= MEM1_SIZE - n; }
uint32_t mc_peek32(uint32_t ea) { return g_m && in_mem1(ea, 4) ? ld32(&g_m->cpu, ea) : 0; }

void mc_set_input(int port, const McInput *in) {
  if (!g_m || port < 0 || port > 3) return;
  uint8_t *p = g_m->pad[port];
  memset(p, 0, 12);
  if (!in) { p[10] = 0xFF; return; }       /* err = PAD_ERR_NO_CONTROLLER */
  p[0] = (uint8_t)(in->buttons >> 8);
  p[1] = (uint8_t)in->buttons;
  p[2] = (uint8_t)in->stick_x;
  p[3] = (uint8_t)in->stick_y;
  p[4] = (uint8_t)in->cstick_x;
  p[5] = (uint8_t)in->cstick_y;
  p[6] = in->trigger_l;
  p[7] = in->trigger_r;
}

int mc_start_match(const uint8_t start[MC_START_SIZE], uint32_t seed, uint32_t flags) {
  Machine *m = g_m;
  if (!m) return run_frame();
  if (m->dead) return -1;
  if (m->in_match || m->start_req) { snprintf(m->error, sizeof m->error, "a match was already started"); return -1; }
  memcpy(m->start, start, MC_START_SIZE);
  m->seed = seed;
  m->online = (flags & MC_ONLINE_RNG) != 0;
  m->start_req = 1;
  for (int n = 0; !m->in_match; n++) {
    if (n == 600) {
      snprintf(m->error, sizeof m->error, "match did not start within 600 frames");
      m->dead = 1;
      return -1;
    }
    if (run_frame() != 0) return -1;
  }
  return 0;
}

/* Solo Fox on Final Destination: an untimed time match (one-player stock matches end at once).
 * Rules bytes as Slippi records them for a 4-stock FD match, with match_kind = time, timer off. */
void mc_solo_fox_start(uint8_t out[MC_START_SIZE]) {
  static const char rules[] =
      "1001 8e4c c300 0000 0000 00ff ff6e 0020 0000 01e0 0000 0000 0000 0000 0000 0000"
      "ffff fff8 0000 000f 0000 0000 3f80 0000 3f80 0000 3f80 0000 0000 0000 0000 0000";
  static const char fox[] = "020004000000000009007800c000040100000000000000003f8000003f8000003f800000";
  static const char none[] = "1503040000ff000009007800c000040100000000000000003f8000003f8000003f800000";
  static const char none45[] = "2103040000ff0000090078004000040000000000000000003f8000003f8000003f800000";
  memset(out, 0, MC_START_SIZE);
  int k = 0;
  for (const char *s = rules; *s; s++) {
    if (*s == ' ') continue;
    unsigned v;
    sscanf(s, "%2x", &v);
    out[k++] = (uint8_t)v;
    s++;
  }
  for (int i = 0; i < 6; i++) {
    const char *h = i == 0 ? fox : i < 4 ? none : none45;
    for (int j = 0; j < 0x24; j++) { unsigned v; sscanf(h + 2 * j, "%2x", &v); out[0x60 + 0x24 * i + j] = (uint8_t)v; }
  }
}

static float f32at(Cpu *c, uint32_t ea) { uint32_t v = ld32(c, ea); float f; memcpy(&f, &v, 4); return f; }
static void stf32(Cpu *c, uint32_t ea, float f) { uint32_t v; memcpy(&v, &f, 4); st32(c, ea, v); }
/* Fox's (any fighter's) joint world matrices, as Melee's render pass leaves them every frame. */
static int bones_walk(Cpu *c, uint32_t j, float (*out)[12], int max, int n, int depth) {
  for (; j && n < max && depth < 64; j = ld32(c, j + 0x08)) {
    if (!in_mem1(j, 0x88)) return n;
    for (int k = 0; k < 12; k++) out[n][k] = f32at(c, j + 0x44 + 4u * (uint32_t)k);
    n++;
    n = bones_walk(c, ld32(c, j + 0x10), out, max, n, depth + 1);
  }
  return n;
}

int mc_read_bones(int port, int follower, float out[][12], int max) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  uint32_t gobj = ld32(c, 0x80453080u + 0xE90u * (uint32_t)port + (follower ? 0xB4u : 0xB0u));
  if (!gobj || !in_mem1(gobj, 0x30)) return 0;
  return bones_walk(c, ld32(c, gobj + 0x28), out, max, 0, 0);
}

/* Each joint's mesh parts (HSD_DObj: next +4, flags +0x14, bit 0 = hidden), depth first like the bones. */
static int dobjs_walk(Cpu *c, uint32_t j, uint8_t *hidden, uint8_t *joint, int max, int n, int *ji, int depth) {
  for (; j && n < max && depth < 64; j = ld32(c, j + 0x08)) {
    if (!in_mem1(j, 0x88)) return n;
    int me = (*ji)++;
    if (!(ld32(c, j + 0x14) & 0x4020u))             /* not a particle or spline joint: u is the DObj list */
      for (uint32_t d = ld32(c, j + 0x18); d && n < max && in_mem1(d, 0x18); d = ld32(c, d + 0x04)) {
        hidden[n] = ld32(c, d + 0x14) & 1u;
        joint[n++] = (uint8_t)me;
      }
    n = dobjs_walk(c, ld32(c, j + 0x10), hidden, joint, max, n, ji, depth + 1);
  }
  return n;
}

int mc_read_dobjs(int port, int follower, uint8_t hidden[], uint8_t joint[], int max) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  uint32_t gobj = ld32(c, 0x80453080u + 0xE90u * (uint32_t)port + (follower ? 0xB4u : 0xB0u));
  if (!gobj || !in_mem1(gobj, 0x30)) return 0;
  int ji = 0;
  return dobjs_walk(c, ld32(c, gobj + 0x28), hidden, joint, max, 0, &ji, 0);
}

int mc_read_fighter(int port, int follower, McFighter *out) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  uint32_t block = 0x80453080u + 0xE90u * (uint32_t)port;   /* player static block */
  uint32_t gobj = ld32(c, block + (follower ? 0xB4u : 0xB0u));
  if (!gobj) return 1;
  if (!in_mem1(gobj, 0x30)) return -1;
  uint32_t fp = ld32(c, gobj + 0x2C);                       /* Fighter */
  if (!in_mem1(fp, 0x2000)) return -1;
  out->action = ld32(c, fp + 0x10);
  out->x = f32at(c, fp + 0xB0);
  out->y = f32at(c, fp + 0xB4);
  out->facing = f32at(c, fp + 0x2C);
  out->percent = f32at(c, fp + 0x1830);
  out->shield = f32at(c, fp + 0x1998);
  out->self_vel_x = f32at(c, fp + 0x80);
  out->self_vel_y = f32at(c, fp + 0x84);
  out->kb_vel_x = f32at(c, fp + 0x8C);
  out->kb_vel_y = f32at(c, fp + 0x90);
  out->ground_vel = f32at(c, fp + 0xEC);
  out->airborne = ld32(c, fp + 0xE0);
  out->hitlag = f32at(c, fp + 0x195C);
  return 0;
}

/* ---- combat (spec §5.1) ---- */
#define HIT_SENTINEL 0x80002F08u   /* the victim recorded for hits outside Melee: never a GObj, never dereferenced */

static uint32_t fighter_at(Cpu *c, int port, int follower) {
  uint32_t gobj = ld32(c, 0x80453080u + 0xE90u * (uint32_t)port + (follower ? 0xB4u : 0xB0u));
  if (!gobj || !in_mem1(gobj, 0x30)) return 0;
  uint32_t fp = ld32(c, gobj + 0x2C);
  return in_mem1(fp, 0x2000) ? fp : 0;
}

static int victim_recorded(Cpu *c, uint32_t hit) {
  for (uint32_t v = 0; v < 12; v++) if (ld32(c, hit + 0x74 + 8 * v) == HIT_SENTINEL) return 1;
  return 0;
}

/* Melee's items owned by a fighter (its lasers): Item+0x518 owner, hitboxes ItemHitbox[4] at +0x5D4 (0x13C each) */
#define ADDR_PLINK_HEADS_ 0x804D782Cu
#define ITEM_OWNER 0x518u
#define ITEM_HITBOXES 0x5D4u
#define ITEM_HITBOX_SIZE 0x13Cu
#define ITEM_LIFE 0xD44u
static uint32_t owned_item(Cpu *c, uint32_t gobj, uint32_t owner) {   /* the Item if `gobj` is an item of `owner`'s */
  uint32_t heads = ld32(c, ADDR_PLINK_HEADS_);
  if (!in_mem1(heads, 4 * 10)) return 0;
  for (uint32_t g = ld32(c, heads + 4 * 9), guard = 0; g && guard < 4096; g = ld32(c, g + 8), guard++) {
    if (!in_mem1(g, 0x30)) return 0;
    uint32_t it = ld32(c, g + 0x2C);
    if ((gobj == 0 || g == gobj) && in_mem1(it, ITEM_HITBOXES + 4 * ITEM_HITBOX_SIZE) && ld32(c, it + ITEM_OWNER) == owner) return it;
  }
  return 0;
}

static void read_capsule(Cpu *c, uint32_t h, uint32_t slot, uint32_t group, McHitbox *o);

int mc_read_hitboxes(int port, int follower, McHitbox out[], int max) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  uint32_t fp = fighter_at(c, port, follower);
  int n = 0;
  for (uint32_t s = 0; fp && s < 4 && n < max; s++) {
    uint32_t h = fp + 0x914 + 0x138 * s;
    if (ld32(c, h) == 0) continue;                      /* HitCapsule_Disabled */
    read_capsule(c, h, s, ld32(c, h + 4), &out[n++]);
  }
  /* the fighter's items (lasers): an item's hitboxes share one group, the item's GObj address (>= 0x80000000) */
  uint32_t gobj = fp ? ld32(c, 0x80453080u + 0xE90u * (uint32_t)port + (follower ? 0xB4u : 0xB0u)) : 0;
  uint32_t heads = ld32(c, ADDR_PLINK_HEADS_);
  if (gobj && in_mem1(heads, 4 * 10))
    for (uint32_t g = ld32(c, heads + 4 * 9), guard = 0; g && n < max && guard < 4096; g = ld32(c, g + 8), guard++) {
      if (!in_mem1(g, 0x30)) break;
      uint32_t it = ld32(c, g + 0x2C);
      if (!in_mem1(it, ITEM_HITBOXES + 4 * ITEM_HITBOX_SIZE) || ld32(c, it + ITEM_OWNER) != gobj) continue;
      for (uint32_t s = 0; s < 4 && n < max; s++) {
        uint32_t h = it + ITEM_HITBOXES + ITEM_HITBOX_SIZE * s;
        if (ld32(c, h) == 0) continue;
        read_capsule(c, h, 4 + s, g, &out[n++]);
      }
    }
  return n;
}

static void read_capsule(Cpu *c, uint32_t h, uint32_t slot, uint32_t group, McHitbox *o) {
  {
    o->slot = slot; o->group = group;
    o->damage = f32at(c, h + 0xC); o->radius = f32at(c, h + 0x1C);
    o->angle = (int32_t)ld32(c, h + 0x20); o->growth = (int32_t)ld32(c, h + 0x24);
    o->set_kb = (int32_t)ld32(c, h + 0x28); o->base_kb = (int32_t)ld32(c, h + 0x2C);
    o->element = ld32(c, h + 0x30);
    o->x = f32at(c, h + 0x4C); o->y = f32at(c, h + 0x50);
    o->prev_x = f32at(c, h + 0x58); o->prev_y = f32at(c, h + 0x5C);
    int any = 0;
    for (uint32_t v = 0; v < 12; v++) any |= ld32(c, h + 0x74 + 8 * v) != 0;
    o->fresh = !any;
    uint32_t b40 = ld8(c, h + 0x40), b42 = ld8(c, h + 0x42);   /* big-endian bitfields: b0 is the top bit */
    o->flags = (b40 & 0x20 ? MC_HIT_AIRBORNE : 0) | (b40 & 0x10 ? MC_HIT_GROUNDED : 0) | (b42 & 0x04 ? MC_HIT_FIGHTERS : 0);
  }
}

int mc_read_hurtboxes(int port, int follower, McHurtbox out[], int max) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  uint32_t fp = fighter_at(c, port, follower);
  if (!fp) return 0;
  uint32_t count = ld8(c, fp + 0x119E);
  int n = 0;
  for (uint32_t i = 0; i < count && i < 15 && n < max; i++) {
    uint32_t h = fp + 0x11A0 + 0x4C * i;
    McHurtbox *o = &out[n++];
    o->state = ld32(c, h); o->radius = f32at(c, h + 0x1C);
    o->ax = f32at(c, h + 0x28); o->ay = f32at(c, h + 0x2C);
    o->bx = f32at(c, h + 0x34); o->by = f32at(c, h + 0x38);
  }
  return n;
}

static void record_victim(Cpu *c, uint32_t h);
int mc_report_hit(int port, int follower, uint32_t group, float damage) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  uint32_t fp = fighter_at(c, port, follower);
  if (!fp) return 1;
  if (group >= 0x80000000u) {   /* one of the fighter's items (a laser): it ends as on a wall, Fox gets no hitlag */
    uint32_t gobj = ld32(c, 0x80453080u + 0xE90u * (uint32_t)port + (follower ? 0xB4u : 0xB0u));
    uint32_t it = owned_item(c, group, gobj);
    if (!it) return 1;
    for (uint32_t s = 0; s < 4; s++) record_victim(c, it + ITEM_HITBOXES + ITEM_HITBOX_SIZE * s);
    float half = f32at(c, ld32(c, 0x804D6D28u) + 0x4C);  /* it_80275158(item, 1): life 1, half-life 1 x it_804D6D28->x4C */
    stf32(c, it + ITEM_LIFE, 1.0f); stf32(c, it + ITEM_LIFE + 4, 1.0f * half);
    return 0;
  }
  int found = 0;
  for (uint32_t s = 0; s < 4; s++) {
    uint32_t h = fp + 0x914 + 0x138 * s;
    if (ld32(c, h) == 0 || ld32(c, h + 4) != group) continue;
    found = 1;
    record_victim(c, h);
  }
  if (!found) return 1;
  /* ftColl_80076ED8: the attacker's dealt damage as an int, at least 1 for any damage */
  int32_t d = damage != 0 ? ((int32_t)damage ? (int32_t)damage : 1) : 0;
  if (d > (int32_t)ld32(c, fp + 0x1914)) st32(c, fp + 0x1914, (uint32_t)d);
  return 0;
}

/* lbColl_80008688: the sentinel victim on one hitbox (if active and not recorded yet) */
static void record_victim(Cpu *c, uint32_t h) {
  {
    if (ld32(c, h) == 0 || victim_recorded(c, h)) return;
    uint32_t v = 0;
    while (v < 12 && ld32(c, h + 0x74 + 8 * v) != 0) v++;
    int full = v == 12;
    if (full) v = ld8(c, h + 0x44);                     /* full: overwrite at Melee's ring cursor and advance it */
    st32(c, h + 0x74 + 8 * v, HIT_SENTINEL);
    st32(c, h + 0x78 + 8 * v, 0);
    if (full) st8(c, h + 0x44, v + 1 >= 12 ? 0 : v + 1);
  }
}

/* ftCommon_CalcHitlag: (int)((int)(damage x ftCommonData+0x198 + +0x19C) x 1), capped at +0x194 */
int mc_hitlag_frames(int damage) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  Cpu *c = &m->cpu;
  uint32_t cd = ld32(c, 0x804D6554u);
  if (!in_mem1(cd, 0x1A0)) return -1;
  int frames = (int)(float)(int)((float)damage * f32at(c, cd + 0x198) + f32at(c, cd + 0x19C));
  float cap = f32at(c, cd + 0x194);
  return frames > cap ? (int)cap : frames;
}

/* ---- shield bubble ---- */
int mc_read_shield(int port, int follower, McShield *out) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  Cpu *c = &m->cpu;
  memset(out, 0, sizeof *out);
  uint32_t fp = fighter_at(c, port, follower);
  if (!fp) return 1;
  uint32_t action = ld32(c, fp + 0x10);
  /* GuardOn 178, Guard 179, GuardSetOff 181 (shield stun), GuardReflect 182; GuardOff 180 has dropped it */
  if (action < 178 || action > 182 || action == 180) return 0;
  uint32_t data = ld32(c, fp + 0x10C), x8 = in_mem1(data, 0x10) ? ld32(c, data + 8) : 0;
  uint32_t parts = ld32(c, fp + 0x5E8);
  if (!in_mem1(x8, 0x14) || !in_mem1(parts, 0x10)) return 0;
  uint32_t joint = ld32(c, parts + 0x10u * ld8(c, x8 + 0x11));
  if (!in_mem1(joint, 0x80)) return 0;
  uint32_t mt = joint + 0x44;                              /* HSD_JObj world matrix, 3x4 row-major */
  float a = f32at(c, mt), b = f32at(c, mt + 16), d = f32at(c, mt + 32);
  out->active = 1;
  out->x = f32at(c, mt + 12); out->y = f32at(c, mt + 28);
  out->radius = sqrtf(a * a + b * b + d * d);
  out->hard = f32at(c, fp + 0x199C);
  return 0;
}

/* ---- items ---- */
#define ADDR_PLINK_HEADS 0x804D782Cu   /* HSD_GObjPLinkHead: HSD_GObj *[] by p_link */
#define PLINK_ITEM 9u
int mc_read_items(McItem out[], int max) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  Cpu *c = &m->cpu;
  uint32_t heads = ld32(c, ADDR_PLINK_HEADS);
  if (!in_mem1(heads, 4 * (PLINK_ITEM + 1))) return 0;
  int n = 0;
  for (uint32_t g = ld32(c, heads + 4 * PLINK_ITEM), guard = 0; g && n < max && guard < 4096; g = ld32(c, g + 8), guard++) {
    if (!in_mem1(g, 0x30)) break;
    uint32_t it = ld32(c, g + 0x2C);                      /* Item */
    if (!in_mem1(it, 0x60)) continue;
    McItem *o = &out[n++];
    o->kind = ld32(c, it + 0x10);
    o->facing = f32at(c, it + 0x2C);
    o->vx = f32at(c, it + 0x40); o->vy = f32at(c, it + 0x44);
    o->x = f32at(c, it + 0x4C); o->y = f32at(c, it + 0x50);
  }
  return n;
}
