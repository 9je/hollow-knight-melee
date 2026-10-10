#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include "cpu.h"
#include "test_hooks.h"

static void rd_mmio(Cpu *c) { c->gpr[3] = ld32(c, 0xCC006000u); }
static void rd_null(Cpu *c) { c->gpr[3] = ld32(c, 0u); }
static void rd_lc(Cpu *c)   { c->gpr[3] = ld32(c, 0xE0000000u); }
static void rd_end(Cpu *c)  { c->gpr[3] = ld32(c, 0x817FFFFEu); }   /* straddles end of MEM1 */
static void wr_ok(Cpu *c)   { st32(c, 0x80001000u, 0x11223344u); st16(c, 0xC0001004u, 0x5566u); }

int main(void) {
  Cpu *c = calloc(1, sizeof *c);
  c->mem = calloc(1, MEM1_SIZE);
  assert(th_run(c, wr_ok) == TH_OK);
  assert(c->mem[0x1000] == 0x11 && c->mem[0x1003] == 0x44 && c->mem[0x1004] == 0x55);
  assert(ld32(c, 0xC0001000u) == 0x11223344u);
  assert(th_run(c, rd_mmio) == TH_FAULT && th_last_fault_ea == 0xCC006000u);
  assert(th_run(c, rd_null) == TH_FAULT && th_last_fault_ea == 0u);
  assert(th_run(c, rd_lc) == TH_FAULT && th_last_fault_ea == 0xE0000000u);
  assert(th_run(c, rd_end) == TH_FAULT);
  assert(rotl32(0x80000001u, 1) == 3u && rotl32(5u, 0) == 5u);
  c->spr[SPR_XER] = XER_SO;
  cr_cmp_s(c, 0, -1, 0); assert(c->cr >> 28 == 0x9);          /* LT | SO */
  cr_cmp_u(c, 7, 1, 0);  assert((c->cr & 0xF) == 0x5);         /* GT | SO */
  assert(cr_bit(c, 0) == 1 && cr_bit(c, 2) == 0);
  c->spr[SPR_XER] = 0;
  assert(add_ca(c, 0xFFFFFFFFu, 1u, 0) == 0u && (c->spr[SPR_XER] & XER_CA));
  assert(add_ca(c, 1u, 1u, 0) == 2u && !(c->spr[SPR_XER] & XER_CA));
  assert(fcmp4(1.0, 2.0) == 8 && fcmp4(2.0, 1.0) == 4 && fcmp4(0.0, -0.0) == 2 && fcmp4(u2f(0x7FF8000000000000ull), 0.0) == 1);
  puts("test_cpu OK");
  return 0;
}
