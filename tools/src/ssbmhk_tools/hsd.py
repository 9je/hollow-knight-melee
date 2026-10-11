"""HSD (sysdolphin) archives: joints, meshes, envelopes and materials of a model (read-only)."""
import math
import struct
from dataclasses import dataclass, field

from . import gxtex


class UnsupportedModel(ValueError):
    pass


class Dat:
    def __init__(self, data: bytes):
        fsize, dsize, nreloc, nroot, nref = struct.unpack_from(">IIIII", data, 0)
        self.d = data[0x20:]
        rt = dsize + nreloc * 4
        st = rt + (nroot + nref) * 8
        self.roots = {}
        for k in range(nroot):
            off, so = struct.unpack_from(">II", self.d, rt + 8 * k)
            self.roots[self.d[st + so:self.d.index(b"\0", st + so)].decode()] = off

    def u32(self, o): return struct.unpack_from(">I", self.d, o)[0]
    def u16(self, o): return struct.unpack_from(">H", self.d, o)[0]
    def f32(self, o): return struct.unpack_from(">f", self.d, o)[0]
    def u8(self, o): return self.d[o]


def srt_matrix(s, r, t):
    """HSD_MtxSRT without classical scaling: T * Rz * Ry * Rx * S, 3x4 row-major."""
    sx, cx = math.sin(r[0]), math.cos(r[0])
    sy, cy = math.sin(r[1]), math.cos(r[1])
    sz, cz = math.sin(r[2]), math.cos(r[2])
    R = [[cz * cy, cz * sx * sy - cx * sz, cz * cx * sy + sx * sz],
         [sz * cy, sz * sx * sy + cx * cz, sz * cx * sy - sx * cz],
         [-sy, sx * cy, cx * cy]]
    return [[R[i][0] * s[0], R[i][1] * s[1], R[i][2] * s[2], t[i]] for i in range(3)]


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) + (a[i][3] if j == 3 else 0.0) for j in range(4)] for i in range(3)]


def mat_apply(m, v):
    return tuple(m[i][0] * v[0] + m[i][1] * v[1] + m[i][2] * v[2] + m[i][3] for i in range(3))


def mat_inverse(m):
    a = [[m[i][j] for j in range(3)] for i in range(3)]
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    inv = [[(a[(j + 1) % 3][(i + 1) % 3] * a[(j + 2) % 3][(i + 2) % 3] - a[(j + 1) % 3][(i + 2) % 3] * a[(j + 2) % 3][(i + 1) % 3]) / det
            for j in range(3)] for i in range(3)]
    t = [-sum(inv[i][k] * m[k][3] for k in range(3)) for i in range(3)]
    return [inv[i] + [t[i]] for i in range(3)]


@dataclass
class RgbaImage:
    w: int
    h: int
    rgba: bytes


@dataclass
class Material:
    texture: RgbaImage | None
    colour: tuple
    uv_scale: tuple
    wrap: tuple = (0, 0)       # GX wrap mode per axis: 0 clamp, 1 repeat, 2 mirror


@dataclass
class Vertex:
    pos: tuple
    nrm: tuple
    uv: tuple
    mtx: int


@dataclass
class PObj:
    owner: int
    kind: str
    envelopes: list
    triangles: list


@dataclass
class DObj:
    mobj: Material
    pobjs: list


@dataclass
class Joint:
    index: int
    parent: int
    flags: int
    srt: tuple
    bind_world: list
    inv_bind: list | None
    dobjs: list = field(default_factory=list)


_CT = {0: ("B", 1), 1: ("b", 1), 2: ("H", 2), 3: ("h", 2), 4: ("f", 4)}


def _attr_value(dat, at, idx):
    fmt, sz = _CT[at["ctype"]]
    n = {9: 3 if at["cnt"] == 1 else 2, 10: 3, 13: 2 if at["cnt"] == 1 else 1}[at["attr"]]
    o = at["arr"] + idx * at["stride"]
    v = [struct.unpack_from(">" + fmt, dat.d, o + sz * k)[0] for k in range(n)]
    if at["ctype"] != 4:
        v = [x / (1 << at["frac"]) for x in v]
    return tuple(v) + (0.0,) * ((3 if at["attr"] != 13 else 2) - n)


def _material(dat, mobj):
    colour, tex, scale, wrap = (255, 255, 255, 255), None, (1.0, 1.0), (0, 0)
    if not mobj:
        return Material(None, colour, scale, wrap)
    mat = dat.u32(mobj + 0xC)
    if mat:
        colour = tuple(dat.d[mat + 4:mat + 8])          # diffuse RGBA
    t = dat.u32(mobj + 8)
    if t:
        # TObjDesc (tobj.h): wrap_s +0x34, wrap_t +0x38, repeat_s +0x3C, repeat_t +0x3D
        wrap = (min(dat.u32(t + 0x34), 2), min(dat.u32(t + 0x38), 2))
        scale = (float(dat.u8(t + 0x3C) or 1), float(dat.u8(t + 0x3D) or 1))
        img = dat.u32(t + 0x4C)
        if img:
            data, w, h, fmt = dat.u32(img), dat.u16(img + 4), dat.u16(img + 6), dat.u32(img + 8)
            tl = dat.u32(t + 0x50)
            tlut = tlut_fmt = None
            if tl:
                n = dat.u16(tl + 0xC)
                tlut, tlut_fmt = dat.d[dat.u32(tl):dat.u32(tl) + 2 * n], dat.u32(tl + 4)
            try:
                tex = RgbaImage(w, h, gxtex.decode(fmt, w, h, dat.d[data:], tlut, tlut_fmt))
            except gxtex.UnsupportedTexture:
                tex = None
    return Material(tex, colour, scale, wrap)


def _pobj(dat, p, owner, joint_index):
    vdl, flags, ndisp, disp, u = dat.u32(p + 8), dat.u16(p + 0xC), dat.u16(p + 0xE), dat.u32(p + 0x10), dat.u32(p + 0x14)
    kind = {0: "skin", 0x1000: "shape", 0x2000: "env"}[flags & 0x3000]
    if kind == "shape":
        raise UnsupportedModel("shape-animation meshes are not supported")
    if kind == "skin" and u:
        raise UnsupportedModel("shared-vertex skin meshes are not supported")
    attrs, a = [], vdl
    while dat.u32(a) != 0xFF:
        attrs.append(dict(attr=dat.u32(a), type=dat.u32(a + 4), cnt=dat.u32(a + 8), ctype=dat.u32(a + 12),
                          frac=dat.u8(a + 16), stride=dat.u16(a + 18), arr=dat.u32(a + 20)))
        a += 0x18
    envs = []
    if kind == "env":
        e = u
        while dat.u32(e):
            lst, ed = [], dat.u32(e)
            while dat.u32(ed):
                lst.append((joint_index[dat.u32(ed)], dat.f32(ed + 4)))
                ed += 8
            envs.append(lst)
            e += 4
    tris, o, end = [], disp, disp + ndisp * 32
    while o < end:
        op = dat.u8(o)
        if op == 0:
            o += 1
            continue
        cnt = dat.u16(o + 1)
        o += 3
        verts = []
        for _ in range(cnt):
            pos = nrm = (0.0, 0.0, 0.0)
            uv, mtx = (0.0, 0.0), 0
            for at in attrs:
                if at["type"] == 1:          # direct: matrix indices only
                    if at["attr"] > 8:
                        raise UnsupportedModel(f"direct vertex attribute {at['attr']}")
                    if at["attr"] == 0:
                        mtx = dat.u8(o)
                    o += 1
                    continue
                idx = dat.u8(o) if at["type"] == 2 else dat.u16(o)
                o += 1 if at["type"] == 2 else 2
                if at["attr"] == 9:
                    pos = _attr_value(dat, at, idx)
                elif at["attr"] == 10:
                    nrm = _attr_value(dat, at, idx)
                elif at["attr"] == 13:
                    uv = _attr_value(dat, at, idx)
            verts.append(Vertex(pos, nrm, uv, mtx))
        prim = op & 0xF8
        if prim == 0x90:
            tris += [tuple(verts[i:i + 3]) for i in range(0, cnt - 2, 3)]
        elif prim == 0x98:
            tris += [(verts[i], verts[i + 1], verts[i + 2]) if i % 2 == 0 else (verts[i + 1], verts[i], verts[i + 2]) for i in range(cnt - 2)]
        elif prim == 0x80:
            for i in range(0, cnt - 3, 4):
                tris += [(verts[i], verts[i + 1], verts[i + 2]), (verts[i], verts[i + 2], verts[i + 3])]
        elif prim == 0xA0:
            tris += [(verts[0], verts[i], verts[i + 1]) for i in range(1, cnt - 1)]
        else:
            raise UnsupportedModel(f"display list primitive {op:02X}")
    return PObj(owner, kind, envs, tris)


def read_joints(dat, root):
    joints, index_of, pending = [], {}, []

    def walk(j, parent, parent_world):
        while j:
            flags = dat.u32(j + 4)
            s = tuple(dat.f32(j + 0x20 + 4 * k) for k in range(3))
            r = tuple(dat.f32(j + 0x14 + 4 * k) for k in range(3))
            t = tuple(dat.f32(j + 0x2C + 4 * k) for k in range(3))
            world = mat_mul(parent_world, srt_matrix(s, r, t))
            ib = dat.u32(j + 0x38)
            inv = [[dat.f32(ib + 4 * (4 * r_ + c)) for c in range(4)] for r_ in range(3)] if ib else None
            jt = Joint(len(joints), parent, flags, (s, r, t), world, inv)
            index_of[j] = jt.index
            joints.append(jt)
            if not flags & 0x4020:          # not a particle (0x20) or spline (0x4000) joint: u is a DObj list
                pending.append((jt, dat.u32(j + 0x10)))
            walk(dat.u32(j + 8), jt.index, world)
            j = dat.u32(j + 0xC)

    walk(root, -1, [[1.0, 0, 0, 0], [0, 1.0, 0, 0], [0, 0, 1.0, 0]])
    for jt, d in pending:                    # meshes after all joints exist (envelopes name any joint)
        while d:
            pobjs, p = [], dat.u32(d + 0xC)
            while p:
                pobj = _pobj(dat, p, jt.index, index_of)
                if pobj.kind == "env" and not (jt.flags & 0x2):
                    raise UnsupportedModel("envelope meshes outside the skeleton root are not supported")
                pobjs.append(pobj)
                p = dat.u32(p + 4)
            jt.dobjs.append(DObj(_material(dat, dat.u32(d + 8)), pobjs))
            d = dat.u32(d + 4)
    return joints
