#pragma once
#include "cpu.h"
typedef struct { uint32_t word; const char *op; void (*fn)(Cpu *); uint8_t ea_kind, ra, rb; int32_t disp; } InsnCase;
extern const InsnCase g_cases[];
extern const size_t g_ncases;
