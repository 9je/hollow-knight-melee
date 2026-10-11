import pytest

from ssbmhk_tools import foxmodel, gciso, hsd


@pytest.fixture
def iso(dev_config):
    if not dev_config.iso.exists():
        pytest.skip("Melee ISO (dev.toml) not available")
    return dev_config.iso


def _identity_pose(model):
    return [hsd.mat_inverse(ib) for ib in model.inv_bind]   # W = IB^-1: the bind pose


def test_bind_pose_reproduces_the_model_and_round_trips(iso, tmp_path):
    dat = hsd.Dat(gciso.read_file(iso, "PlFxNr.dat"))
    model = foxmodel.build_model(hsd.read_joints(dat, dat.roots["PlyFox5K_Share_joint"]))
    assert len(model.inv_bind) == 73 and len(model.indices) == 3 * 6658
    assert len(model.textures) < 77                                   # identical images stored once
    pos = foxmodel.pose(model, _identity_pose(model))
    xs, ys = [p[0] for p in pos], [p[1] for p in pos]
    assert -10.5 < min(xs) < -9.5 and 9.5 < max(xs) < 10.5          # arms out (bind pose), from the spike
    assert -0.5 < min(ys) < 0.5 and 16.5 < max(ys) < 17.5
    path = tmp_path / "fox.model"
    foxmodel.write_model(model, path)
    back = foxmodel.read_model(path)
    assert len(back.verts) == len(model.verts) and back.indices == model.indices
    assert all(abs(a - b) < 1e-6 for a, b in zip(back.verts[100].pos, model.verts[100].pos))
    foxmodel.render_png(model, pos, tmp_path / "bind.png")
    assert (tmp_path / "bind.png").stat().st_size > 1000


def test_pose_blends_bones_linearly():
    v = foxmodel.Vert((1.0, 0.0, 0.0), (0, 0, 1), (0, 0), (0, 1, 0, 0), (0.5, 0.5, 0.0, 0.0))
    ident = [[1.0, 0, 0, 0], [0, 1.0, 0, 0], [0, 0, 1.0, 0]]
    m = foxmodel.Model(inv_bind=[ident, ident], verts=[v], textures=[], parts=[], indices=[])
    up = [[1.0, 0, 0, 0], [0, 1.0, 0, 10.0], [0, 0, 1.0, 0]]
    assert foxmodel.pose(m, [ident, up])[0] == (1.0, 5.0, 0.0)
