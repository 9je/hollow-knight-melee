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
