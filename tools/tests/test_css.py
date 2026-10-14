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
    """A rendered frame stand-in: a transparent PNG with a coloured figure (a head over a body) in the middle."""
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((120, 60, 180, 120), fill=color)          # the head
    d.rectangle((130, 120, 170, 330), fill=color)       # the body
    img.save(path)
    return path


def test_sheik_art_makes_a_tile_and_a_portrait_per_costume(tmp_path):
    frames = [_figure(tmp_path / f"s{k}.png", (40 + 40 * k, 60, 200, 255)) for k in range(3)]
    base = bytes([30, 30, 60, 255]) * (64 * 56)   # a plain tile to borrow the border and plate from
    tile, portraits = css.sheik_art(frames, base)
    assert len(tile) == 64 * 56 * 4
    assert len(portraits) == 3 and all(len(p) == 136 * 188 * 4 for p in portraits)
    # the portrait: the figure, scaled, over nothing; its colour is the costume's
    px = portraits[2]
    opaque = [i for i in range(0, len(px), 4) if px[i + 3] > 200]
    assert 2000 < len(opaque) < 136 * 188
    assert all(abs(px[i] - 120) < 20 for i in opaque[:50])
    # the tile: the head's colour in its face, and a name plate with light (text) pixels at the bottom
    face = [tile[i:i + 4] for i in range(4 * (64 * 20 + 20), 4 * (64 * 20 + 44), 4)]
    assert any(abs(p[2] - 200) < 20 and p[3] == 255 for p in face)
    plate = [tile[i:i + 4] for i in range(4 * (64 * 46), 4 * (64 * 52), 4)]
    assert any(p[0] > 200 and p[1] > 200 and p[2] > 200 for p in plate)


def test_extract_takes_sheik_frames(tmp_path, monkeypatch):
    sections = {"tiles": ((64, 56), [(18, 0, bytes([1, 2, 3, 255]) * (64 * 56))]), "portraits": ((136, 188), []), "stocks": ((24, 24), [])}
    frames = [_figure(tmp_path / f"s{k}.png", (10, 20, 200, 255)) for k in range(2)]
    css.add_sheik(sections, frames)
    assert [c for c, _, _ in sections["tiles"][1]] == [18, 19]
    assert [(c, k) for c, k, _ in sections["portraits"][1]] == [(19, 0), (19, 1)]
