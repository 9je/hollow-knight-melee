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
    assert len(model.inv_bind) == 73 and len(model.tris) == 6658
    assert model.n_parts == 77 and all(0 <= p < 77 for p in model.tri_part)
    # (joint, index within the joint) names a part at runtime too (the game appends extra parts to joint 0)
    assert sorted({j for j, _ in model.part_key}) == [0, 41, 45, 49] and model.part_key[71] == (0, 71) and model.part_key[72] == (41, 0)
    aw, ah, rgba = model.atlas                                        # one texture: no per-part materials to sort
    assert all(0.0 <= v.uv[0] <= 1.0 and 0.0 <= v.uv[1] <= 1.0 for v in model.verts)
    pos = foxmodel.pose(model, _identity_pose(model))
    xs, ys = [p[0] for p in pos], [p[1] for p in pos]
    assert -10.5 < min(xs) < -9.5 and 9.5 < max(xs) < 10.5          # arms out (bind pose), from the spike
    assert -0.5 < min(ys) < 0.5 and 16.5 < max(ys) < 17.5
    path = tmp_path / "fox.model"
    foxmodel.write_model(model, path)
    back = foxmodel.read_model(path)
    assert len(back.verts) == len(model.verts) and back.tris == model.tris and back.tri_part == model.tri_part
    assert back.atlas[:2] == model.atlas[:2] and back.part_key == model.part_key
    assert all(abs(a - b) < 1e-6 for a, b in zip(back.verts[100].pos, model.verts[100].pos))
    foxmodel.render_png(model, pos, tmp_path / "bind.png")
    assert (tmp_path / "bind.png").stat().st_size > 1000


def test_pose_blends_bones_linearly():
    v = foxmodel.Vert((1.0, 0.0, 0.0), (0, 0, 1), (0, 0), (0, 1, 0, 0), (0.5, 0.5, 0.0, 0.0))
    ident = [[1.0, 0, 0, 0], [0, 1.0, 0, 0], [0, 0, 1.0, 0]]
    m = foxmodel.Model(inv_bind=[ident, ident], verts=[v], atlas=(1, 1, bytes(4)), tris=[], tri_part=[], part_key=[])
    up = [[1.0, 0, 0, 0], [0, 1.0, 0, 10.0], [0, 0, 1.0, 0]]
    assert foxmodel.pose(m, [ident, up])[0] == (1.0, 5.0, 0.0)


def test_atlas_cells_bake_the_wrap_mode():
    # a 2x1 texture: left red, right blue. Clamp: u = -0.5 samples the left edge (red); repeat: u = -0.25 samples
    # the right texel (blue); mirror: u = -0.25 samples the left texel (red)
    img = hsd.RgbaImage(2, 1, bytes([255, 0, 0, 255, 0, 0, 255, 255]))
    for wrap, u, want in (((0, 0), -0.5, (255, 0, 0, 255)), ((1, 1), -0.25, (0, 0, 255, 255)), ((2, 2), -0.25, (255, 0, 0, 255))):
        atlas = foxmodel.Atlas()
        cell = atlas.add_texture(img, wrap)
        au, av = atlas.uv(cell, (u, 0.5))
        aw, ah, rgba = atlas.build()
        x, y = min(int(au * aw), aw - 1), min(int(av * ah), ah - 1)
        assert tuple(rgba[4 * (y * aw + x):4 * (y * aw + x) + 4]) == want, (wrap, u)
