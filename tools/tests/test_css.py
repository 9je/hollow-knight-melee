import struct

import pytest

from ssbmhk_tools import css


@pytest.fixture
def iso(dev_config):
    if not dev_config.iso.exists():
        pytest.skip("Melee ISO (dev.toml) not available")
    return dev_config.iso


def test_costume_blocks_are_costume_major_and_skip_short_characters():
    blocks = css.costume_blocks(css.PORTRAIT_CHARACTERS)
    assert len(blocks) == 118                       # 25 x 4, then the 15 with a fifth costume, the 3 with a sixth
    assert blocks[:3] == [(0, 0), (1, 0), (2, 0)]
    assert blocks[25] == (0, 1)
    assert (3, 4) not in blocks and (0, 5) in blocks and (4, 5) in blocks and (17, 5) in blocks
    assert len(css.costume_blocks(css.STOCK_CHARACTERS)) == 123


def test_tile_order_is_melees_original_layout():
    assert css.TILE_ORDER[:3] == [8, 7, 5] and len(set(css.TILE_ORDER)) == 25 and 19 not in css.TILE_ORDER


def test_extract_from_iso(iso, tmp_path):
    assert css.main([str(iso), "--out", str(tmp_path)]) == 0
    b = (tmp_path / "css.bin").read_bytes()
    assert b[:4] == b"CSS1"
    o = 4
    sizes = {}
    for key in ("tiles", "portraits", "stocks"):
        w, h, n = struct.unpack_from("<III", b, o)
        o += 12
        sizes[key] = (w, h, n)
        first = b[o:o + 4]
        o += n * (4 + w * h * 4)
        if key == "tiles":
            assert first[0] == 8                     # Mario's tile first
    assert o == len(b)
    assert sizes["tiles"] == (64, 56, 25) and sizes["portraits"] == (136, 188, 118)
    assert sizes["stocks"][:2] == (24, 24) and sizes["stocks"][2] >= 104
    # Fox's second-costume portrait (orange) is mostly opaque and colourful
    o = 4 + 12 + 25 * (4 + 64 * 56 * 4) + 12
    stocks_at = o + 118 * (4 + 136 * 188 * 4) + 12
    entries = []
    for i in range(118):
        c, k = b[o], b[o + 1]
        entries.append((c, k, b[o + 4:o + 4 + 136 * 188 * 4]))
        o += 4 + 136 * 188 * 4
    fox2 = next(img for c, k, img in entries if (c, k) == (2, 1))
    assert sum(fox2[3::4]) > 136 * 188 * 255 // 3
    assert len({fox2[i:i + 3] for i in range(0, len(fox2), 4)}) > 32
    # the stock icons: every (character, costume) once, the first 26 the default costumes in order, Fox with his four
    keys = [(b[stocks_at + i * (4 + 24 * 24 * 4)], b[stocks_at + 1 + i * (4 + 24 * 24 * 4)]) for i in range(sizes["stocks"][2])]
    assert len(set(keys)) == len(keys) and keys[:26] == [(c, 0) for c in range(26)]
    assert sorted(k for c, k in keys if c == 2) == [0, 1, 2, 3]


def _figure(path, color, w=300, h=400):
    """A rendered frame stand-in (frame_dump's raster: u32 width, u32 height, RGBA): a transparent image with a coloured
    figure (a head over a body) in the middle."""
    px = bytearray(w * h * 4)
    for y in range(h):
        for x in range(w):
            head = (x - 150) ** 2 + (y - 90) ** 2 <= 30 ** 2
            body = 130 <= x <= 170 and 120 <= y <= 330
            if head or body:
                px[4 * (y * w + x):4 * (y * w + x) + 4] = bytes(color)
    path.write_bytes(struct.pack("<II", w, h) + px)
    return path


def _plate_tile(name, columns, plate=(33, 0, 0, 255), rows=range(43, 51)):
    """A select tile stand-in: a plate with `name` written in white blocks, one per (first, last) column in `columns`."""
    px = bytearray(plate * (64 * 56))
    for (x0, x1), ch in zip(columns, name):
        for y in rows:
            for x in range(x0, x1 + 1):
                if ch == "I" or (x in (x0, x1) or y in (rows[0], rows[-1])):   # a hollow block, or a bar for I
                    px[4 * (y * 64 + x):4 * (y * 64 + x) + 4] = bytes((214, 214, 214, 255))
    return bytes(px)


def test_tile_letters_are_cut_from_melees_name_plates():
    tiles = [(6, 0, _plate_tile("LINK", [(11, 17), (22, 23), (29, 35), (41, 47)])),
             (5, 0, _plate_tile("BOWSER", [(4, 9), (13, 20), (24, 32), (37, 42), (45, 50), (53, 59)])),
             (18, 0, _plate_tile("ZELDA", [(8, 13), (18, 23), (28, 33), (38, 43), (49, 55)]))]
    letters = css.tile_letters(tiles)
    assert set("LINKBOWSER") <= set(letters)                  # Zelda's tile isn't a letter source: her tile is Sheik's base
    assert (letters["I"].w, letters["I"].h) == (2, 8) and (letters["K"].w, letters["K"].h) == (7, 8)
    assert letters["S"].get(37 - 37, 0)[:3] == (214, 214, 214) and letters["S"].get(1, 1)[3] == 0   # the plate isn't copied


def test_tile_letters_skip_a_plate_whose_letters_do_not_separate():
    tiles = [(6, 0, _plate_tile("LINK", [(11, 17), (18, 19), (29, 35), (41, 47)]))]   # L and I touch: three runs, four letters
    assert css.tile_letters(tiles) == {}


def test_rgba_round_trips(tmp_path):
    img = css.Rgba(3, 2, bytes(range(24)))
    img.save(tmp_path / "a.rgba")
    back = css.Rgba.load(tmp_path / "a.rgba")
    assert (back.w, back.h, bytes(back.px)) == (3, 2, bytes(range(24)))
    assert back.get(1, 1) == (16, 17, 18, 19)


def test_rgba_resize_averages_and_bbox_finds_the_drawn_part():
    img = css.Rgba(4, 4, bytes(16 * 4))
    img.fill((1, 1, 3, 3), (200, 100, 0, 255))
    assert img.bbox() == (1, 1, 3, 3)
    small = img.resize(2, 2)
    assert small.get(0, 0)[:3] == (200, 100, 0) and small.get(0, 0)[3] == 64    # a quarter of the area was drawn
    assert small.get(1, 1)[3] == 64


def _letters():
    return css.tile_letters([(6, 0, _plate_tile("LINK", [(11, 17), (22, 23), (29, 35), (41, 47)])),
                             (5, 0, _plate_tile("BOWSER", [(4, 9), (13, 20), (24, 32), (37, 42), (45, 50), (53, 59)])),
                             (17, 0, _plate_tile("YOSHI", [(4, 9), (13, 20), (24, 29), (34, 40), (44, 45)]))])


def test_sheik_art_makes_a_tile_and_a_portrait_per_costume(tmp_path):
    frames = [_figure(tmp_path / f"s{k}.rgba", (40 + 40 * k, 60, 200, 255)) for k in range(3)]
    base = bytes([30, 30, 60, 255]) * (64 * 56)   # a plain tile to borrow the border and plate from
    tile, portraits = css.sheik_art(frames, base, _letters())
    assert len(tile) == 64 * 56 * 4
    assert len(portraits) == 3 and all(len(p) == 136 * 188 * 4 for p in portraits)
    # the portrait: the figure, scaled, over nothing; its colour is the costume's
    px = portraits[2]
    opaque = [i for i in range(0, len(px), 4) if px[i + 3] > 200]
    assert 2000 < len(opaque) < 136 * 188
    assert all(abs(px[i] - 120) < 20 for i in opaque[:50])
    # the tile: the head's colour in its face, nothing (the select's backdrop) around it, the base tile's border kept,
    # and her name in the plate's letters at the bottom, centred
    face = [tile[i:i + 4] for i in range(4 * (64 * 20 + 20), 4 * (64 * 20 + 44), 4)]
    assert any(abs(p[2] - 200) < 20 and p[3] == 255 for p in face)
    assert tile[4 * (64 * 6 + 6) + 3] == 0 and tile[4 * (64 * 6 + 57) + 3] == 0      # beside the head: see-through
    assert tuple(tile[4 * (64 * 1 + 1):4 * (64 * 1 + 1) + 4]) == (30, 30, 60, 255)     # the border is the base's
    lit = [(i // 4 % 64, i // 4 // 64) for i in range(4 * 64 * 42, 4 * 64 * 52, 4) if tile[i] > 200 and tile[i + 1] > 200 and tile[i + 2] > 200]
    assert lit and min(y for _, y in lit) == 43 and max(y for _, y in lit) == 50
    xs = [x for x, _ in lit]
    assert abs((min(xs) + max(xs)) / 2 - 31.5) <= 1                                   # S H E I K, centred on the plate


def test_sheik_art_takes_the_face_from_the_head_frame(tmp_path):
    frames = [_figure(tmp_path / "s0.rgba", (10, 20, 200, 255))]
    head = _figure(tmp_path / "head.rgba", (200, 20, 10, 255))
    base = bytes([30, 30, 60, 255]) * (64 * 56)
    tile, portraits = css.sheik_art(frames, base, {}, head)
    face = [tile[i:i + 4] for i in range(4 * (64 * 20 + 20), 4 * (64 * 20 + 44), 4)]
    assert any(abs(p[0] - 200) < 20 and p[3] == 255 for p in face) and not any(abs(p[2] - 200) < 20 for p in face)
    px = portraits[0]
    assert any(px[i + 3] == 255 and abs(px[i + 2] - 200) < 20 for i in range(0, len(px), 4))   # the portrait stays the costume's


def test_sheik_art_without_letters_leaves_the_plate_blank(tmp_path):
    frames = [_figure(tmp_path / "s0.rgba", (10, 20, 200, 255))]
    base = bytes([30, 30, 60, 255]) * (64 * 56)
    tile, _ = css.sheik_art(frames, base, {})
    assert not any(tile[i] > 200 for i in range(4 * 64 * 42, 4 * 64 * 52, 4))


def test_extract_takes_sheik_frames(tmp_path, monkeypatch):
    sections = {"tiles": ((64, 56), [(18, 0, bytes([1, 2, 3, 255]) * (64 * 56))]), "portraits": ((136, 188), []), "stocks": ((24, 24), [])}
    frames = [_figure(tmp_path / f"s{k}.rgba", (10, 20, 200, 255)) for k in range(2)]
    css.add_sheik(sections, frames)
    assert [c for c, _, _ in sections["tiles"][1]] == [18, 19]
    assert [(c, k) for c, k, _ in sections["portraits"][1]] == [(19, 0), (19, 1)]
