"""GameCube GX texture formats to RGBA8 (row-major, top row first)."""
import struct


class UnsupportedTexture(ValueError):
    pass


BLOCK = {0: (8, 8), 1: (8, 4), 2: (8, 4), 3: (4, 4), 4: (4, 4), 5: (4, 4), 6: (4, 4), 8: (8, 8), 9: (8, 4), 14: (8, 8)}


def _rgb565(v):
    r, g, b = v >> 11, (v >> 5) & 0x3F, v & 0x1F
    return (r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2, 255)


def _rgb5a3(v):
    if v & 0x8000:
        r, g, b = (v >> 10) & 0x1F, (v >> 5) & 0x1F, v & 0x1F
        return (r << 3 | r >> 2, g << 3 | g >> 2, b << 3 | b >> 2, 255)
    a, r, g, b = (v >> 12) & 7, (v >> 8) & 0xF, (v >> 4) & 0xF, v & 0xF
    return (r * 17, g * 17, b * 17, a << 5 | a << 2 | a >> 1)


def _ia8(v):
    return (v & 0xFF,) * 3 + (v >> 8,)


def _palette(tlut, tlut_fmt):
    n = len(tlut) // 2
    conv = {0: _ia8, 1: _rgb565, 2: _rgb5a3}[tlut_fmt]
    return [conv(struct.unpack_from(">H", tlut, 2 * i)[0]) for i in range(n)]


def _cmpr_block(data, o):
    c0, c1, bits = struct.unpack_from(">HHI", data, o)
    p0, p1 = _rgb565(c0), _rgb565(c1)
    if c0 > c1:
        p2 = tuple((2 * a + b) // 3 for a, b in zip(p0[:3], p1[:3])) + (255,)
        p3 = tuple((a + 2 * b) // 3 for a, b in zip(p0[:3], p1[:3])) + (255,)
    else:
        p2 = tuple((a + b) // 2 for a, b in zip(p0[:3], p1[:3])) + (255,)
        p3 = (0, 0, 0, 0)
    pal = (p0, p1, p2, p3)
    return [pal[(bits >> (30 - 2 * i)) & 3] for i in range(16)]


def decode(fmt, w, h, data, tlut=None, tlut_fmt=None) -> bytes:
    if fmt not in BLOCK:
        raise UnsupportedTexture(f"GX texture format {fmt}")
    bw, bh = BLOCK[fmt]
    out = bytearray(w * h * 4)
    pal = _palette(tlut, tlut_fmt) if fmt in (8, 9) else None
    o = 0

    def put(x, y, px):
        if x < w and y < h:
            out[4 * (y * w + x):4 * (y * w + x) + 4] = bytes(px)

    for by in range(0, h, bh):
        for bx in range(0, w, bw):
            if fmt == 14:   # CMPR: 8x8 tile of four 4x4 DXT1 blocks
                for sub in range(4):
                    px = _cmpr_block(data, o)
                    o += 8
                    sx, sy = bx + (sub & 1) * 4, by + (sub >> 1) * 4
                    for i in range(16):
                        put(sx + i % 4, sy + i // 4, px[i])
                continue
            if fmt == 6:    # RGBA8: AR plane then GB plane, 64 bytes per 4x4 block
                for i in range(16):
                    a, r = data[o + 2 * i], data[o + 2 * i + 1]
                    g, b = data[o + 32 + 2 * i], data[o + 33 + 2 * i]
                    put(bx + i % 4, by + i // 4, (r, g, b, a))
                o += 64
                continue
            for y in range(bh):
                for x in range(bw):
                    if fmt in (0, 8):   # 4 bits per texel
                        v = data[o] >> 4 if x % 2 == 0 else data[o] & 0xF
                        if x % 2 == 1:
                            o += 1
                        px = pal[v] if fmt == 8 else (v * 17,) * 3 + (255,)
                    elif fmt in (1, 2, 9):
                        v = data[o]; o += 1
                        px = pal[v] if fmt == 9 else (v,) * 3 + (255,) if fmt == 1 else ((v & 0xF) * 17,) * 3 + ((v >> 4) * 17,)
                    else:
                        v = struct.unpack_from(">H", data, o)[0]; o += 2
                        px = {3: _ia8, 4: _rgb565, 5: _rgb5a3}[fmt](v)
                    put(bx + x, by + y, px)
    return bytes(out)
