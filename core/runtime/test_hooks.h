/* test_hooks.h - hook implementations for tests: every guest stop longjmps back to th_run(). */
#ifndef TEST_HOOKS_H
#define TEST_HOOKS_H
#include "cpu.h"
enum { TH_OK = 0, TH_FAULT, TH_TRAP, TH_BAD_RETURN, TH_INDIRECT_MISS };
extern uint32_t th_last_fault_ea, th_last_trap_pc, th_last_target;
/* Optional resolver used by guest_call_indirect (set by func_diff / generated tests). */
#ifndef GUEST_FN_DEFINED
#define GUEST_FN_DEFINED
typedef uint32_t (*GuestFn)(Cpu *);
#endif
extern GuestFn (*th_lookup)(uint32_t addr);
int th_run(Cpu *c, void (*fn)(Cpu *));
int th_call(Cpu *c, GuestFn fn, uint32_t *ret);  /* runs a guest function, *ret = returned LR */
#endif
