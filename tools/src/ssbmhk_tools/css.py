"""Melee's character select, from MnSlChr.usd in your ISO, into css.bin for the mod's bench select (not for distribution):
the 25 select tiles (64x56, the faces with their name plates, in the select's original order), the character portraits
(136x188, one per costume), and the stock icons of every costume (24x24). MnSelectChrDataTable's models hold the tiles
(one texture each); its texture animations hold the portraits and stock icons, costume-major: every character's first
costume, then every character's second, and so on (the portraits skip Sheik, as Melee's select does, and keep their
fifth and sixth costumes in an order of their own).

Sheik has no tile or portrait in Melee's select (she is Zelda's transformation); given frames of her rendered by the
core (frame_dump's raster, one per costume, and one facing the camera), she gets a portrait made of each and a tile
made of her head on Zelda's tile's border and plate, her name in the lettering cut from the other tiles' plates
(--sheik-frames, --sheik-head), so she stands in the select like the rest. The standard library only: the player's
installer makes it with an embeddable Python.

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
# the portraits' fifth and sixth costumes are not costume-major: Melee's file keeps them in this order (checked against
# each costume's stock icon)
PORTRAIT_TAIL = [(0, 4), (1, 4), (4, 4), (6, 4), (8, 4), (16, 4), (17, 4), (18, 4), (21, 4), (22, 4),
                 (0, 5), (4, 5), (17, 5), (12, 4), (9, 4), (15, 4), (25, 4), (23, 4)]


def portrait_order() -> list[tuple[int, int]]:
    """(character, costume) of each portrait in the file: the first four costumes costume-major, then PORTRAIT_TAIL."""
    head = [(c, k) for c, k in costume_blocks(PORTRAIT_CHARACTERS) if k < 4]
    return head + PORTRAIT_TAIL
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
    portraits = _images(d, _texanim_of(d, table, dsize, PORTRAIT, len(portrait_order())))
    stocks = _images(d, _texanim_of(d, table, dsize, STOCK, STOCK_IMAGES))
    return {
        "tiles": (TILE[:2], [(c, 0, tiles[i]) for i, c in enumerate(TILE_ORDER)]),
        "portraits": (PORTRAIT[:2], [(c, k, img) for (c, k), img in zip(portrait_order(), portraits)]),
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


SHEIK = 19
# the tiles whose name plates spell Sheik's letters: S and E from Bowser's, H from Yoshi's, I and K from Link's
LETTER_TILES = {5: "BOWSER", 6: "LINK", 17: "YOSHI"}
HEAD_ZOOM, HEAD_TOP = 0.9, 0.05                     # the tile's face: this much of the head's height, from this far down it
LETTER_GAP = 4                                      # between the letters of a name on a plate, as Melee spaces short names


class Rgba:
    """An RGBA image (top row first) and the little that Sheik's art needs of one; the standard library only, so the
    player's installer (an embeddable Python) can make the art."""

    def __init__(self, w: int, h: int, px=None):
        self.w, self.h = w, h
        self.px = bytearray(px) if px is not None else bytearray(w * h * 4)

    @classmethod
    def load(cls, path) -> "Rgba":
        """frame_dump's raster: u32 width, u32 height, RGBA."""
        b = Path(path).read_bytes()
        w, h = struct.unpack_from("<II", b)
        if len(b) != 8 + w * h * 4:
            raise ValueError(f"{path}: {len(b)} bytes for a {w}x{h} raster")
        return cls(w, h, b[8:])

    def save(self, path) -> None:
        Path(path).write_bytes(struct.pack("<II", self.w, self.h) + bytes(self.px))

    def get(self, x: int, y: int) -> tuple:
        i = 4 * (y * self.w + x)
        return tuple(self.px[i:i + 4])

    def bbox(self):
        """(x0, y0, x1, y1) around the drawn pixels (alpha > 0), the end exclusive; None when nothing is drawn."""
        x0, y0, x1, y1 = self.w, self.h, -1, -1
        for y in range(self.h):
            row = self.px[4 * y * self.w + 3:4 * (y + 1) * self.w:4]
            if not any(row):
                continue
            xs = [x for x, a in enumerate(row) if a]
            x0, x1 = min(x0, xs[0]), max(x1, xs[-1])
            y0, y1 = min(y0, y), max(y1, y)
        return None if x1 < 0 else (x0, y0, x1 + 1, y1 + 1)

    def crop(self, box) -> "Rgba":
        """The box (x0, y0, x1, y1), the end exclusive; outside the image is transparent."""
        x0, y0, x1, y1 = box
        out = Rgba(x1 - x0, y1 - y0)
        for y in range(max(y0, 0), min(y1, self.h)):
            sx0, sx1 = max(x0, 0), min(x1, self.w)
            if sx1 > sx0:
                d = 4 * ((y - y0) * out.w + sx0 - x0)
                out.px[d:d + 4 * (sx1 - sx0)] = self.px[4 * (y * self.w + sx0):4 * (y * self.w + sx1)]
        return out

    def resize(self, w: int, h: int) -> "Rgba":
        """Box-filtered: each new pixel averages the source area it covers, its colour weighted by alpha."""
        out = Rgba(w, h)
        for y in range(h):
            sy0, sy1 = y * self.h / h, (y + 1) * self.h / h
            for x in range(w):
                sx0, sx1 = x * self.w / w, (x + 1) * self.w / w
                r = g = b = a = area = 0.0
                for sy in range(int(sy0), min(self.h, int(-(-sy1 // 1)))):
                    fy = min(sy1, sy + 1) - max(sy0, sy)
                    if fy <= 0:
                        continue
                    for sx in range(int(sx0), min(self.w, int(-(-sx1 // 1)))):
                        fx = min(sx1, sx + 1) - max(sx0, sx)
                        if fx <= 0:
                            continue
                        f = fx * fy
                        i = 4 * (sy * self.w + sx)
                        pa = self.px[i + 3] * f
                        r += self.px[i] * pa; g += self.px[i + 1] * pa; b += self.px[i + 2] * pa; a += pa; area += f
                i = 4 * (y * w + x)
                if a > 0:
                    out.px[i:i + 4] = bytes((int(r / a + 0.5), int(g / a + 0.5), int(b / a + 0.5), int(a / area + 0.5)))
        return out

    def fill(self, box, color) -> None:
        x0, y0, x1, y1 = box
        row = bytes(color) * (x1 - x0)
        for y in range(y0, y1):
            self.px[4 * (y * self.w + x0):4 * (y * self.w + x1)] = row

    def paste(self, img: "Rgba", x: int, y: int) -> None:
        """img over this image at (x, y): alpha blended."""
        for sy in range(img.h):
            dy = y + sy
            if not 0 <= dy < self.h:
                continue
            for sx in range(img.w):
                dx = x + sx
                if not 0 <= dx < self.w:
                    continue
                si, di = 4 * (sy * img.w + sx), 4 * (dy * self.w + dx)
                a = img.px[si + 3]
                if a == 0:
                    continue
                if a == 255:
                    self.px[di:di + 4] = img.px[si:si + 4]
                    continue
                da = self.px[di + 3]
                oa = a + da * (255 - a) // 255
                for c in range(3):
                    self.px[di + c] = (img.px[si + c] * a + self.px[di + c] * da * (255 - a) // 255) // max(oa, 1)
                self.px[di + 3] = oa


def _lit(px: tuple) -> bool:
    """A letter's pixel on a name plate (the letters are light grey, the plates dark)."""
    return px[3] > 0 and px[0] + px[1] + px[2] > 350


def tile_letters(tiles: list) -> dict:
    """The letters of the names on Melee's select tiles (LETTER_TILES), each cut out of its plate as an Rgba whose
    non-letter pixels are transparent (its `top`: the row the text starts on): the plate's text, in rows 42..51 between the border columns, read as runs of
    columns with a lit pixel; a plate whose runs don't match its name (letters touching) gives no letters."""
    letters = {}
    tw, th = TILE[:2]
    for c, _, img in tiles:
        name = LETTER_TILES.get(c)
        if not name or len(img) != tw * th * 4:
            continue
        tile = Rgba(tw, th, img)
        lit = [[_lit(tile.get(x, y)) for y in range(42, 52)] for x in range(tw)]
        runs, x = [], 3
        while x < tw - 3:
            if any(lit[x]):
                x0 = x
                while x < tw - 3 and any(lit[x]):
                    x += 1
                runs.append((x0, x))
            else:
                x += 1
        if len(runs) != len(name):
            continue
        rows = [y for y in range(42, 52) if any(lit[x][y - 42] for x in range(3, tw - 3))]
        y0, y1 = rows[0], rows[-1] + 1
        for (x0, x1), ch in zip(runs, name):
            glyph = Rgba(x1 - x0, y1 - y0)
            for y in range(y0, y1):
                for x in range(x0, x1):
                    px = tile.get(x, y)
                    if _lit(px):
                        glyph.px[4 * ((y - y0) * glyph.w + x - x0):4 * ((y - y0) * glyph.w + x - x0) + 4] = bytes(px)
            glyph.top = y0                           # the row the plate's text starts on
            letters.setdefault(ch, glyph)
    return letters


def sheik_art(frames: list, base_tile: bytes, letters: dict, head_frame=None) -> tuple[bytes, list[bytes]]:
    """Sheik's tile (64x56) and portraits (136x188, one per frame) from transparent renders of her (frame_dump's raster),
    one per costume: each portrait is the figure fitted into the portrait, the tile is her head (from `head_frame`, a
    render where she faces the camera; the first frame's otherwise) inside `base_tile`'s border, over nothing (the
    select's backdrop shows through, as around every other head), with
    her name on its plate in `letters` (tile_letters: Melee's own lettering; the plate stays blank when a letter is
    missing)."""
    portraits, head = [], None
    if head_frame is not None:
        img = Rgba.load(head_frame)
        head = img.crop(img.bbox() or (0, 0, img.w, img.h))
    pw, ph = PORTRAIT[:2]
    for i, f in enumerate(frames):
        img = Rgba.load(f)
        fig = img.crop(img.bbox() or (0, 0, img.w, img.h))
        scale = min((pw - 12) / fig.w, (ph - 8) / fig.h)
        fw, fh = max(1, round(fig.w * scale)), max(1, round(fig.h * scale))
        out = Rgba(pw, ph)
        out.paste(fig.resize(fw, fh), (pw - fw) // 2, ph - 4 - fh)
        portraits.append(bytes(out.px))
        if i == 0 and head is None:
            head = fig
    tw, th = TILE[:2]
    tile = Rgba(tw, th, base_tile)
    fx0, fy0, fx1, fy1 = 2, 2, tw - 2, 41          # the face, inside the border and above the plate
    # the face area is transparent in Melee's tiles (the select's grey shows through around every head): the base
    # tile's own head goes, its border's soft inner edge (the two pixels inside the frame) stays
    tile.fill((fx0 + 2, fy0 + 2, fx1 - 2, fy1), (0, 0, 0, 0))
    face = Rgba(fx1 - fx0, fy1 - fy0)
    hh = max(2, int(head.h * 0.24))                # the head: about the top quarter of the figure
    top = head.crop((0, 0, head.w, hh)).bbox()
    cx = (top[0] + top[2]) // 2 if top else head.w // 2
    rh = hh * HEAD_ZOOM                            # the face fills the tile, as the others' do
    rw = rh * face.w / face.h
    crop = head.crop((int(cx - rw / 2), int(hh * HEAD_TOP), int(cx + rw / 2), int(hh * HEAD_TOP + rh))).resize(face.w, face.h)
    face.paste(crop, 0, 0)
    tile.paste(face, fx0, fy0)
    plate = tile.get(4, 48)
    tile.fill((fx0, fy1, fx1, th - 3), plate)
    name = "SHEIK"
    if all(ch in letters for ch in name):
        glyphs = [letters[ch] for ch in name]
        x = round((tw - (sum(g.w for g in glyphs) + LETTER_GAP * (len(name) - 1))) / 2)
        for g in glyphs:
            tile.paste(g, x, g.top)
            x += g.w + LETTER_GAP
    return bytes(tile.px), portraits


def add_sheik(sections: dict, frames: list, head_frame=None) -> None:
    """Sheik's tile and portraits (sheik_art) into the sections, on Zelda's tile's border and plate (any tile's if hers is
    missing), her name in the other tiles' lettering."""
    tiles = sections["tiles"][1]
    base = next((img for c, _, img in tiles if c == 18), tiles[0][2] if tiles else bytes([0, 0, 0, 255]) * (TILE[0] * TILE[1]))
    tile, portraits = sheik_art(frames, base, tile_letters(tiles), head_frame)
    tiles.append((SHEIK, 0, tile))
    sections["portraits"][1].extend((SHEIK, k, p) for k, p in enumerate(portraits))


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
    ap.add_argument("--sheik-frames", type=Path, nargs="*", default=[], help="renders of Sheik, one per costume (frame_dump's raster, .rgba): her portraits, and her tile")
    ap.add_argument("--sheik-head", type=Path, help="a render of Sheik facing the camera (two frames into a turn): her tile's face (the first of --sheik-frames otherwise)")
    a = ap.parse_args(argv)
    sections = extract(a.iso)
    if a.sheik_frames:
        add_sheik(sections, a.sheik_frames, a.sheik_head)
    a.out.mkdir(parents=True, exist_ok=True)
    (a.out / "css.bin").write_bytes(pack(sections))
    print("css.bin: " + ", ".join(f"{len(e)} {k}" for k, ((w, h), e) in sections.items()))
    return 0
