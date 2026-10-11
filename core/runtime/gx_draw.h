/* gx_draw.h - internals shared by gx_draw.c and gx_tex.c */
#pragma once
#include <stdint.h>

int gx_tex_supported(int fmt);
uint32_t gx_tex_bytes(int fmt, int w, int h);
void gx_tex_decode(const uint8_t *src, int fmt, int w, int h, const uint8_t *tlut, int tlut_fmt, uint8_t *out);
