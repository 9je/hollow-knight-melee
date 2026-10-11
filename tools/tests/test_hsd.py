import pytest

from ssbmhk_tools import gciso, hsd


@pytest.fixture
def iso(dev_config):
    if not dev_config.iso.exists():
        pytest.skip("Melee ISO (dev.toml) not available")
    return dev_config.iso


def test_fox_costume_skeleton_and_meshes(iso):
    dat = hsd.Dat(gciso.read_file(iso, "PlFxNr.dat"))
    joints = hsd.read_joints(dat, dat.roots["PlyFox5K_Share_joint"])
    assert len(joints) == 73
    pobjs = [p for j in joints for d in j.dobjs for p in d.pobjs]
    kinds = {k: sum(1 for p in pobjs if p.kind == k) for k in ("skin", "shape", "env")}
    assert kinds == {"skin": 5, "shape": 0, "env": 86}
    assert sum(len(p.triangles) for p in pobjs) == 6658
    textured = [d.mobj for j in joints for d in j.dobjs if d.mobj.texture]
    assert textured and all(len(m.texture.rgba) == m.texture.w * m.texture.h * 4 for m in textured)


def test_texture_repeat_and_wrap_come_from_the_tobj(iso):
    # TObj: wrap_s +0x34, wrap_t +0x38 (GX: 0 clamp, 1 repeat, 2 mirror), repeat_s/t +0x3C/+0x3D (tobj.h)
    dat = hsd.Dat(gciso.read_file(iso, "PlFxNr.dat"))
    mats = [d.mobj for j in hsd.read_joints(dat, dat.roots["PlyFox5K_Share_joint"]) for d in j.dobjs if d.mobj.texture]
    assert all(m.uv_scale[1] == 1.0 for m in mats)               # never tiled vertically (blend_flags misread gave 5)
    assert sum(m.uv_scale == (1.0, 1.0) for m in mats) >= len(mats) - 2
    assert {m.wrap for m in mats} <= {(0, 0), (1, 1), (2, 2), (0, 1), (1, 0), (0, 2), (2, 0), (1, 2), (2, 1)}
    assert any(m.wrap[0] == 0 for m in mats)                    # most are GX_CLAMP
