"""Fox's model for the mod: HSD joints/meshes to fox.model (model-space vertices, 4 bone weights, inverse
binds, RGBA textures), linear-blend posing, and a preview render. Generated from the player's ISO only."""
import argparse
import struct
from dataclasses import dataclass
from pathlib import Path

from . import gciso, hsd

MAGIC = b"FOXMODEL"


@dataclass
class Vert:
    pos: tuple
    nrm: tuple
    uv: tuple
    bone: tuple
    weight: tuple


@dataclass
class Model:
    inv_bind: list
    verts: list
    textures: list      # (w, h, rgba)
    parts: list         # (texture, first_index, n_indices, colour)
    indices: list


def build_model(joints) -> Model:
    inv = [j.inv_bind or hsd.mat_inverse(j.bind_world) for j in joints]
    to_model = [hsd.mat_inverse(ib) for ib in inv]
    verts, indices, parts, textures, tex_ids = [], [], [], [], {}
    for j in joints:
        for d in j.dobjs:
            tex = -1
            if d.mobj.texture is not None:
                key = (d.mobj.texture.w, d.mobj.texture.h, d.mobj.texture.rgba)   # identical images stored once
                if key not in tex_ids:
                    tex_ids[key] = len(textures)
                    textures.append((d.mobj.texture.w, d.mobj.texture.h, d.mobj.texture.rgba))
                tex = tex_ids[key]
            first = len(indices)
            for p in d.pobjs:
                for tri in p.triangles:
                    for v in tri:
                        if p.kind == "env":
                            env = p.envelopes[v.mtx // 3]
                            if len(env) == 1 and env[0][1] >= 1.0 - 1e-6:
                                bones, weights, pos = (env[0][0],), (1.0,), hsd.mat_apply(to_model[env[0][0]], v.pos)
                            else:
                                bones, weights, pos = tuple(b for b, _ in env), tuple(w for _, w in env), v.pos
                        else:
                            bones, weights, pos = (p.owner,), (1.0,), hsd.mat_apply(to_model[p.owner], v.pos)
                        if len(bones) > 4:
                            raise hsd.UnsupportedModel("more than 4 bone weights")
                        uv = (v.uv[0] * d.mobj.uv_scale[0], v.uv[1] * d.mobj.uv_scale[1])
                        indices.append(len(verts))
                        verts.append(Vert(pos, v.nrm, uv, bones + (0,) * (4 - len(bones)), weights + (0.0,) * (4 - len(weights))))
            if len(indices) > first:
                parts.append((tex, first, len(indices) - first, d.mobj.colour))
    return Model(inv, verts, textures, parts, indices)


def pose(model, bones) -> list:
    skin = [hsd.mat_mul(bones[i], model.inv_bind[i]) for i in range(len(model.inv_bind))]
    out = []
    for v in model.verts:
        acc = [0.0, 0.0, 0.0]
        for b, w in zip(v.bone, v.weight):
            if w:
                q = hsd.mat_apply(skin[b], v.pos)
                acc = [acc[k] + w * q[k] for k in range(3)]
        out.append(tuple(acc))
    return out


def write_model(m: Model, path: Path):
    b = bytearray(MAGIC + struct.pack("<I", 1))
    b += struct.pack("<I", len(m.inv_bind))
    for ib in m.inv_bind:
        b += struct.pack("<12f", *[x for row in ib for x in row])
    b += struct.pack("<I", len(m.verts))
    for v in m.verts:
        b += struct.pack("<8f4B4f", *v.pos, *v.nrm, *v.uv, *v.bone, *v.weight)
    b += struct.pack("<I", len(m.textures))
    for w, h, rgba in m.textures:
        b += struct.pack("<II", w, h) + rgba
    b += struct.pack("<I", len(m.parts))
    for tex, first, n, colour in m.parts:
        b += struct.pack("<iII4B", tex, first, n, *colour)
    b += struct.pack("<I", len(m.indices)) + struct.pack(f"<{len(m.indices)}I", *m.indices)
    Path(path).write_bytes(bytes(b))


def read_model(path: Path) -> Model:
    d = Path(path).read_bytes()
    assert d[:8] == MAGIC and struct.unpack_from("<I", d, 8)[0] == 1
    o = 12
    (nb,) = struct.unpack_from("<I", d, o); o += 4
    inv = []
    for _ in range(nb):
        f = struct.unpack_from("<12f", d, o); o += 48
        inv.append([list(f[0:4]), list(f[4:8]), list(f[8:12])])
    (nv,) = struct.unpack_from("<I", d, o); o += 4
    verts = []
    for _ in range(nv):
        f = struct.unpack_from("<8f4B4f", d, o); o += 52
        verts.append(Vert(f[0:3], f[3:6], f[6:8], f[8:12], f[12:16]))
    (nt,) = struct.unpack_from("<I", d, o); o += 4
    texs = []
    for _ in range(nt):
        w, h = struct.unpack_from("<II", d, o); o += 8
        texs.append((w, h, d[o:o + 4 * w * h])); o += 4 * w * h
    (npart,) = struct.unpack_from("<I", d, o); o += 4
    parts = []
    for _ in range(npart):
        tex, first, n, *col = struct.unpack_from("<iII4B", d, o); o += 16
        parts.append((tex, first, n, tuple(col)))
    (ni,) = struct.unpack_from("<I", d, o); o += 4
    return Model(inv, verts, texs, parts, list(struct.unpack_from(f"<{ni}I", d, o)))


def render_png(model, positions, path, axes=(0, 1, 2), size=500):
    import numpy as np
    from PIL import Image
    P = np.array(positions)
    T = P[np.array(model.indices).reshape(-1, 3)]
    X, Y, Z = T[:, :, axes[0]], T[:, :, axes[1]], T[:, :, axes[2]]
    x0, y0 = X.min(), Y.min()
    sc = (size - 40) / max(X.max() - x0, Y.max() - y0, 1e-6)
    px, py = (X - x0) * sc + 20, size - ((Y - y0) * sc + 20)
    zb = np.full((size, size), -1e9)
    img = np.zeros((size, size, 3), np.uint8)
    img[:] = (30, 30, 40)
    nrm = np.cross(T[:, 1] - T[:, 0], T[:, 2] - T[:, 0])
    light = np.abs(nrm @ np.array([0.3, 0.5, 0.8])) / (np.linalg.norm(nrm, axis=1) + 1e-12)
    for t in range(len(T)):
        xa, ya, za = px[t], py[t], Z[t]
        x_lo, x_hi = int(max(xa.min(), 0)), int(min(xa.max() + 1, size))
        y_lo, y_hi = int(max(ya.min(), 0)), int(min(ya.max() + 1, size))
        d = (ya[1] - ya[2]) * (xa[0] - xa[2]) + (xa[2] - xa[1]) * (ya[0] - ya[2])
        if x_lo >= x_hi or y_lo >= y_hi or abs(d) < 1e-12:
            continue
        gx, gy = np.meshgrid(np.arange(x_lo, x_hi) + 0.5, np.arange(y_lo, y_hi) + 0.5)
        l0 = ((ya[1] - ya[2]) * (gx - xa[2]) + (xa[2] - xa[1]) * (gy - ya[2])) / d
        l1 = ((ya[2] - ya[0]) * (gx - xa[2]) + (xa[0] - xa[2]) * (gy - ya[2])) / d
        l2 = 1 - l0 - l1
        z = l0 * za[0] + l1 * za[1] + l2 * za[2]
        sub = zb[y_lo:y_hi, x_lo:x_hi]
        upd = (l0 >= 0) & (l1 >= 0) & (l2 >= 0) & (z > sub)
        sub[upd] = z[upd]
        s = int(60 + 190 * light[t])
        img[y_lo:y_hi, x_lo:x_hi][upd] = (s, int(s * 0.7), int(s * 0.45))
    Image.fromarray(img).save(path)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Extract Fox's model from your Melee ISO into fox.model (not for distribution).")
    ap.add_argument("iso", type=Path)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--bones", type=Path, help="73 lines of 12 floats (posed preview)")
    a = ap.parse_args(argv)
    dat = hsd.Dat(gciso.read_file(a.iso, "PlFxNr.dat"))
    model = build_model(hsd.read_joints(dat, dat.roots["PlyFox5K_Share_joint"]))
    a.out.mkdir(parents=True, exist_ok=True)
    write_model(model, a.out / "fox.model")
    bones = ([[list(map(float, l.split()))[4 * r:4 * r + 4] for r in range(3)] for l in a.bones.read_text().splitlines()]
             if a.bones else [hsd.mat_inverse(ib) for ib in model.inv_bind])
    render_png(model, pose(model, bones), a.out / "fox_preview.png")
    print(f"fox.model: {len(model.inv_bind)} bones, {len(model.verts)} vertices, {len(model.textures)} textures, {len(model.parts)} parts")
    return 0
