"""Melee's character select, from MnSlChr.usd in your ISO, into css.bin for the mod's bench select (not for distribution):
the 25 select tiles (64x56, the faces with their name plates, in the select's original order), the character portraits
(136x188, one per costume), and the stock icons of every costume (24x24). MnSelectChrDataTable's models hold the tiles
(one texture each); its texture animations hold the portraits and stock icons, costume-major: every character's first
costume, then every character's second, and so on (the portraits skip Sheik, as Melee's select does).

css.bin: "CSS1"; then three sections, each u32 width, height, count followed by count entries of u8 character (Melee's
external id), u8 costume, u16 0, and the RGBA image (top row first): tiles, portraits, stock icons."""
import argparse
import struct
from pathlib import Path

from . import gciso, gxtex, hsd
from .stocks import _image_size, _texanims

# Melee's costumes per character (external id): the Pl<Ch><Co>.dat files in the ISO; G&W's colours are code, not files
COSTUMES = {0: 6, 1: 5, 2: 4, 3: 4, 4: 6, 5: 4, 6: 5, 7: 4, 8: 5, 9: 5, 10: 4, 11: 4, 12: 5, 13: 4, 14: 4, 15: 5,
            16: 5, 17: 6, 18: 5, 19: 5, 20: 4, 21: 5, 22: 5, 23: 5, 24: 4, 25: 5}
# the select tiles as the file keeps them: Melee's original layout (Mario .. Ness, Ice Climbers .. G&W, Marth .. Dr. Mario)
TILE_ORDER = [8, 7, 5, 12, 17, 1, 0, 2, 11, 14, 4, 16, 18, 6, 13, 15, 10, 3, 9, 23, 24, 21, 20, 25, 22]
TILE, PORTRAIT, STOCK = (64, 56, 9), (136, 188, 9), (24, 24, 8)


def costume_blocks(characters: list[int]) -> list[tuple[int, int]]:
    """(character, costume) in the texture animations' order: costume-major over `characters`, a character present in
    the costume's block while it has that costume."""
    out = []
    for costume in range(6):
        out += [(c, costume) for c in characters if COSTUMES[c] > costume]
    return out


PORTRAIT_CHARACTERS = [c for c in range(26) if c != 19]
STOCK_CHARACTERS = list(range(26))


def _images(d: hsd.Dat, ta: int) -> list[bytes]:
    n, imgtbl, tluttbl, ntlut = d.u16(ta + 20), d.u32(ta + 12), d.u32(ta + 16), d.u16(ta + 22)
    out = []
    for j in range(n):
        desc = d.u32(imgtbl + 4 * j)
        ptr, w, h, fmt = d.u32(desc), d.u16(desc + 4), d.u16(desc + 6), d.u32(desc + 8)
        tlut = tfmt = None
        if ntlut:
            t = d.u32(tluttbl + 4 * j)
            tptr, tfmt, cnt = d.u32(t), d.u32(t + 4), d.u16(t + 12)
            tlut = d.d[tptr:tptr + 2 * cnt]
        out.append(gxtex.decode(fmt, w, h, d.d[ptr:ptr + _image_size(fmt, w, h)], tlut, tfmt))
    return out


def _texanim_of(d: hsd.Dat, table: int, dsize: int, kind: tuple, count: int) -> int:
    """The data table's first texture animation of `count` images of `kind` (w, h, fmt)."""
    for k in range(64):
        p = d.u32(table + 4 * k)
        if not p or p >= dsize:
            continue
        acc: list = []
        try:
            _texanims(d, p, acc)
        except (struct.error, IndexError):
            continue
        for ta in acc:
            if d.u16(ta + 20) != count:
                continue
            desc = d.u32(d.u32(ta + 12))
            if (d.u16(desc + 4), d.u16(desc + 6), d.u32(desc + 8)) == kind:
                return ta
    raise ValueError(f"MnSlChr.usd: no texture animation of {count} {kind[0]}x{kind[1]} images")


def _tiles(d: hsd.Dat, raw: bytes) -> list[bytes]:
    """The 26 unique 64x56 CI8 textures (25 characters and the random tile), in file order, each with the palette of a
    texture object that uses it (the image field at +0x4C, the palette at +0x50)."""
    dsize, nreloc = struct.unpack_from(">II", raw, 4)
    relocs = struct.unpack_from(f">{nreloc}I", d.d, dsize)
    by_value: dict = {}
    for f in relocs:
        by_value.setdefault(d.u32(f), []).append(f)
    tiles, seen = [], set()
    for o in sorted(relocs):
        if o + 12 > dsize:
            continue
        ptr, w, h, fmt = d.u32(o), d.u16(o + 4), d.u16(o + 6), d.u32(o + 8)
        if (w, h, fmt) != TILE or ptr in seen or ptr >= dsize:
            continue
        tl = next((d.u32(f + 4) for f in by_value.get(o, []) if 0 < d.u32(f + 4) < dsize), None)
        if tl is None:
            continue
        seen.add(ptr)
        n = d.u16(tl + 0xC)
        tiles.append(gxtex.decode(fmt, w, h, d.d[ptr:ptr + _image_size(fmt, w, h)], d.d[d.u32(tl):d.u32(tl) + 2 * n], d.u32(tl + 4)))
    return tiles


def extract(iso: Path) -> dict:
    raw = gciso.read_file(iso, "MnSlChr.usd")
    d = hsd.Dat(raw)
    dsize = struct.unpack_from(">I", raw, 4)[0]
    table = d.roots["MnSelectChrDataTable"]
    tiles = _tiles(d, raw)
    if len(tiles) < len(TILE_ORDER):
        raise ValueError(f"MnSlChr.usd: {len(tiles)} select tiles, expected {len(TILE_ORDER)} or more")
    portraits = _images(d, _texanim_of(d, table, dsize, PORTRAIT, len(costume_blocks(PORTRAIT_CHARACTERS))))
    stocks = _images(d, _texanim_of(d, table, dsize, STOCK, STOCK_IMAGES))
    return {
        "tiles": (TILE[:2], [(c, 0, tiles[i]) for i, c in enumerate(TILE_ORDER)]),
        "portraits": (PORTRAIT[:2], [(c, k, img) for (c, k), img in zip(costume_blocks(PORTRAIT_CHARACTERS), portraits)]),
        "stocks": (STOCK[:2], stock_entries(stocks)),
    }


STOCK_IMAGES = 129   # the stock animation: 24x24 icons for every costume, then a few 32x32 symbols (the smash ball, hands)


def stock_entries(images: list[bytes]) -> list[tuple[int, int, bytes]]:
    """(character, costume, icon) for the 24x24 icons: the first 26 are every character's first costume in external-id
    order; a later icon is the character whose icon's shape (its alpha) it has, with the next costume number."""
    size = STOCK[0] * STOCK[1] * 4
    icons = [img for img in images if len(img) == size]
    masks = [bytes(1 if a else 0 for a in img[3::4]) for img in icons[:26]]
    out, seen = [], {c: 0 for c in range(26)}
    for img in icons:
        mask = bytes(1 if a else 0 for a in img[3::4])
        c = min(range(26), key=lambda k: sum(x != y for x, y in zip(mask, masks[k])))
        out.append((c, seen[c], img))
        seen[c] += 1
    return out


def pack(sections: dict) -> bytes:
    out = [b"CSS1"]
    for key in ("tiles", "portraits", "stocks"):
        (w, h), entries = sections[key]
        out.append(struct.pack("<III", w, h, len(entries)))
        for c, k, img in entries:
            out.append(struct.pack("<BBH", c, k, 0) + img)
    return b"".join(out)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Extract Melee's character select (tiles, portraits, stock icons) from your ISO into css.bin (not for distribution).")
    ap.add_argument("iso", type=Path)
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args(argv)
    sections = extract(a.iso)
    a.out.mkdir(parents=True, exist_ok=True)
    (a.out / "css.bin").write_bytes(pack(sections))
    print("css.bin: " + ", ".join(f"{len(e)} {k}" for k, ((w, h), e) in sections.items()))
    return 0
