#define _POSIX_C_SOURCE 199309L
/* THROWAWAY boot spike (plan 1c scoping). Findings: polls EXI CR, then DSP CSR, then DSP mailbox (ucode handshake). */
/* boot.c - THROWAWAY spike: run recompiled main.dol from __start with minimal hooks and see where it stops.
 * usage: boot <mem1.bin> <iso> [seconds] */
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <setjmp.h>
#include <time.h>
#include <unistd.h>
#include <xmmintrin.h>
#include "cpu.h"
#include "guest_table.h"
#include "guest_meta.h"

static uint8_t hw[0x10000];     /* 0xCC000000 hardware registers (shadow) */
static uint8_t lc[0x4000];      /* 0xE0000000 locked cache */
static struct { uint32_t ea; int w; unsigned long n; } io_log[512];
static int io_n;

static void io_note(uint32_t ea, int w) {
  for (int i = 0; i < io_n; i++) if (io_log[i].ea == ea && io_log[i].w == w) { io_log[i].n++; return; }
  if (io_n < 512) { io_log[io_n].ea = ea; io_log[io_n].w = w; io_log[io_n].n = 1; io_n++; }
}
static void io_dump(void) {
  fprintf(stderr, "--- io accesses (%d distinct)\n", io_n);
  for (int i = 0; i < io_n; i++) fprintf(stderr, "  %08X %s x%lu\n", io_log[i].ea, io_log[i].w ? "W" : "R", io_log[i].n);
}
static const char *fname(uint32_t a) {
  const char *best = "?"; uint32_t ba = 0;
  for (size_t i = 0; i < g_guest_nnames; i++) if (g_guest_names[i].addr <= a && g_guest_names[i].addr >= ba) { ba = g_guest_names[i].addr; best = g_guest_names[i].name; }
  return best;
}
void guest_fault(Cpu *c, uint32_t ea, uint32_t size, int write) {
  (void)c; fprintf(stderr, "FAULT ea=%08X size=%u %s\n", ea, size, write ? "W" : "R"); io_dump(); abort();
}
uint8_t *guest_io_ptr(Cpu *c, uint32_t ea, uint32_t size, int write) {
  if ((ea & 0xFFFF0000u) == 0xCC000000u && (ea & 0xFFFF) + size <= sizeof hw) {
    io_note(ea, write);
    uint32_t o = ea & 0xFFFF;
    /* EXI channel control (0x680C/0x6820/0x6834): transfers complete instantly (TSTART reads back 0). */
    if (!write && (o == 0x680C || o == 0x6820 || o == 0x6834)) hw[o + 3] &= ~1u;
    /* DSP CSR (0x500A, 16-bit): reset / DMA / init-busy bits self-clear. */
    if (!write && (o == 0x500A || o == 0x500B)) { hw[0x500A] &= ~0x0Eu; hw[0x500B] &= ~0x01u; }
    return hw + o;
  }
  if ((ea & 0xFFFFC000u) == 0xE0000000u && (ea & 0x3FFF) + size <= sizeof lc) return lc + (ea & 0x3FFF);
  guest_fault(c, ea, size, write);
}
void guest_sc(Cpu *c, uint32_t pc) { (void)c; (void)pc; }
void guest_trap(Cpu *c, uint32_t pc, uint32_t insn) {
  (void)c; fprintf(stderr, "TRAP pc=%08X (%s) insn=%08X lr=%08X\n", pc, fname(pc), insn, c->spr[SPR_LR]); io_dump(); abort();
}
uint32_t guest_bad_return(Cpu *c, uint32_t expected, uint32_t got) {
  (void)c; fprintf(stderr, "BAD_RETURN expected=%08X got=%08X\n", expected, got); abort();
}
uint32_t guest_call_indirect(Cpu *c, uint32_t target) {
  GuestFn f = guest_lookup(target);
  if (!f) { fprintf(stderr, "INDIRECT_MISS %08X\n", target); abort(); }
  return f(c);
}
static uint64_t tb_now(void) {   /* 40.5 MHz timebase from host monotonic time (spike: not deterministic) */
  struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
  return (uint64_t)ts.tv_sec * 40500000ull + (uint64_t)ts.tv_nsec * 405ull / 10000ull;
}
uint32_t guest_mftb(Cpu *c, int upper) { (void)c; uint64_t t = tb_now(); return upper ? (uint32_t)(t >> 32) : (uint32_t)t; }
static uint64_t dec_deadline;   /* timebase value at which DEC goes negative */
static int dec_armed;
uint32_t guest_mfspr(Cpu *c, uint32_t spr) { return spr == 22 ? (uint32_t)(dec_deadline - tb_now()) : c->spr[spr]; }
void guest_mtspr(Cpu *c, uint32_t spr, uint32_t v) {
  if (spr == 22) { dec_deadline = tb_now() + (uint64_t)(int64_t)(int32_t)v + 1; dec_armed = 1; c->irq_pending = 1; return; }
  c->spr[spr] = spr == 923 ? v & ~2u : v;
}
void guest_fpscr_changed(Cpu *c) { _mm_setcsr((_mm_getcsr() & ~0x8000u) | ((c->fpscr & 4u) ? 0x8000u : 0u)); }

/* ---- host replacements (hle.txt) ---- */
static uint32_t call_guest(Cpu *c, uint32_t addr, uint32_t r3, uint32_t r4);
static void service(Cpu *c);
#define RET return c->spr[SPR_LR]
uint32_t hle___OSInitAudioSystem(Cpu *c) { RET; }   /* audio is out of scope */
uint32_t hle___AI_SRC_INIT(Cpu *c) { RET; }  /* audio sample-rate calibration */
uint32_t hle___AXOutInitDSP(Cpu *c) { RET; }  /* AX DSP task boot: no DSP */
uint32_t hle___OSInitMemoryProtection(Cpu *c) { RET; }  /* BATs/MI protection: fixed host memory map */

static unsigned long g_frames;
/* Call a guest function from the host; returns its r3. Volatile registers are clobbered like a real call. */
static uint32_t call_guest(Cpu *c, uint32_t addr, uint32_t r3, uint32_t r4) {
  uint32_t lr = c->spr[SPR_LR];
  c->gpr[3] = r3; c->gpr[4] = r4; c->spr[SPR_LR] = 0x80000100u;
  uint32_t r = guest_lookup(addr)(c);
  if (r != 0x80000100u) { fprintf(stderr, "guest call %08X returned to %08X\n", addr, r); abort(); }
  c->spr[SPR_LR] = lr;
  return c->gpr[3];
}
/* One video frame: raise the VI display interrupt (DI0 status bit) and run the guest's handler. */
uint32_t hle_VIWaitForRetrace(Cpu *c) {
  g_frames++;
  service(c);
  hw[0x2030] |= 0x80;                                   /* VI DI0 interrupt status */
  uint32_t ctx = ld32(c, 0x800000D4u);                  /* OSGetCurrentContext */
  call_guest(c, 0x8034E964u, 24u, ctx);                 /* __VIRetraceHandler(__OS_INTERRUPT_PI_VI, ctx) */
  RET;
}

/* PADRead(PADStatus status[4]): 12-byte entries; port 0 connected and idle, ports 1-3 absent. */
uint32_t hle_PADRead(Cpu *c) {
  uint32_t p = c->gpr[3];
  for (int i = 0; i < 4; i++) {
    for (int k = 0; k < 12; k++) st8(c, p + 12 * i + k, 0);
    st8(c, p + 12 * i + 10, i == 0 ? 0 : (uint8_t)-1);   /* err: 0 OK, -1 PAD_ERR_NO_CONTROLLER */
  }
  c->gpr[3] = 1; RET;
}
uint32_t hle_CARDProbeEx(Cpu *c) { c->gpr[3] = (uint32_t)-3; RET; }   /* CARD_RESULT_NOCARD */

/* ---- interrupts: pending sources delivered when the guest enables interrupts or at a retrace ---- */
static uint8_t aram[0x01000000];
static FILE *g_iso;
static struct { uint32_t cb, block, len; } pend_dvd[64];
static int n_dvd;
static int pend_ar;
static int pend_pe;   /* PE finish (draw done) */
static int in_irq;
static void deliver_queues(Cpu *c) {
  while (n_dvd || pend_ar || pend_pe) {
  while (n_dvd) {                                       /* DVD command completion callbacks, FIFO */
    uint32_t cb = pend_dvd[0].cb, block = pend_dvd[0].block, len = pend_dvd[0].len;
    memmove(pend_dvd, pend_dvd + 1, sizeof pend_dvd[0] * (size_t)--n_dvd);
    if (getenv("TRACE")) fprintf(stderr, "dvd cb %08X len %X\n", cb, len);
    if (cb) { uint32_t msr = c->msr; c->msr &= ~0x8000u; call_guest(c, cb, len, block); c->msr = msr; }
  }
  if (pend_pe) {                                        /* GXFinishInterruptHandler(__OS_INTERRUPT_PI_PE_FINISH, ctx) */
    pend_pe = 0;
    call_guest(c, 0x8033CF4Cu, 9u, ld32(c, 0x800000D4u));
  }
  while (pend_ar) {
    pend_ar = 0;
    if (getenv("TRACE")) fprintf(stderr, "ar irq\n");
    hw[0x500B] |= 0x20;                                  /* DSP CSR: ARAM DMA interrupt */
    uint32_t msr = c->msr;
    c->msr &= ~0x8000u;                                  /* handlers run with EE off */
    call_guest(c, 0x80350F98u, 6u, ld32(c, 0x800000D4u)); /* __ARHandler(__OS_INTERRUPT_DSP_ARAM, ctx) */
    c->msr = msr;
  }
  }
}
static int dec_due(void) { return dec_armed && (int64_t)(tb_now() - dec_deadline) >= 0; }
static jmp_buf dec_jmp;
static int in_dec;
/* OSLoadContext: only reached as the rfi ending an interrupt handler (single-threaded); unwind to service(). */
uint32_t hle_OSLoadContext(Cpu *c) {
  (void)c;
  if (!in_dec) { fprintf(stderr, "OSLoadContext outside DEC handler\n"); abort(); }
  longjmp(dec_jmp, 1);
}
/* Take every pending interrupt. Every register may be live, so the whole context is saved and
 * restored around the handlers, as the exception vector + rfi do on hardware. */
static void service(Cpu *c) {
  static Cpu saved;
  if (in_irq) return;
  int dec = dec_due();
  if (!dec && !n_dvd && !pend_ar && !pend_pe) { c->irq_pending = dec_armed; return; }
  in_irq = 1;
  saved = *c;
  c->msr &= ~0x8000u;                                    /* handlers run with EE off */
  if (dec) {
    dec_armed = 0;
    in_dec = 1;
    if (!setjmp(dec_jmp)) call_guest(c, 0x80343BC8u, 8u, ld32(c, 0x800000D4u));  /* DecrementerExceptionCallback */
    in_dec = 0;
  }
  deliver_queues(c);
  *c = saved;
  c->irq_pending = dec_armed || n_dvd || pend_ar || pend_pe;
  in_irq = 0;
}
void guest_irq(Cpu *c) { service(c); }
/* No GPU: the draw-done token completes as soon as it is set; the PE finish interrupt is pending. */
uint32_t hle_GXSetDrawDone(Cpu *c) { st8(c, 0x804D7328u, 0); pend_pe = 1; c->irq_pending = 1; RET; }
uint32_t hle_GXWaitDrawDone(Cpu *c) { service(c); RET; }  /* takes the pending finish interrupt */
/* BOOL DVDReadAbsAsyncPrio(DVDCommandBlock *block, void *addr, s32 length, s32 offset, cb, prio).
 * Reads straight from the ISO; block->state = END(0), transferredSize = length; callback(length, block) later. */
uint32_t hle_DVDReadAbsAsyncPrio(Cpu *c) {
  uint32_t block = c->gpr[3], addr = c->gpr[4], len = c->gpr[5], off = c->gpr[6], cb = c->gpr[7];
  fseek(g_iso, (long)off, SEEK_SET);
  if (fread(guest_ptr(c, addr, len, 1), 1, len, g_iso) != len) { fprintf(stderr, "ISO read fail %08X+%X\n", off, len); abort(); }
  st32(c, block + 0x08, 1);       /* command = READ */
  st32(c, block + 0x0C, 0);       /* state = DVD_STATE_END */
  st32(c, block + 0x10, off); st32(c, block + 0x14, len); st32(c, block + 0x18, addr);
  st32(c, block + 0x20, len);     /* transferredSize */
  st32(c, block + 0x28, cb);
  if (n_dvd == 64) abort();
  pend_dvd[n_dvd].cb = cb; pend_dvd[n_dvd].block = block; pend_dvd[n_dvd].len = len; n_dvd++; c->irq_pending = 1;
  c->gpr[3] = 1; RET;
}
uint32_t hle_DVDGetDriveStatus(Cpu *c) { c->gpr[3] = 0; RET; }   /* DVD_STATE_END: idle */
uint32_t hle___ARChecksize(Cpu *c) { st32(c, 0x804D74B4u, 0x01000000u); RET; }  /* __AR_Size = 16 MiB */
/* ARStartDMA(type, mainmem, aram, length): 0 = MRAM->ARAM, 1 = ARAM->MRAM. Completes instantly; IRQ pending. */
uint32_t hle_ARStartDMA(Cpu *c) {
  uint32_t type = c->gpr[3], mm = c->gpr[4], ar = c->gpr[5], len = c->gpr[6];
  if (ar + len > sizeof aram) { fprintf(stderr, "ARAM DMA out of range %08X+%X\n", ar, len); abort(); }
  uint8_t *m = guest_ptr(c, mm, len, type == 1);
  if (getenv("TRACE")) fprintf(stderr, "ardma t%u %08X %08X %X\n", type, mm, ar, len);
  if (type == 0) memcpy(aram + ar, m, len); else memcpy(m, aram + ar, len);
  pend_ar = 1; c->irq_pending = 1; RET;
}
/* BOOL OSRestoreInterrupts(BOOL level) / BOOL OSEnableInterrupts(void): return old EE, then deliver. */
uint32_t hle_OSRestoreInterrupts(Cpu *c) {
  uint32_t old = (c->msr >> 15) & 1;
  if (c->gpr[3]) c->msr |= 0x8000u; else c->msr &= ~0x8000u;
  c->gpr[3] = old;
  if (c->msr & 0x8000u) service(c);
  c->gpr[3] = old; RET;
}
uint32_t hle_OSEnableInterrupts(Cpu *c) {
  uint32_t old = (c->msr >> 15) & 1;
  c->msr |= 0x8000u;
  service(c);
  c->gpr[3] = old; RET;
}

static Cpu *g_cpu;
static void on_alarm(int s) { (void)s; fprintf(stderr, "TIMEOUT after %lu frames\n", g_frames);
  fprintf(stderr, "SCENE %02X %02X %02X %02X\n", ld8(g_cpu, 0x80479D30u), ld8(g_cpu, 0x80479D31u), ld8(g_cpu, 0x80479D32u), ld8(g_cpu, 0x80479D33u)); io_dump(); abort(); }

static void w32(uint8_t *m, uint32_t a, uint32_t v) { m[a & 0x1FFFFFF] = v >> 24; m[(a & 0x1FFFFFF) + 1] = v >> 16; m[(a & 0x1FFFFFF) + 2] = v >> 8; m[(a & 0x1FFFFFF) + 3] = v; }
static uint32_t be32(const uint8_t *p) { return (uint32_t)p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]; }

int main(int argc, char **argv) {
  if (argc < 3) { fprintf(stderr, "usage: boot mem1.bin iso [seconds]\n"); return 2; }
  static Cpu cpu; Cpu *c = &cpu; g_cpu = c;
  c->mem = calloc(1, MEM1_SIZE);
  FILE *f = fopen(argv[1], "rb"); if (!f || fread(c->mem, 1, MEM1_SIZE, f) != MEM1_SIZE) return 2; fclose(f);
  /* Apploader/IPL leftovers (values as Dolphin's BS2 emulation writes them). */
  FILE *iso = fopen(argv[2], "rb"); g_iso = iso; uint8_t hdr[0x440];
  if (!iso || fread(hdr, 1, sizeof hdr, iso) != sizeof hdr) return 2;
  memcpy(c->mem, hdr, 0x20);                    /* disc ID */
  uint32_t fst_off = be32(hdr + 0x424), fst_size = be32(hdr + 0x428);
  uint32_t fst_addr = (0x81800000u - fst_size) & ~31u;
  fseek(iso, fst_off, SEEK_SET);
  if (fread(c->mem + (fst_addr & 0x1FFFFFF), 1, fst_size, iso) != fst_size) return 2;
  w32(c->mem, 0x80000020, 0x0D15EA5E); w32(c->mem, 0x80000024, 1);
  w32(c->mem, 0x80000028, 0x01800000); w32(c->mem, 0x8000002C, 3);
  w32(c->mem, 0x80000030, 0); w32(c->mem, 0x80000034, fst_addr);
  w32(c->mem, 0x80000038, fst_addr); w32(c->mem, 0x8000003C, fst_size);
  w32(c->mem, 0x800000CC, 0); w32(c->mem, 0x800000F0, 0x01800000);
  w32(c->mem, 0x800000F8, 162000000); w32(c->mem, 0x800000FC, 486000000);
  c->msr = 0x00002032u;
  c->spr[SPR_HID2] = 0xE0000000u;
  c->spr[SPR_LR] = 0x80000100u;
  signal(SIGALRM, on_alarm);
  alarm(argc > 3 ? atoi(argv[3]) : 10);
  uint32_t r = guest_lookup(GUEST_ENTRY)(c);
  fprintf(stderr, "__start returned %08X\n", r);
  io_dump();
  return 0;
}
