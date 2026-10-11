import math
from pathlib import Path

import pytest

from ssbmhk_tools.hkrooms import DEFAULT_DATA, apply, extract_scene, mat_mul, scene_files, trs_matrix


def test_trs_applies_scale_then_rotation_then_translation():
    q90 = (0.0, 0.0, math.sin(math.pi / 4), math.cos(math.pi / 4))   # 90 degrees about z
    m = trs_matrix((10.0, 5.0, 0.0), q90, (2.0, 3.0, 1.0))
    x, y = apply(m, 1.0, 1.0)
    assert (round(x, 6), round(y, 6)) == (10.0 - 3.0, 5.0 + 2.0)   # (2, 3) rotated by 90 degrees, then moved


def test_parent_chain_composes():
    parent = trs_matrix((100.0, 0.0, 0.0), (0, 0, 0, 1), (2.0, 2.0, 1.0))
    child = trs_matrix((1.0, 1.0, 0.0), (0, 0, 0, 1), (1.0, 1.0, 1.0))
    x, y = apply(mat_mul(parent, child), 0.5, 0.0)
    assert (x, y) == (103.0, 2.0)


HK = Path(DEFAULT_DATA)


@pytest.mark.skipif(not (HK / "globalgamemanagers").exists(), reason="Hollow Knight not installed")
def test_kings_pass_has_terrain_and_gates():
    assert scene_files(HK)["Tutorial_01"].endswith("level6")
    room = extract_scene(HK, "Tutorial_01")
    assert room["scene"] == "Tutorial_01"
    assert len(room["colliders"]) > 50                                # 87 on layer 8 (most boxes are enemies/triggers)
    assert {"box", "poly", "edge"} <= {c["kind"] for c in room["colliders"]}
    assert any(g["name"].startswith("right") for g in room["gates"])
    xs = [p[0] for c in room["colliders"] for p in c["points"]]
    assert max(xs) - min(xs) > 50                                     # world units, not local
