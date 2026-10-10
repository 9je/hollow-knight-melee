#pragma once
#include "Common/CommonTypes.h"
class JitInterface {
public:
  void InvalidateICacheLine(u32) {}
  void InvalidateICacheLines(u32, u32) {}
  void InvalidateICache(u32, u32, bool) {}
  void ClearSafe() {}
};
