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
    atlas: tuple        # (w, h, rgba): every part's texture in one image, wrap modes baked in
    tris: list          # (i0, i1, i2)
    tri_part: list      # part (DObj, depth first) of each triangle: Melee hides parts by DObj
    part_key: list      # (joint, index within the joint's DObj list) of each part, as mc_read_dobjs names them

    @property
    def n_parts(self):
        return len(self.part_key)


def _wrap(x, n, mode):
    if mode == 1:
        return x % n
    if mode == 2:
        m = x % (2 * n)
        return m if m < n else 2 * n - 1 - m
    return min(max(x, 0), n - 1)


class Atlas:
    """Textures packed into one RGBA image. Each texture is stored as a 3x3 block covering u, v in [-1, 2],
    with its GX wrap mode (clamp, repeat, mirror) baked in, so UVs outside 0..1 sample correctly without
    sampler state; one material means the renderer can sort every triangle back to front."""
    PAD = 2

    def __init__(self):
        self.cells, self.pos, self.size = [], None, None

    def add_texture(self, img, wrap) -> int:
        w, h = img.w, img.h
        block = bytearray(9 * w * h * 4)
        for by in range(3 * h):
            ty = _wrap(by - h, h, wrap[1])
            for bx in range(3 * w):
                tx = _wrap(bx - w, w, wrap[0])
                block[4 * (by * 3 * w + bx):4 * (by * 3 * w + bx) + 4] = img.rgba[4 * (ty * w + tx):4 * (ty * w + tx) + 4]
        return self._add(3 * w, 3 * h, bytes(block), True)

    def add_colour(self, rgba) -> int:
        return self._add(4, 4, bytes(rgba) * 16, False)

    def _add(self, w, h, data, textured):
        if self.pos is not None:
            raise ValueError("atlas already packed")
        self.cells.append((w, h, data, textured))
        return len(self.cells) - 1

    def _pack(self):
        if self.pos is not None:
            return
        width = max(2048, max((c[0] for c in self.cells), default=4) + 2 * self.PAD)
        order = sorted(range(len(self.cells)), key=lambda i: -self.cells[i][1])
        self.pos, x, y, shelf = [None] * len(self.cells), self.PAD, self.PAD, 0
        for i in order:
            w, h = self.cells[i][0], self.cells[i][1]
            if x + w + self.PAD > width:
                x, y, shelf = self.PAD, y + shelf + self.PAD, 0
            self.pos[i] = (x, y)
            x, shelf = x + w + self.PAD, max(shelf, h)
        self.size = (width, y + shelf + self.PAD)

    def uv(self, cell, uv):
        self._pack()
        w, h, _, textured = self.cells[cell]
        x, y = self.pos[cell]
        aw, ah = self.size
        if not textured:
            return ((x + w / 2) / aw, (y + h / 2) / ah)
        u, v = min(max(uv[0], -1.0), 2.0), min(max(uv[1], -1.0), 2.0)
        return ((x + (u + 1.0) * w / 3) / aw, (y + (v + 1.0) * h / 3) / ah)

    def build(self):
        self._pack()
        aw, ah = self.size
        out = bytearray(aw * ah * 4)
        for (w, h, data, _), (x, y) in zip(self.cells, self.pos):
            for row in range(h):
                out[4 * ((y + row) * aw + x):4 * ((y + row) * aw + x + w)] = data[4 * row * w:4 * (row + 1) * w]
        return (aw, ah, bytes(out))


def build_model(joints) -> Model:
    inv = [j.inv_bind or hsd.mat_inverse(j.bind_world) for j in joints]
    to_model = [hsd.mat_inverse(ib) for ib in inv]
    atlas, dobjs, cell_of, keys = Atlas(), [], {}, []
    for j in joints:
        for local, d in enumerate(j.dobjs):
            keys.append((j.index, local))
            if d.mobj.texture is not None:
                key = (d.mobj.texture.w, d.mobj.texture.h, d.mobj.texture.rgba, d.mobj.wrap)   # identical images stored once
                if key not in cell_of:
                    cell_of[key] = atlas.add_texture(d.mobj.texture, d.mobj.wrap)
                cell = cell_of[key]
            else:
                key = ("colour", d.mobj.colour)
                if key not in cell_of:
                    cell_of[key] = atlas.add_colour(d.mobj.colour)
                cell = cell_of[key]
            dobjs.append((j, d, cell))
    verts, tris, tri_part = [], [], []
    for part, (j, d, cell) in enumerate(dobjs):
        for p in d.pobjs:
            for tri in p.triangles:
                first = len(verts)
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
                    uv = atlas.uv(cell, (v.uv[0] * d.mobj.uv_scale[0], v.uv[1] * d.mobj.uv_scale[1]))
                    verts.append(Vert(pos, v.nrm, uv, bones + (0,) * (4 - len(bones)), weights + (0.0,) * (4 - len(weights))))
                tris.append((first, first + 1, first + 2))
                tri_part.append(part)
    return Model(inv, verts, atlas.build(), tris, tri_part, keys)


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
    b = bytearray(MAGIC + struct.pack("<I", 2))
    b += struct.pack("<I", len(m.inv_bind))
    for ib in m.inv_bind:
        b += struct.pack("<12f", *[x for row in ib for x in row])
    b += struct.pack("<I", len(m.verts))
    for v in m.verts:
        b += struct.pack("<8f4B4f", *v.pos, *v.nrm, *v.uv, *v.bone, *v.weight)
    aw, ah, rgba = m.atlas
    b += struct.pack("<II", aw, ah) + rgba
    b += struct.pack("<I", m.n_parts)
    for joint, local in m.part_key:
        b += struct.pack("<BB", joint, local)
    b += struct.pack("<I", len(m.tris))
    for (i0, i1, i2), part in zip(m.tris, m.tri_part):
        b += struct.pack("<3IHxx", i0, i1, i2, part)
    Path(path).write_bytes(bytes(b))


def read_model(path: Path) -> Model:
    d = Path(path).read_bytes()
    if d[:8] != MAGIC or struct.unpack_from("<I", d, 8)[0] != 2:
        raise ValueError(f"{path} is not a FOXMODEL v2 file")
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
    aw, ah = struct.unpack_from("<II", d, o); o += 8
    atlas = (aw, ah, d[o:o + 4 * aw * ah]); o += 4 * aw * ah
    (n_parts,) = struct.unpack_from("<I", d, o); o += 4
    keys = [tuple(d[o + 2 * i:o + 2 * i + 2]) for i in range(n_parts)]; o += 2 * n_parts
    (nt,) = struct.unpack_from("<I", d, o); o += 4
    tris, parts = [], []
    for _ in range(nt):
        i0, i1, i2, part = struct.unpack_from("<3IHxx", d, o); o += 16
        tris.append((i0, i1, i2)); parts.append(part)
    return Model(inv, verts, atlas, tris, parts, keys)


def render_png(model, positions, path, axes=(0, 1, 2), size=500):
    import numpy as np
    from PIL import Image
    P = np.array(positions)
    T = P[np.array(model.tris)]
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
    print(f"fox.model: {len(model.inv_bind)} bones, {len(model.verts)} vertices, {len(model.tris)} triangles, {model.n_parts} parts, atlas {model.atlas[0]}x{model.atlas[1]}")
    return 0
