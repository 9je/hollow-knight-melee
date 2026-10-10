#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include "oracle.h"

int main(void) {
  Cpu *c = calloc(1, sizeof *c);
  c->mem = calloc(1, MEM1_SIZE);
  c->msr = 0x00002032u; c->spr[SPR_HID2] = 0xE0000000u; c->spr[SPR_HID0] = 0x0011C464u;
  uint32_t npc;
  assert(dolphin_exec(c, 0x80003000u, 0x38600005u, &npc) == DOL_OK);           /* li r3,5 */
  assert(c->gpr[3] == 5 && npc == 0x80003004u);
  c->gpr[4] = 0x80001000u; c->mem[0x1002] = 0xAB;
  assert(dolphin_exec(c, 0x80003000u, 0x80640000u, &npc) == DOL_OK);           /* lwz r3,0(r4) */
  assert(c->gpr[3] == 0x0000AB00u);
  c->gpr[4] = 0xCC006000u;
  assert(dolphin_exec(c, 0x80003000u, 0x80640000u, &npc) == DOL_EXCEPTION);    /* DSI outside MEM1 */
  c->fpr[1].ps0 = 1.5; c->fpr[2].ps0 = 2.25;
  assert(dolphin_exec(c, 0x80003000u, 0xFC61102Au, &npc) == DOL_OK);           /* fadd f3,f1,f2 */
  assert(c->fpr[3].ps0 == 3.75);
  c->spr[SPR_LR] = 0x80004000u;
  assert(dolphin_exec(c, 0x80003000u, 0x4E800020u, &npc) == DOL_OK && npc == 0x80004000u); /* blr */
  /* run: li r3,7 ; blr  placed at 0x80005000, return to sentinel 0x80000100 */
  c->mem[0x5000] = 0x38; c->mem[0x5001] = 0x60; c->mem[0x5002] = 0x00; c->mem[0x5003] = 0x07;
  c->mem[0x5004] = 0x4E; c->mem[0x5005] = 0x80; c->mem[0x5006] = 0x00; c->mem[0x5007] = 0x20;
  uint32_t pc = 0x80005000u; uint64_t n;
  c->spr[SPR_LR] = 0x80000100u;
  assert(dolphin_run(c, &pc, 0x80000100u, 100, &n) == DOL_OK && c->gpr[3] == 7 && n == 2);
  puts("oracle_selftest OK");
  return 0;
}
