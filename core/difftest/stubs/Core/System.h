#pragma once
#include "Common/CommonTypes.h"
#include "Core/PowerPC/PowerPC.h"
#include "Core/PowerPC/JitInterface.h"
#include "Core/HW/GPFifo.h"
#include "Core/HW/SystemTimers.h"
namespace Memory { class MemoryManager { public: u8* GetPointerForRange(u32, u32) { return nullptr; } }; }
namespace Core {
class System {
public:
  PowerPC::PowerPCManager& GetPowerPC() { return m_ppc; }
  SystemTimers::SystemTimersManager& GetSystemTimers() { return m_timers; }
  JitInterface& GetJitInterface() { return m_jit; }
  GPFifo::GPFifoManager& GetGPFifo() { return m_fifo; }
  Memory::MemoryManager& GetMemory() { return m_memory; }
  PowerPC::MMU& GetMMU() { return *m_mmu; }
  PowerPC::MMU* m_mmu = nullptr;
private:
  PowerPC::PowerPCManager m_ppc;
  SystemTimers::SystemTimersManager m_timers;
  JitInterface m_jit;
  GPFifo::GPFifoManager m_fifo;
  Memory::MemoryManager m_memory;
};
inline bool IsCPUThread() { return true; }
class CPUThreadGuard { public: explicit CPUThreadGuard(System&) {} };
}  // namespace Core
