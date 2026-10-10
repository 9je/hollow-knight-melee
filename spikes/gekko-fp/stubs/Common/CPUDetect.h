#pragma once
struct CPUInfo { bool bFlushToZero = true; };  // x86-64 Dolphin: SSE FTZ capable => true
extern CPUInfo cpu_info;
