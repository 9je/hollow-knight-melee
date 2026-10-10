#include "guest_table.h"
GuestFn guest_lookup(uint32_t addr) {
  size_t lo = 0, hi = g_guest_nentries;
  while (lo < hi) {
    size_t mid = (lo + hi) / 2;
    if (g_guest_entries[mid].addr < addr) lo = mid + 1; else hi = mid;
  }
  return lo < g_guest_nentries && g_guest_entries[lo].addr == addr ? g_guest_entries[lo].fn : 0;
}
