/* gates.c - move gating (spec §6, mc_set_gates): Melee's own fields and dispatch, per port */
#include <stdio.h>
#include "mc_internal.h"
#include "melee_core.h"

#define PLAYER_BLOCK(p) (0x80453080u + 0xE90u * (uint32_t)(p))
/* ftData_Special*: per fighter kind, the function a special's input calls (indexed by Fighter.kind, +0x04) */
static const struct { uint32_t table, gate; } specials[] = {
  {0x803C167Cu, MC_GATE_SPECIAL_N}, {0x803C15F8u, MC_GATE_SPECIAL_N},     /* SpecialN, SpecialAirN */
  {0x803C13E8u, MC_GATE_SPECIAL_S}, {0x803C1574u, MC_GATE_SPECIAL_S},     /* SpecialS, SpecialAirS */
  {0x803C1784u, MC_GATE_SPECIAL_HI}, {0x803C146Cu, MC_GATE_SPECIAL_HI},   /* SpecialHi, SpecialAirHi */
  {0x803C1700u, MC_GATE_SPECIAL_LW}, {0x803C14F0u, MC_GATE_SPECIAL_LW},   /* SpecialLw, SpecialAirLw */
};

int mc_set_gates(int port, uint32_t locked) {
  Machine *m = g_m;
  if (!m || m->dead || port < 0 || port > 3) return -1;
  m->gates[port] = locked;
  m->any_gates = (m->gates[0] | m->gates[1] | m->gates[2] | m->gates[3]) != 0;
  return 0;
}

/* at each frame boundary: the jump and wall-jump gates, which Melee keeps in the fighter */
void gates_boundary_jobs(Cpu *c) {
  Machine *m = g_m;
  for (int p = 0; p < 4; p++) {
    uint32_t gobj = ld32(c, PLAYER_BLOCK(p) + 0xB0u);
    if (!gobj) continue;
    uint32_t fp = ld32(c, gobj + 0x2C), g = m->gates[p];
    if (!g && !m->walljump_off[p]) continue;
    if ((g & MC_GATE_DOUBLE_JUMP) && ld32(c, fp + 0xE0)) {          /* airborne: every jump counts as used */
      uint32_t max = ld32(c, fp + 0x168);                            /* co_attrs.max_jumps */
      if (max < 256 && ld8(c, fp + 0x1968) < max) st8(c, fp + 0x1968, (uint8_t)max);
    }
    uint32_t b = ld8(c, fp + 0x2224);                                /* can_walljump: bit 0x01 */
    if (g & MC_GATE_WALL_JUMP) {
      if (b & 1u) { m->walljump_off[p] = 1; st8(c, fp + 0x2224, b & ~1u); }
    } else if (m->walljump_off[p]) {
      m->walljump_off[p] = 0; st8(c, fp + 0x2224, b | 1u);
    }
  }
}

/* in guest_call_indirect: a locked special's entry, called for a port's fighter, does nothing */
int gates_block(Cpu *c, uint32_t target) {
  Machine *m = g_m;
  uint32_t gobj = c->gpr[3];
  for (int p = 0; p < 4; p++) {
    if (!m->gates[p] || gobj != ld32(c, PLAYER_BLOCK(p) + 0xB0u)) continue;
    uint32_t kind = ld32(c, ld32(c, gobj + 0x2C) + 0x04u);
    if (kind > 0x20) return 0;
    for (unsigned k = 0; k < sizeof specials / sizeof specials[0]; k++)
      if ((m->gates[p] & specials[k].gate) && ld32(c, specials[k].table + 4u * kind) == target) return 1;
    return 0;
  }
  return 0;
}
