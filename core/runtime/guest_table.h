#pragma once
#include "cpu.h"
#ifndef GUEST_FN_DEFINED
#define GUEST_FN_DEFINED
typedef uint32_t (*GuestFn)(Cpu *);
#endif
typedef struct { uint32_t addr; GuestFn fn; } GuestEntry;
extern const GuestEntry g_guest_entries[];
extern const size_t g_guest_nentries;
GuestFn guest_lookup(uint32_t addr);   /* binary search, NULL if absent */
