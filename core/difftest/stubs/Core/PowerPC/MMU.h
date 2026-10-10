#pragma once
// Stub MMU: flat MEM1 at EA 0x80000000/0xC0000000 (+24 MiB), big-endian. Anything else raises a DSI.
#include <cstring>
#include "Common/CommonTypes.h"
namespace PowerPC {
struct PowerPCState;
class MMU {
public:
  u8* mem = nullptr;
  u32* exceptions = nullptr;  // points at PowerPCState::Exceptions
  u8* Map(u32 ea, u32 size) {
    u32 seg = ea >> 29, off = ea & 0x1FFFFFFFu;
    if ((seg == 4u || seg == 6u) && off + size <= 0x01800000u) return mem + off;
    *exceptions |= 0x00000008;  // EXCEPTION_DSI
    return nullptr;
  }
  template <typename T> T Read(u32 a) {
    u8* p = Map(a, sizeof(T));
    if (!p) return 0;
    T v = 0;
    for (size_t i = 0; i < sizeof(T); i++) v = T((u64(v) << 8) | p[i]);
    return v;
  }
  template <typename T> void Write(T v, u32 a) {
    u8* p = Map(a, sizeof(T));
    if (!p) return;
    for (size_t i = sizeof(T); i-- > 0;) { p[i] = (u8)v; v = T(u64(v) >> 8); }
  }
  void Write_U16_Swap(u32 v, u32 a) { Write<u16>(__builtin_bswap16((u16)v), a); }
  void Write_U32_Swap(u32 v, u32 a) { Write<u32>(__builtin_bswap32(v), a); }
  void ClearDCacheLine(u32 a) { u8* p = Map(a & ~31u, 32); if (p) std::memset(p, 0, 32); }
  void StoreDCacheLine(u32) {}
  void InvalidateDCacheLine(u32) {}
  void FlushDCacheLine(u32) {}
  void DMA_MemoryToLC(u32, u32, u32) {}
  void DMA_LCToMemory(u32, u32, u32) {}
  void IBATUpdated() {}
  void DBATUpdated() {}
  void SDRUpdated() {}
  void SRUpdated() {}
  void InvalidateTLBEntry(u32) {}
};
}  // namespace PowerPC
