/* melee_core.c - public API: boot, step, inspect. */
#include <stdarg.h>
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
/* Resume the guest until its next frame boundary, with the guest's MXCSR swapped in. */
static int run_frame(void) {
  Machine *m = g_m;
  if (!m) { snprintf(init_error, sizeof init_error, "libmelee_core is not initialised"); return -1; }
  if (m->dead) return -1;
  m->error[0] = 0;                      /* a live machine's message only describes the last call */
  unsigned host = _mm_getcsr();
  _mm_setcsr(m->guest_csr);
  coro_resume(m->guest);
  m->guest_csr = _mm_getcsr();
  _mm_setcsr(host);
  return m->dead ? -1 : 0;
}

int mc_init(const char *mem1_path, const char *iso_path) {
  mc_shutdown();
  init_error[0] = 0;
  Machine *m = calloc(1, sizeof *m);
  if (!m) return init_fail("out of memory");
  g_m = m;
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
  coro_free(m->guest);
  if (m->iso) fclose(m->iso);
  free(m->cpu.mem);
  free(m->aram);
  free(m);
  g_m = NULL;
}
const char *mc_error(void) { return g_m && g_m->error[0] ? g_m->error : init_error; }
int mc_step(void) { return run_frame(); }
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

int mc_start_match(const uint8_t start[MC_START_SIZE], uint32_t seed) {
  Machine *m = g_m;
  if (!m) return run_frame();
  if (m->dead) return -1;
  if (m->in_match || m->start_req) { snprintf(m->error, sizeof m->error, "a match was already started"); return -1; }
  memcpy(m->start, start, MC_START_SIZE);
  m->seed = seed;
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
  return 0;
}
