/* melee_core.h - libmelee_core C API (spec §3.1). One instance per process; not thread-safe.
 * Every function returning int: 0 = ok, -1 = error (see mc_error()). After a guest error the
 * machine is dead and every call fails until mc_init succeeds again. */
#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

int mc_init(const char *mem1_path, const char *iso_path);   /* boots to the first frame boundary */
void mc_shutdown(void);
const char *mc_error(void);
int mc_step(void);                 /* runs exactly one frame */
uint64_t mc_frame(void);           /* frame boundaries reached since mc_init */
uint32_t mc_mem_crc32(void);       /* CRC-32 of MEM1 (determinism checks) */
uint32_t mc_peek32(uint32_t ea);   /* big-endian MEM1 read; 0 outside MEM1 or with no machine */

#ifdef __cplusplus
}
#endif
