#pragma once
// Minimal stub of Dolphin's PowerPC.h: only the state needed by the FP/paired interpreter files.
#include <bit>
#include <cstring>
#include <vector>
#include "Common/CommonTypes.h"
#include "Common/FloatUtils.h"
#include "Core/PowerPC/Gekko.h"
struct PairedSingle {
  u64 PS0AsU64() const { return ps0; }
  u64 PS1AsU64() const { return ps1; }
  double PS0AsDouble() const { return std::bit_cast<double>(ps0); }
  double PS1AsDouble() const { return std::bit_cast<double>(ps1); }
  void SetPS0(u64 v) { ps0 = v; }
  void SetPS0(double v) { ps0 = std::bit_cast<u64>(v); }
  void SetPS1(u64 v) { ps1 = v; }
  void SetPS1(double v) { ps1 = std::bit_cast<u64>(v); }
  void SetBoth(u64 l, u64 r) { SetPS0(l); SetPS1(r); }
  void SetBoth(double l, double r) { SetPS0(l); SetPS1(r); }
  void Fill(u64 v) { SetBoth(v, v); }
  void Fill(double v) { SetBoth(v, v); }
  u64 ps0 = 0, ps1 = 0;
};
namespace PowerPC {
struct PowerPCState {
  u32 gpr[32] = {};
  u32 spr[1024] = {};
  u32 Exceptions = 0;
  u32 pc = 0;
  UReg_MSR msr;
  UReg_FPSCR fpscr;
  UGeckoInstruction dummy_;
  PairedSingle ps[32];
  struct CRStub { void SetField(u32, u32) {} } cr;
  void UpdateCR1() {}
  void UpdateFPRFDouble(double d) { fpscr.FPRF = Common::ClassifyDouble(d); }
  void UpdateFPRFSingle(float f) { fpscr.FPRF = Common::ClassifyFloat(f); }
};
struct MMU {  // flat host-endian "value" memory; endianness is irrelevant to the value semantics
  std::vector<unsigned char> mem = std::vector<unsigned char>(64);
  // big-endian guest memory, like Dolphin's MMU::Read<T>/Write<T>
  template <typename T> T Read(u32 a) { T v = 0; for (size_t i = 0; i < sizeof(T); i++) v = T((u64(v) << 8) | mem[a + i]); return v; }
  template <typename T> void Write(T v, u32 a) { for (size_t i = sizeof(T); i-- > 0;) { mem[a + i] = (unsigned char)v; v = T(u64(v) >> 8); } }
};
}
#define HID2(ppc_state) ((UReg_HID2&)(ppc_state).spr[SPR_HID2])
