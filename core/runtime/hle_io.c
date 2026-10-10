/* hle_io.c - DVD (from the ISO), ARAM, PAD, memory card and audio replacements. */
#include <string.h>
#include "mc_internal.h"
#include "guest_fns.h"

#define ADDR_AR_SIZE 0x804D74B4u   /* __AR_Size */
#define GM_COUNT 0x2D

uint32_t hle___OSInitAudioSystem(Cpu *c) { RET; }      /* audio is out of scope (spec §3.3) */
uint32_t hle___AI_SRC_INIT(Cpu *c) { RET; }
uint32_t hle___AXOutInitDSP(Cpu *c) { RET; }           /* no DSP */
uint32_t hle___OSInitMemoryProtection(Cpu *c) { RET; } /* fixed host memory map */
uint32_t hle_CARDProbeEx(Cpu *c) { c->gpr[3] = (uint32_t)-3; RET; }        /* CARD_RESULT_NOCARD */
uint32_t hle_lbCardGame_DecideGameMode(Cpu *c) { c->gpr[3] = GM_COUNT; RET; } /* never the memory card scene */
uint32_t hle_fn_800195FC(Cpu *c) { RET; }  /* pad-sampling alarm: input arrives at frame boundaries (frame.c) */

/* u32 PADRead(PADStatus status[4]): the current frame's input. */
uint32_t hle_PADRead(Cpu *c) {
  uint32_t p = c->gpr[3];
  for (int i = 0; i < 4; i++)
    for (int k = 0; k < 12; k++) st8(c, p + 12u * (uint32_t)i + (uint32_t)k, g_m->pad[i][k]);
  c->gpr[3] = 1;
  RET;
}

/* BOOL DVDReadAbsAsyncPrio(DVDCommandBlock *block, void *addr, s32 length, s32 offset, cb, prio):
 * reads the ISO at once; the completion callback(length, block) is delivered as an interrupt. */
uint32_t hle_DVDReadAbsAsyncPrio(Cpu *c) {
  Machine *m = g_m;
  uint32_t block = c->gpr[3], addr = c->gpr[4], len = c->gpr[5], off = c->gpr[6], cb = c->gpr[7];
  if (m->n_dvd == (int)(sizeof m->dvd / sizeof m->dvd[0])) mc_die("too many DVD reads in flight");
  if (fseek(m->iso, (long)off, SEEK_SET) != 0 || fread(guest_ptr(c, addr, len, 1), 1, len, m->iso) != len)
    mc_die("ISO read failed at %08X+%X", off, len);
  st32(c, block + 0x08, 1);   /* command = READ */
  st32(c, block + 0x0C, 0);   /* state = DVD_STATE_END */
  st32(c, block + 0x10, off);
  st32(c, block + 0x14, len);
  st32(c, block + 0x18, addr);
  st32(c, block + 0x20, len); /* transferredSize */
  st32(c, block + 0x28, cb);
  m->dvd[m->n_dvd].cb = cb;
  m->dvd[m->n_dvd].block = block;
  m->dvd[m->n_dvd].len = len;
  m->n_dvd++;
  c->irq_pending = 1;
  c->gpr[3] = 1;
  RET;
}
uint32_t hle_DVDGetDriveStatus(Cpu *c) { c->gpr[3] = 0; RET; }   /* idle */
uint32_t hle___ARChecksize(Cpu *c) { st32(c, ADDR_AR_SIZE, ARAM_SIZE); RET; }
/* ARStartDMA(type, mainmem, aram, length): 0 = MRAM->ARAM, 1 = ARAM->MRAM; completes at once. */
uint32_t hle_ARStartDMA(Cpu *c) {
  uint32_t type = c->gpr[3], mm = c->gpr[4], ar = c->gpr[5], len = c->gpr[6];
  if (ar > ARAM_SIZE || len > ARAM_SIZE - ar) mc_die("ARAM DMA out of range %08X+%X", ar, len);
  uint8_t *p = guest_ptr(c, mm, len, type == 1);
  if (type == 0) memcpy(g_m->aram + ar, p, len); else memcpy(p, g_m->aram + ar, len);
  g_m->pend_ar = 1;
  c->irq_pending = 1;
  RET;
}
