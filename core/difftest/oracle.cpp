// oracle.cpp - runs Dolphin's unmodified interpreter (pinned commit) on our Cpu struct.
// Dispatch mirrors Interpreter::SingleStepInner: invalid paired-single execution raises a program
// exception, otherwise the op runs through Interpreter::RunInterpreterOp. MSR.FP is assumed set.
#include <xmmintrin.h>
#include "Common/CPUDetect.h"
#include "Core/PowerPC/Interpreter/ExceptionUtils.h"
#include "Core/PowerPC/Interpreter/Interpreter.h"
#include "Core/PowerPC/PowerPC.h"
#include "Core/System.h"
#include "Core/Debugger/BranchWatch.h"
#include "oracle.h"

CPUInfo cpu_info;
class PPCSymbolDB {};

Interpreter::Interpreter(Core::System& system, PowerPC::PowerPCState& ppc_state, PowerPC::MMU& mmu,
                         Core::BranchWatch& branch_watch, PPCSymbolDB& ppc_symbol_db)
    : m_system(system), m_ppc_state(ppc_state), m_mmu(mmu), m_branch_watch(branch_watch),
      m_ppc_symbol_db(ppc_symbol_db) {}
Interpreter::~Interpreter() = default;
void Interpreter::Init() {}
void Interpreter::Shutdown() {}
void Interpreter::SingleStep() {}
void Interpreter::Run() {}
void Interpreter::ClearCache() {}
const char* Interpreter::GetName() const { return "oracle"; }
void Interpreter::unknown_instruction(Interpreter& interpreter, UGeckoInstruction) {
  GenerateProgramException(interpreter.m_ppc_state, ProgramExceptionCause::IllegalInstruction);
}

// Copied from Interpreter.cpp (pinned commit).
static bool IsPairedSingleQuantizedNonIndexedInstruction(UGeckoInstruction inst) {
  const u32 opcode = inst.OPCD;
  return opcode == 0x38 || opcode == 0x39 || opcode == 0x3C || opcode == 0x3D;
}
static bool IsPairedSingleInstruction(UGeckoInstruction inst) {
  return inst.OPCD == 4 || IsPairedSingleQuantizedNonIndexedInstruction(inst);
}
static bool IsInvalidPairedSingleExecution(PowerPC::PowerPCState& s, UGeckoInstruction inst) {
  if (!HID2(s).PSE && IsPairedSingleInstruction(inst)) return true;
  return HID2(s).PSE && !HID2(s).LSQE && IsPairedSingleQuantizedNonIndexedInstruction(inst);
}

namespace {
PowerPC::PowerPCState g_state;
PowerPC::MMU g_mmu;
Core::System g_system;
Core::BranchWatch g_bw;
PPCSymbolDB g_syms;
Interpreter g_interp(g_system, g_state, g_mmu, g_bw, g_syms);
u32 g_last_exceptions;

void to_dolphin(const Cpu* c) {
  auto& s = g_state;
  for (int i = 0; i < 32; i++) {
    s.gpr[i] = c->gpr[i];
    s.ps[i].SetBoth(f2u(c->fpr[i].ps0), f2u(c->fpr[i].ps1));
  }
  for (int i = 0; i < 1024; i++) s.spr[i] = c->spr[i];
  s.SetXER(UReg_XER{c->spr[SPR_XER]});
  s.cr.Set(c->cr);
  s.msr.Hex = c->msr;
  s.fpscr.Hex = c->fpscr;
  for (int i = 0; i < 16; i++) s.sr[i] = c->sr[i];
  g_mmu.mem = c->mem;
  g_mmu.exceptions = &s.Exceptions;
  g_system.m_mmu = &g_mmu;
  _mm_setcsr((_mm_getcsr() & ~0x8000u) | ((c->fpscr & 4u) ? 0x8000u : 0u));
}
void from_dolphin(Cpu* c) {
  auto& s = g_state;
  for (int i = 0; i < 32; i++) {
    c->gpr[i] = s.gpr[i];
    c->fpr[i].ps0 = u2f(s.ps[i].PS0AsU64());
    c->fpr[i].ps1 = u2f(s.ps[i].PS1AsU64());
  }
  for (int i = 0; i < 1024; i++) c->spr[i] = s.spr[i];
  c->spr[SPR_XER] = s.GetXER().Hex;
  c->cr = s.cr.Get();
  c->msr = s.msr.Hex;
  c->fpscr = s.fpscr.Hex;
  for (int i = 0; i < 16; i++) c->sr[i] = s.sr[i];
}
// One instruction at g_state.pc; returns true if an exception was raised.
bool step(u32 word) {
  auto& s = g_state;
  s.npc = s.pc + 4;
  s.Exceptions = 0;
  UGeckoInstruction inst{word};
  if (word == 0 || IsInvalidPairedSingleExecution(s, inst))
    GenerateProgramException(s, ProgramExceptionCause::IllegalInstruction);
  else
    Interpreter::RunInterpreterOp(g_interp, inst);
  g_last_exceptions = s.Exceptions;
  // Only synchronous exceptions stop execution; decrementer/external/perf-monitor are interrupts,
  // which plan 1b does not model (the recompiled side has no interrupt delivery either).
  return (s.Exceptions & (EXCEPTION_SYSCALL | EXCEPTION_DSI | EXCEPTION_ISI | EXCEPTION_ALIGNMENT |
                          EXCEPTION_FPU_UNAVAILABLE | EXCEPTION_PROGRAM)) != 0;
}
}  // namespace

extern "C" int dolphin_exec(Cpu* c, uint32_t pc, uint32_t insn, uint32_t* next_pc) {
  to_dolphin(c);
  g_state.pc = pc;
  bool exc = step(insn);
  from_dolphin(c);
  *next_pc = g_state.npc;
  return exc ? DOL_EXCEPTION : DOL_OK;
}

extern "C" int dolphin_run(Cpu* c, uint32_t* pc, uint32_t stop, uint64_t budget, uint64_t* steps) {
  to_dolphin(c);
  g_state.pc = *pc;
  uint64_t n = 0;
  int rc = DOL_OK;
  while (g_state.pc != stop) {
    if (n == budget) { rc = DOL_BUDGET; break; }
    g_state.Exceptions = 0;
    u32 word = g_mmu.Read<u32>(g_state.pc);
    if (g_state.Exceptions) { g_last_exceptions = g_state.Exceptions; rc = DOL_EXCEPTION; break; }
    n++;
    if (step(word)) { rc = DOL_EXCEPTION; break; }
    g_state.pc = g_state.npc;
  }
  from_dolphin(c);
  *pc = g_state.pc;
  *steps = n;
  return rc;
}

extern "C" uint32_t dolphin_last_exceptions(void) { return g_last_exceptions; }
