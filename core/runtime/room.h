/* room.h - room collision API (plan 2): Melee collision data, validation, loading. */
#pragma once
#include <stddef.h>
#include <stdint.h>

#define MC_MAX_VERTS 2048
#define MC_MAX_LINES 1536
#define MC_MAX_JOINTS 256
enum { MC_FLOOR, MC_CEILING, MC_RIGHT_WALL, MC_LEFT_WALL, MC_DYNAMIC, MC_GROUPS };
#define MC_LINE_FLOOR 1u
#define MC_LINE_CEILING 2u
#define MC_LINE_RIGHT_WALL 4u
#define MC_LINE_LEFT_WALL 8u
#define MC_LO_PLATFORM 0x100u
#define MC_LO_LEDGE 0x200u
typedef struct { float x, y; } McVec2;
typedef struct { uint16_t v0, v1; int16_t prev0, next0, prev1, next1; uint16_t hi_flags, lo_flags; } McLine;
typedef struct { int16_t start[MC_GROUPS], count[MC_GROUPS]; float left, bottom, right, top; int16_t vtx_start, vtx_count; } McJoint;
typedef struct {
  const McVec2 *verts; int n_verts;
  const McLine *lines; int n_lines;
  int16_t start[MC_GROUPS], count[MC_GROUPS];
  const McJoint *joints; int n_joints;
  uint32_t x2c;
} McCollision;
int mc_check_collision(const McCollision *col, char *why, size_t why_size);   /* 0 ok, -1 + reason */
const McCollision *mc_get_collision(void);        /* live collision, copied out; valid until the next call */
int mc_load_collision(const McCollision *col);    /* validates and copies now; applied at the next frame boundary */

typedef struct { float left, right, top, bottom; } McBox;
int mc_set_bounds(const McBox *blast, const McBox *camera);   /* absolute world units; applied at once */
int mc_get_bounds(McBox *blast, McBox *camera);

enum { MC_SOLID, MC_PLATFORM };
typedef struct {
  int kind;              /* MC_SOLID: closed outline, either winding; MC_PLATFORM: open polyline, either direction */
  const McVec2 *pts; int n;
  uint8_t material;      /* 0..19 (Melee terrain id: footsteps, ice) */
} McShape;
/* Builds collision into library-owned storage (valid until the next call). 0 ok, -1 + reason in why. */
int mc_build_collision(const McShape *shapes, int n_shapes, const McCollision **out, char *why, size_t why_size);

/* Moves the port's fighter to (x, y) at the next frame boundary, keeping its action and velocity.
 * A grounded fighter is snapped to a floor within 10 units (Melee's spawn probe) or becomes airborne. */
int mc_place_fighter(int port, float x, float y);

