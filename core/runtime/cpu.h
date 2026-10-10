/* cpu.h - guest CPU state and memory access for recompiled main.dol code. */
#ifndef GUEST_CPU_H
#define GUEST_CPU_H
#include <stdint.h>
#include <string.h>
#include "gekko_fp.h"

#ifdef __cplusplus
#define _Noreturn [[noreturn]]
extern "C" {
#endif

#define MEM1_SIZE 0x01800000u
#define SPR_XER 1
#define SPR_LR 8
#define SPR_CTR 9
#define SPR_GQR0 912
#define SPR_HID2 920
#define SPR_HID0 1008
#define XER_SO 0x80000000u
#define XER_OV 0x40000000u
#define XER_CA 0x20000000u
#define XER_MASK 0xE000FF7Fu /* bits Dolphin keeps: SO OV CA, BYTE_CMP, BYTE_COUNT */

typedef struct Cpu {
  uint32_t gpr[32];
  GekkoPS fpr[32];     /* ps0, ps1; compared as raw bits */
  uint32_t cr;         /* CR0 in bits 31..28 */
  uint32_t fpscr;
  uint32_t msr;
  uint32_t sr[16];
  uint32_t spr[1024];  /* indexed like Dolphin: XER=1, LR=8, CTR=9, GQRn=912+n, HID2=920 */
  uint8_t *mem;        /* MEM1_SIZE bytes */
} Cpu;

#define LR  (c->spr[SPR_LR])
#define CTR (c->spr[SPR_CTR])
#define XER (c->spr[SPR_XER])
#define CA  ((XER >> 29) & 1u)

/* Hooks: implemented by the runtime (plan 1c) or by test_hooks.c. */
_Noreturn void guest_fault(Cpu *c, uint32_t ea, uint32_t size, int write);
_Noreturn void guest_trap(Cpu *c, uint32_t pc, uint32_t insn);
uint32_t guest_bad_return(Cpu *c, uint32_t expected, uint32_t got);
uint32_t guest_call_indirect(Cpu *c, uint32_t target);
uint32_t guest_mftb(Cpu *c, int upper);
uint32_t guest_mfspr(Cpu *c, uint32_t spr);
void guest_mtspr(Cpu *c, uint32_t spr, uint32_t value);
void guest_fpscr_changed(Cpu *c);

static inline uint8_t *guest_ptr(Cpu *c, uint32_t ea, uint32_t size, int write) {
  uint32_t seg = ea >> 29, off = ea & 0x1FFFFFFFu;
  if ((seg == 4u || seg == 6u) && off + size <= MEM1_SIZE) return c->mem + off;
  guest_fault(c, ea, size, write);
}
static inline uint32_t ld8(Cpu *c, uint32_t ea) { return *guest_ptr(c, ea, 1, 0); }
static inline uint32_t ld16(Cpu *c, uint32_t ea) { uint16_t v; memcpy(&v, guest_ptr(c, ea, 2, 0), 2); return __builtin_bswap16(v); }
static inline uint32_t ld32(Cpu *c, uint32_t ea) { uint32_t v; memcpy(&v, guest_ptr(c, ea, 4, 0), 4); return __builtin_bswap32(v); }
static inline uint64_t ld64(Cpu *c, uint32_t ea) { uint64_t v; memcpy(&v, guest_ptr(c, ea, 8, 0), 8); return __builtin_bswap64(v); }
static inline void st8(Cpu *c, uint32_t ea, uint32_t v) { *guest_ptr(c, ea, 1, 1) = (uint8_t)v; }
static inline void st16(Cpu *c, uint32_t ea, uint32_t v) { uint16_t x = __builtin_bswap16((uint16_t)v); memcpy(guest_ptr(c, ea, 2, 1), &x, 2); }
static inline void st32(Cpu *c, uint32_t ea, uint32_t v) { uint32_t x = __builtin_bswap32(v); memcpy(guest_ptr(c, ea, 4, 1), &x, 4); }
static inline void st64(Cpu *c, uint32_t ea, uint64_t v) { uint64_t x = __builtin_bswap64(v); memcpy(guest_ptr(c, ea, 8, 1), &x, 8); }

static inline uint32_t rotl32(uint32_t x, uint32_t n) { n &= 31; return n ? (x << n) | (x >> (32 - n)) : x; }
static inline uint64_t f2u(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
static inline double u2f(uint64_t u) { double d; memcpy(&d, &u, 8); return d; }

static inline void cr_set(Cpu *c, int f, uint32_t v4) { int sh = 28 - 4 * f; c->cr = (c->cr & ~(0xFu << sh)) | ((v4 & 0xFu) << sh); }
static inline uint32_t cr_get(Cpu *c, int f) { return (c->cr >> (28 - 4 * f)) & 0xFu; }
static inline uint32_t cr_bit(Cpu *c, int bi) { return (c->cr >> (31 - bi)) & 1u; }
static inline void cr_setbit(Cpu *c, int bi, uint32_t v) { c->cr = (c->cr & ~(1u << (31 - bi))) | ((v & 1u) << (31 - bi)); }
static inline void cr_cmp_s(Cpu *c, int f, int32_t a, int32_t b) { cr_set(c, f, (a < b ? 8u : a > b ? 4u : 2u) | (XER >> 31)); }
static inline void cr_cmp_u(Cpu *c, int f, uint32_t a, uint32_t b) { cr_set(c, f, (a < b ? 8u : a > b ? 4u : 2u) | (XER >> 31)); }
#define CR0(v) cr_cmp_s(c, 0, (int32_t)(v), 0)
static inline void set_ca(Cpu *c, uint32_t ca) { XER = (XER & ~XER_CA) | (ca ? XER_CA : 0u); }
static inline uint32_t add_ca(Cpu *c, uint32_t a, uint32_t b, uint32_t cin) {
  uint64_t r = (uint64_t)a + b + cin; set_ca(c, (uint32_t)(r >> 32)); return (uint32_t)r;
}
static inline uint32_t fcmp4(double a, double b) { return a < b ? 8u : a > b ? 4u : a == b ? 2u : 1u; }

/* Quantized load/store. Size 0 for invalid GQR types 1-3 (Dolphin: no memory access). */
static inline uint32_t psq_size(uint32_t type, uint32_t w) {
  uint32_t e = type == 0 ? 4u : (type == 4 || type == 6) ? 1u : (type == 5 || type == 7) ? 2u : 0u;
  return w ? e : 2u * e;
}
static inline GekkoPS psq_ld(Cpu *c, uint32_t ea, uint32_t type, uint32_t scale, uint32_t w) {
  uint32_t n = psq_size(type, w);
  return gekko_psq_load(n ? guest_ptr(c, ea, n, 0) : c->mem, type, scale, w);
}
static inline void psq_st(Cpu *c, uint32_t ea, GekkoPS v, uint32_t type, uint32_t scale, uint32_t w) {
  uint32_t n = psq_size(type, w);
  gekko_psq_store(n ? guest_ptr(c, ea, n, 1) : c->mem, v, type, scale, w);
}
#ifdef __cplusplus
}
#endif
#endif
