import pytest

from ssbmhk_tools import stocks


@pytest.fixture
def iso(dev_config):
    if not dev_config.iso.exists():
        pytest.skip("Melee ISO (dev.toml) not available")
    return dev_config.iso


def test_icon_order_maps_melee_ids():
    # IfAll's stock icons: characters 0-18 in order, then Falco..Ganondorf one place early, Sheik last
    assert [stocks.icon_index(e) for e in (0, 9, 18, 19, 20, 25)] == [0, 9, 18, 25, 19, 24]


def test_extract_from_iso(iso, tmp_path):
    assert stocks.main([str(iso), "--out", str(tmp_path)]) == 0
    b = (tmp_path / "stocks.bin").read_bytes()
    assert b[:4] == b"STCK"
    w, h, n = int.from_bytes(b[4:8], "little"), int.from_bytes(b[8:12], "little"), int.from_bytes(b[12:16], "little")
    assert (w, h, n) == (24, 24, 26) and len(b) == 16 + 24 * 24 * 4 * 26
    marth = b[16 + 9 * 24 * 24 * 4:16 + 10 * 24 * 24 * 4]
    assert sum(marth[3::4]) > 24 * 24 * 255 // 3          # mostly opaque: an icon, not empty
    assert len({marth[i:i + 3] for i in range(0, len(marth), 4)}) > 8   # several colours
