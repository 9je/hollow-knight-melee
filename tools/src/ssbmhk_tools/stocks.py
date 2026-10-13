"""Melee's stock icons (the character heads by the damage percent), from IfAll.usd in your ISO, into stocks.bin for the
mod's character choice and shop (not for distribution). IfAll's Stc_scemdls is a scene model set whose material
animation's texture animation holds every icon (24x24, CI4) with its palette; the first 26 are the characters in their
default colours. stocks.bin: "STCK", width, height, count (u32 little-endian), then count RGBA images (top row first) in
Melee's character id order (0 Captain Falcon ... 25 Ganondorf)."""
import argparse
import struct
from pathlib import Path

from . import gciso, gxtex, hsd

CHARACTERS = 26


def icon_index(character: int) -> int:
    """IfAll's icon for a Melee character id: 0-18 in order, Falco..Ganondorf (20-25) one place early, Sheik (19) last."""
    return 25 if character == 19 else character - 1 if character > 19 else character


def _texanims(d: hsd.Dat, maj: int, acc: list, depth: int = 0) -> None:
    if not maj or depth > 64:
        return
    child, nxt, matanim = d.u32(maj), d.u32(maj + 4), d.u32(maj + 8)
    m = matanim
    while m:
        ta = d.u32(m + 8)
        while ta:
            acc.append(ta)
            ta = d.u32(ta)
        m = d.u32(m)
    _texanims(d, child, acc, depth + 1)
    _texanims(d, nxt, acc, depth + 1)


def _image_size(fmt: int, w: int, h: int) -> int:
    """Bytes of a GX texture, its width and height rounded up to whole tiles."""
    bw, bh, bpp = {8: (8, 8, 4), 9: (8, 4, 8), 0: (8, 8, 4), 1: (8, 4, 8), 2: (8, 4, 8), 3: (4, 4, 16), 4: (4, 4, 16),
                   5: (4, 4, 16), 6: (4, 4, 32), 14: (8, 8, 4)}[fmt]
    return ((w + bw - 1) // bw * bw) * ((h + bh - 1) // bh * bh) * bpp // 8


def extract(iso: Path) -> tuple[int, int, list[bytes]]:
    d = hsd.Dat(gciso.read_file(iso, "IfAll.usd"))
    sms = d.u32(d.roots["Stc_scemdls"])
    maj_table = d.u32(sms + 8)
    acc: list = []
    _texanims(d, d.u32(maj_table), acc)
    ta = next(t for t in acc if d.u16(t + 20) >= CHARACTERS)
    imgtbl, tluttbl, ntlut = d.u32(ta + 12), d.u32(ta + 16), d.u16(ta + 22)
    w = h = 0
    icons = []
    for c in range(CHARACTERS):
        j = icon_index(c)
        desc = d.u32(imgtbl + 4 * j)
        ptr, w, h, fmt = d.u32(desc), d.u16(desc + 4), d.u16(desc + 6), d.u32(desc + 8)
        tlut = tfmt = None
        if ntlut:
            t = d.u32(tluttbl + 4 * j)
            tptr, tfmt, n = d.u32(t), d.u32(t + 4), d.u16(t + 12)
            tlut = d.d[tptr:tptr + 2 * n]
        icons.append(gxtex.decode(fmt, w, h, d.d[ptr:ptr + _image_size(fmt, w, h)], tlut, tfmt))
    return w, h, icons


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Extract Melee's stock icons from your ISO into stocks.bin (not for distribution).")
    ap.add_argument("iso", type=Path)
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args(argv)
    w, h, icons = extract(a.iso)
    a.out.mkdir(parents=True, exist_ok=True)
    (a.out / "stocks.bin").write_bytes(b"STCK" + struct.pack("<III", w, h, len(icons)) + b"".join(icons))
    print(f"stocks.bin: {len(icons)} icons, {w}x{h}")
    return 0
