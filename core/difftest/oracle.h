#include "cpu.h"
#ifdef __cplusplus
extern "C" {
#endif
enum { DOL_OK = 0, DOL_EXCEPTION = 1, DOL_BUDGET = 2 };
/* Run one instruction word as if it were at guest address pc, on state c (c->mem is the memory). */
int dolphin_exec(Cpu *c, uint32_t pc, uint32_t insn, uint32_t *next_pc);
/* Fetch+execute from *pc until *pc == stop, an exception, or budget instructions. */
int dolphin_run(Cpu *c, uint32_t *pc, uint32_t stop, uint64_t budget, uint64_t *steps);
uint32_t dolphin_last_exceptions(void);   /* Dolphin's ppc_state.Exceptions after the last call */
#ifdef __cplusplus
}
#endif
