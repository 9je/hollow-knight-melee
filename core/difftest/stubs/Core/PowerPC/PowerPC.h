#pragma once
// Stub of Dolphin's PowerPC.h for the difftest oracle: the CPU state the interpreter files touch,
// with GetXER/SetXER etc. copied verbatim from the pinned commit.
#include <array>
#include <bit>
#include <cstring>
#include <type_traits>
#include <xmmintrin.h>
#include "Common/CommonTypes.h"
#include "Common/FloatUtils.h"
#include "Core/PowerPC/ConditionRegister.h"
#include "Core/PowerPC/Gekko.h"
#include "Core/PowerPC/MMU.h"

struct PairedSingle {
  u64 PS0AsU64() const { return ps0; }
  u64 PS1AsU64() const { return ps1; }
  u32 PS0AsU32() const { return u32(ps0); }
  u32 PS1AsU32() const { return u32(ps1); }
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
enum : u32 { CR_EMU_SO_BIT_ = 0 };
struct ICacheStub {
  template <class... A> void Invalidate(A&&...) {}
  template <class... A> void Reset(A&&...) {}
};
struct PowerPCState {
  u32 pc = 0;
  u32 npc = 0;
  u32 gpr[32]{};
  alignas(16) PairedSingle ps[32];
  ConditionRegister cr{};
  UReg_MSR msr;
  UReg_FPSCR fpscr;
  u32 Exceptions = 0;
  int downcount = 0;
  u8 xer_ca = 0;
  u8 xer_so_ov = 0;
  u16 xer_stringctrl = 0;
  u32 reserve_address = 0;
  bool reserve = false;
  bool pagetable_update_pending = false;
  bool m_enable_dcache = false;
  std::array<u32, 16> sr{};
  alignas(8) u32 spr[1024]{};
  ICacheStub iCache;

  void UpdateCR1() { cr.SetField(1, (fpscr.FX << 3) | (fpscr.FEX << 2) | (fpscr.VX << 1) | fpscr.OX); }
  void SetCarry(u32 ca) { xer_ca = ca; }
  u32 GetCarry() const { return xer_ca; }
  UReg_XER GetXER() const {
    u32 xer = 0;
    xer |= xer_stringctrl;
    xer |= xer_ca << XER_CA_SHIFT;
    xer |= xer_so_ov << XER_OV_SHIFT;
    return UReg_XER{xer};
  }
  void SetXER(UReg_XER new_xer) {
    xer_stringctrl = new_xer.BYTE_COUNT + (new_xer.BYTE_CMP << 8);
    xer_ca = new_xer.CA;
    xer_so_ov = (new_xer.SO << 1) + new_xer.OV;
  }
  u32 GetXER_SO() const { return xer_so_ov >> 1; }
  void SetXER_SO(bool value) { xer_so_ov |= static_cast<u32>(value) << 1; }
  u32 GetXER_OV() const { return xer_so_ov & 1; }
  void SetXER_OV(bool value) { xer_so_ov = (xer_so_ov & 0xFE) | static_cast<u32>(value); SetXER_SO(value); }
  void UpdateFPRFDouble(double d) { fpscr.FPRF = Common::ClassifyDouble(d); }
  void UpdateFPRFSingle(float f) { fpscr.FPRF = Common::ClassifyFloat(f); }
};

class PowerPCManager {
public:
  void MSRUpdated() {}
  void CheckExceptions() {}
  void WriteFullTimeBaseValue(u64) {}
};

// As Dolphin's SetSIMDMode on x86-64: MXCSR.FTZ follows FPSCR.NI, MXCSR.RC follows FPSCR.RN.
inline void RoundingModeUpdated(PowerPCState& s) {
  static const u32 rc[4] = {0x0000, 0x6000, 0x4000, 0x2000};
  _mm_setcsr(0x1F80u | rc[s.fpscr.Hex & 3] | ((s.fpscr.Hex & 4) ? 0x8000u : 0u));
}
inline void MMCRUpdated(PowerPCState&) {}
inline void RecalculateAllFeatureFlags(PowerPCState&) {}
}  // namespace PowerPC

#define HID0(ppc_state) ((UReg_HID0&)(ppc_state).spr[SPR_HID0])
#define HID2(ppc_state) ((UReg_HID2&)(ppc_state).spr[SPR_HID2])
#define HID4(ppc_state) ((UReg_HID4&)(ppc_state).spr[SPR_HID4])
#define DMAU(ppc_state) (*(UReg_DMAU*)&(ppc_state).spr[SPR_DMAU])
#define DMAL(ppc_state) (*(UReg_DMAL*)&(ppc_state).spr[SPR_DMAL])
#define MMCR0(ppc_state) ((UReg_MMCR0&)(ppc_state).spr[SPR_MMCR0])
#define MMCR1(ppc_state) ((UReg_MMCR1&)(ppc_state).spr[SPR_MMCR1])
#define THRM1(ppc_state) ((UReg_THRM12&)(ppc_state).spr[SPR_THRM1])
#define THRM2(ppc_state) ((UReg_THRM12&)(ppc_state).spr[SPR_THRM2])
#define THRM3(ppc_state) ((UReg_THRM3&)(ppc_state).spr[SPR_THRM3])
#define LR(ppc_state) (ppc_state).spr[SPR_LR]
#define CTR(ppc_state) (ppc_state).spr[SPR_CTR]
#define SRR0(ppc_state) (ppc_state).spr[SPR_SRR0]
#define SRR1(ppc_state) (ppc_state).spr[SPR_SRR1]
#define GQR(ppc_state, x) (ppc_state).spr[SPR_GQR0 + (x)]
#define TL(ppc_state) (ppc_state).spr[SPR_TL]
#define TU(ppc_state) (ppc_state).spr[SPR_TU]
