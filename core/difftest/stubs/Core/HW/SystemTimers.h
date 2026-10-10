#pragma once
#include "Common/CommonTypes.h"
namespace SystemTimers {
class SystemTimersManager {
public:
  u32 GetFakeDecrementer() const { return 0; }
  u64 GetFakeTimeBase() const { return 0; }
  void TimeBaseSet() {}
  void DecrementerSet() {}
};
}
