import math
from pathlib import Path

import pytest

from types import SimpleNamespace

from ssbmhk_tools.hkrooms import DEFAULT_DATA, active_in_hierarchy, apply, extract_scene, mat_mul, scene_files, trs_matrix, world_matrix


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


def _t(go, pos=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0, 1.0), scale=(1.0, 1.0, 1.0), father=0):
    """A minimal UnityPy-like Transform: upper-case math types, PPtr-like fields."""
    vec = lambda v: SimpleNamespace(X=v[0], Y=v[1], Z=v[2])   # noqa: E731
    return SimpleNamespace(m_GameObject=SimpleNamespace(path_id=go), m_LocalPosition=vec(pos),
                           m_LocalRotation=SimpleNamespace(X=rot[0], Y=rot[1], Z=rot[2], W=rot[3]),
                           m_LocalScale=vec(scale), m_Father=SimpleNamespace(path_id=father))


def test_world_matrix_follows_rotated_negatively_scaled_parents():
    q90 = (0.0, 0.0, math.sin(math.pi / 4), math.cos(math.pi / 4))
    transforms = {1: _t(10, pos=(50.0, 0.0, 0.0), rot=q90, scale=(-2.0, 1.0, 1.0)),   # rotated, mirrored parent
                  2: _t(20, pos=(1.0, 0.0, 0.0), father=1)}
    x, y = apply(world_matrix(2, transforms, {}), 1.0, 0.0)
    # child local (1,0) + child pos (1,0) = (2,0) in parent space; scale (-2,1) -> (-4,0); rotate 90 -> (0,-4); + (50,0)
    assert (round(x, 6), round(y, 6)) == (50.0, -4.0)


def test_inactive_grandparent_hides_the_collider():
    gos = {10: SimpleNamespace(m_IsActive=False), 20: SimpleNamespace(m_IsActive=True), 30: SimpleNamespace(m_IsActive=True)}
    transforms = {1: _t(10), 2: _t(20, father=1), 3: _t(30, father=2)}
    assert not active_in_hierarchy(3, transforms, gos)
    gos[10].m_IsActive = True
    assert active_in_hierarchy(3, transforms, gos)
