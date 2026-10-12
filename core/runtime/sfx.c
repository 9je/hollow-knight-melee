/* sfx.c - Melee's sound effect calls, observed by recompiler taps (runtime/taps.txt) and queued for the host.
 * Taps run at the entry of the tapped function with its arguments in r3..r7 and only read guest memory. */
#include "mc_internal.h"
#include "guest_fns.h"

#define ADDR_VOICES 0x804C45A0u     /* AXDriver_804C45A0: HSD_SM[0x60] */
#define SM_SIZE 0x34u
#define ADDR_VIDHIGH 0x804D7788u    /* vidhigh: next handle's high part */
#define ADDR_FREE 0x804D7790u       /* AXDriver_804D7790: free voice list head */
#define ADDR_NBANKS 0x804D77B0u     /* bank count */
#define ADDR_BANK_BASE 0x804D77B4u  /* u32 *: first script per bank */
#define ADDR_NSCRIPTS 0x804D77B8u   /* script count */
#define ADDR_MUTED 0x804D77CCu      /* muted mix channel mask */

static McSfx q[MC_SFX_QUEUE];
static int q_head, q_n;   /* oldest event, count */

void sfx_reset(void) { q_head = q_n = 0; }

static void push(McSfx e) {
  e.frame = (uint32_t)g_m->frames;
  if (q_n == MC_SFX_QUEUE) { q_head = (q_head + 1) % MC_SFX_QUEUE; q_n--; }   /* full: drop the oldest */
  q[(q_head + q_n) % MC_SFX_QUEUE] = e;
  q_n++;
}
static int in_mem1(uint32_t ea, uint32_t n) { return ea >= 0x80000000u && ea - 0x80000000u <= MEM1_SIZE - n; }

/* int HSD_AudioSFXStartParam(int id, u8 vol, u8 pan, int track, int channel): the checks it makes before taking a
 * voice, then the handle it will return: (vidhigh << 7) | voice index, -1 with no free voice. */
void tap_HSD_AudioSFXStartParam(Cpu *c) {
  if (g_m->standin_depth && !standin_hit_sound(c, (int32_t)c->gpr[3])) return;   /* the stand-in is never heard, but for hits on it */
  int32_t id = (int32_t)c->gpr[3], track = (int32_t)c->gpr[6], ch = (int32_t)c->gpr[7];
  int32_t nbanks = (int32_t)ld32(c, ADDR_NBANKS), nscripts = (int32_t)ld32(c, ADDR_NSCRIPTS);
  uint32_t base = ld32(c, ADDR_BANK_BASE);
  int32_t bank = id / 10000, member = id % 10000;
  if (bank >= nbanks || !in_mem1(base, 4u * (uint32_t)nbanks)) return;
  if (bank < 0) return;   /* Melee indexes before its table here; never seen */
  int32_t script = member + (int32_t)ld32(c, base + 4u * (uint32_t)bank);
  if (script >= nscripts) return;
  if (bank < nbanks - 1 && (int32_t)ld32(c, base + 4u * (uint32_t)(bank + 1)) <= script) return;
  if (track < 0 || track > 0xFF || ch < 0 || ch >= 0x10) return;
  if (ld32(c, ADDR_MUTED) & (1u << ch)) return;
  uint32_t v = ld32(c, ADDR_FREE);
  int32_t handle = -1;
  if (v >= ADDR_VOICES && v < ADDR_VOICES + 0x60u * SM_SIZE)
    handle = (int32_t)(ld32(c, ADDR_VIDHIGH) << 7 | (v - ADDR_VOICES) / SM_SIZE);
  push((McSfx){ .kind = MC_SFX_START, .id = (uint32_t)id, .vol = (uint8_t)c->gpr[4], .pan = (uint8_t)c->gpr[5],
                .track = (uint8_t)track, .channel = (uint8_t)ch, .handle = handle });
}
void tap_HSD_AudioSFXKeyOff(Cpu *c) {
  int32_t vid = (int32_t)c->gpr[3];
  if (vid < 0 || (vid & 0x7F) >= 0x60) return;
  push((McSfx){ .kind = MC_SFX_KEYOFF, .handle = vid });
}
void tap_HSD_AudioSFXKeyOffAll(Cpu *c) { (void)c; push((McSfx){ .kind = MC_SFX_KEYOFF_ALL, .handle = -1 }); }
void tap_HSD_AudioSFXKeyOffTrack(Cpu *c) {
  int32_t track = (int32_t)c->gpr[3];
  if (track < 0 || track > 0xFF) return;   /* no voice has it */
  push((McSfx){ .kind = MC_SFX_KEYOFF_TRACK, .track = (uint8_t)track, .handle = -1 });
}

int mc_read_sfx(McSfx out[], int max) {
  if (!g_m || g_m->dead || max < 0) return -1;
  int n = max < q_n ? max : q_n;
  for (int i = 0; i < n; i++) out[i] = q[(q_head + i) % MC_SFX_QUEUE];
  q_head = (q_head + n) % MC_SFX_QUEUE;
  q_n -= n;
  return n;
}
