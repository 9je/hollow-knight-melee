#include <setjmp.h>
#include <xmmintrin.h>
#include "test_hooks.h"

static jmp_buf th_jmp;
uint32_t th_last_fault_ea, th_last_trap_pc, th_last_target;
GuestFn (*th_lookup)(uint32_t addr);

void guest_fault(Cpu *c, uint32_t ea, uint32_t size, int write) { (void)c; (void)size; (void)write; th_last_fault_ea = ea; longjmp(th_jmp, TH_FAULT); }
void guest_trap(Cpu *c, uint32_t pc, uint32_t insn) { (void)c; (void)insn; th_last_trap_pc = pc; longjmp(th_jmp, TH_TRAP); }
uint8_t *guest_io_ptr(Cpu *c, uint32_t ea, uint32_t size, int write) { guest_fault(c, ea, size, write); }
void guest_sc(Cpu *c, uint32_t pc) { guest_trap(c, pc, 0x44000002u); }
uint32_t guest_bad_return(Cpu *c, uint32_t expected, uint32_t got) { (void)c; (void)expected; th_last_target = got; longjmp(th_jmp, TH_BAD_RETURN); }
uint32_t guest_call_indirect(Cpu *c, uint32_t target) {
  GuestFn f = th_lookup ? th_lookup(target) : 0;
  if (!f) { th_last_target = target; longjmp(th_jmp, TH_INDIRECT_MISS); }
  return f(c);
}
uint32_t guest_mftb(Cpu *c, int upper) { (void)c; (void)upper; return 0; }
uint32_t guest_mfspr(Cpu *c, uint32_t spr) { return c->spr[spr]; }
/* Mirrors Dolphin's mtspr side effects on register values only: DMAL's DMA_T trigger bit self-clears. */
void guest_mtspr(Cpu *c, uint32_t spr, uint32_t value) { c->spr[spr] = spr == 923 ? value & ~2u : value; }
void guest_fpscr_changed(Cpu *c) { _mm_setcsr((_mm_getcsr() & ~0x8000u) | ((c->fpscr & GEKKO_FPSCR_NI) ? 0x8000u : 0u)); }

int th_run(Cpu *c, void (*fn)(Cpu *)) {
  int s = setjmp(th_jmp);
  if (s) return s;
  fn(c);
  return TH_OK;
}
int th_call(Cpu *c, GuestFn fn, uint32_t *ret) {
  int s = setjmp(th_jmp);
  if (s) return s;
  *ret = fn(c);
  return TH_OK;
}
