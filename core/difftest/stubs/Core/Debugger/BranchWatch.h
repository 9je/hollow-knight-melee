#pragma once
#include "Common/CommonTypes.h"
#include "Core/PowerPC/Gekko.h"
namespace Core {
class BranchWatch {
public:
  bool GetRecordingActive() const { return false; }
  void HitTrue(u32, u32, UGeckoInstruction, bool) {}
  void HitFalse(u32, u32, UGeckoInstruction, bool) {}
};
}
