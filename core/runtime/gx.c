/* gx.c - GX capture: the frame's graphics command stream and which Melee object wrote each part (melee_core.h). */
#include <stdlib.h>
#include <string.h>
#include "mc_internal.h"
#include "melee_core.h"

_Static_assert(sizeof(McGxMark) == sizeof(struct McGxMarkI), "McGxMark layout");

int mc_gx_capture(int on) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  m->gx_on = on != 0;
  if (!m->gx_on) for (int k = 0; k < 2; k++) { m->gx_len[k] = 0; m->gx_nmarks[k] = 0; }
  return 0;
}

const uint8_t *mc_mem1(void) { return g_m ? g_m->cpu.mem : NULL; }

int mc_gx_frame(const uint8_t **fifo, uint32_t *len, const McGxMark **marks, int *nmarks) {
  Machine *m = g_m;
  if (!m || m->dead) return -1;
  int k = !m->gx_cur;
  *fifo = m->gx_fifo[k]; *len = m->gx_len[k];
  *marks = (const McGxMark *)m->gx_marks[k]; *nmarks = m->gx_nmarks[k];
  return 0;
}

/* a write of `size` bytes to the FIFO: the store lands at the end of this frame's stream */
uint8_t *gx_append(uint32_t size) {
  Machine *m = g_m;
  int k = m->gx_cur;
  if (m->gx_len[k] + size > m->gx_cap[k]) {
    uint32_t cap = m->gx_cap[k] ? m->gx_cap[k] * 2 : 1u << 20;
    uint8_t *p = realloc(m->gx_fifo[k], cap);
    if (!p) mc_die("out of memory (GX capture)");
    m->gx_fifo[k] = p; m->gx_cap[k] = cap;
  }
  uint8_t *at = m->gx_fifo[k] + m->gx_len[k];
  m->gx_len[k] += size;
  return at;
}

void gx_mark(Cpu *c, uint32_t gobj) {
  Machine *m = g_m;
  int k = m->gx_cur;
  if (m->gx_nmarks[k] == m->gx_capmarks[k]) {
    int cap = m->gx_capmarks[k] ? m->gx_capmarks[k] * 2 : 1024;
    void *p = realloc(m->gx_marks[k], (size_t)cap * sizeof *m->gx_marks[k]);
    if (!p) mc_die("out of memory (GX marks)");
    m->gx_marks[k] = p; m->gx_capmarks[k] = cap;
  }
  struct McGxMarkI *mk = &m->gx_marks[k][m->gx_nmarks[k]++];
  mk->offset = m->gx_len[k]; mk->gobj = gobj;
  mk->classifier = gobj ? (uint16_t)ld16(c, gobj) : 0;
  mk->p_link = gobj ? (uint8_t)ld8(c, gobj + 2) : 0;
  mk->gx_link = gobj ? (uint8_t)ld8(c, gobj + 3) : 0;
  mk->owner = 0; mk->owner_plink = 0xFF;
  if (gobj && (mk->p_link == 11 || mk->p_link == 12)) {   /* effects: EF_Effect.parent_gobj */
    uint32_t ef = ld32(c, gobj + 0x2C);
    uint32_t parent = ef >= 0x80000000u && ef < 0x80000000u + MEM1_SIZE - 0x10 ? ld32(c, ef + 8) : 0;
    if (parent >= 0x80000000u && parent < 0x80000000u + MEM1_SIZE - 0x30) { mk->owner = parent; mk->owner_plink = (uint8_t)ld8(c, parent + 2); }
  }
  uint32_t cobj = ld32(c, 0x804D765Cu);                 /* cobj.c `current`: the camera drawing now */
  for (int i = 0; i < 12; i++) {
    uint32_t u = cobj >= 0x80000000u && cobj < 0x80000000u + MEM1_SIZE - 0x90 ? ld32(c, cobj + 0x54 + 4u * (uint32_t)i) : 0;
    memcpy(&mk->view[i], &u, 4);
  }
}

/* at the frame boundary: this frame's stream becomes the last complete one */
void gx_frame_done(void) {
  Machine *m = g_m;
  if (!m->gx_on) return;
  gx_track_frame(m->gx_fifo[m->gx_cur], m->gx_len[m->gx_cur], m->cpu.mem);
  m->gx_cur = !m->gx_cur;
  m->gx_len[m->gx_cur] = 0;
  m->gx_nmarks[m->gx_cur] = 0;
}

void gx_free(Machine *m) {
  for (int k = 0; k < 2; k++) { free(m->gx_fifo[k]); free(m->gx_marks[k]); }
}
