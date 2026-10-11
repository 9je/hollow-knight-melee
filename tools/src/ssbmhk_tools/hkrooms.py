"""Hollow Knight scenes to room JSON: terrain colliders (layer 8) in world coordinates and transition gates.
Reads the installed game's files (read-only) with UnityPy."""
import argparse
import json
from pathlib import Path

import UnityPy

DEFAULT_DATA = str(Path.home() / ".steam/steam/steamapps/common/Hollow Knight/hollow_knight_Data")
TERRAIN_LAYER = 8
COLLIDERS = ("BoxCollider2D", "PolygonCollider2D", "EdgeCollider2D")


def scene_files(data_dir) -> dict:
    env = UnityPy.load(str(Path(data_dir) / "globalgamemanagers"))
    for o in env.objects:
        if o.type.name == "BuildSettings":
            return {Path(s).stem: str(Path(data_dir) / f"level{i}") for i, s in enumerate(o.read().scenes)}
    raise ValueError("no BuildSettings in globalgamemanagers")


def trs_matrix(pos, rot, scale):
    x, y, z, w = rot
    r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
         [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
         [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    m = [[r[i][j] * scale[j] for j in range(3)] + [pos[i]] for i in range(3)]
    return m + [[0.0, 0.0, 0.0, 1.0]]


def _components(go):
    """A GameObject's component pointers (plain PPtrs, or ComponentPair-like items with .component)."""
    return [x if hasattr(x, "path_id") else x.component for x in go.m_Components]


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def apply(m, x, y):
    return (m[0][0] * x + m[0][1] * y + m[0][3], m[1][0] * x + m[1][1] * y + m[1][3])


def _c(v, name):
    """A component of a UnityPy vector/quaternion (lower-case NodeHelper fields or upper-case math types)."""
    return float(getattr(v, name, getattr(v, name.upper(), 0.0)))


def _v(v):
    return (_c(v, "x"), _c(v, "y"), _c(v, "z"))


def world_matrix(tid, transforms, cache) -> list:
    """World matrix of Transform `tid`: parent * child up the m_Father chain, like Unity's TransformPoint."""
    if tid in cache:
        return cache[tid]
    t = transforms[tid]
    q = t.m_LocalRotation
    m = trs_matrix(_v(t.m_LocalPosition), (_c(q, 'x'), _c(q, 'y'), _c(q, 'z'), _c(q, 'w')), _v(t.m_LocalScale))
    father = t.m_Father.path_id
    if father and father in transforms:
        m = mat_mul(world_matrix(father, transforms, cache), m)
    cache[tid] = m
    return m


def active_in_hierarchy(tid, transforms, gos) -> bool:
    """GameObject.activeInHierarchy: the object and every ancestor active."""
    while tid in transforms:
        t = transforms[tid]
        if not gos[t.m_GameObject.path_id].m_IsActive:
            return False
        tid = t.m_Father.path_id
    return True


def extract_scene(data_dir, scene) -> dict:
    env = UnityPy.load(scene_files(data_dir)[scene])
    by_id = {o.path_id: o for o in env.objects}
    gos, transforms = {}, {}
    for o in env.objects:
        if o.type.name == "GameObject":
            gos[o.path_id] = o.read()
        elif o.type.name == "Transform":
            transforms[o.path_id] = o.read()
    go_transform = {t.m_GameObject.path_id: pid for pid, t in transforms.items()}
    world_cache = {}

    def world(tid):
        return world_matrix(tid, transforms, world_cache)

    def active(tid):
        return active_in_hierarchy(tid, transforms, gos)

    def effector(go_id):
        return any(by_id[c.path_id].type.name == "PlatformEffector2D"
                   for c in _components(gos[go_id]) if c.path_id in by_id)

    colliders, gates = [], []
    for o in env.objects:
        if o.type.name in COLLIDERS:
            c = o.read()
            go_id = c.m_GameObject.path_id
            go = gos.get(go_id)
            if go is None or go.m_Layer != TERRAIN_LAYER or c.m_IsTrigger or not c.m_Enabled:
                continue
            tid = go_transform.get(go_id)
            if tid is None or not active(tid):
                continue
            m, off = world(tid), (_c(c.m_Offset, 'x'), _c(c.m_Offset, 'y'))
            if o.type.name == "BoxCollider2D":
                hx, hy = _c(c.m_Size, 'x') / 2, _c(c.m_Size, 'y') / 2
                local = [(-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy)]
                paths, kind, closed = [local], "box", True
            elif o.type.name == "PolygonCollider2D":
                paths = [[(_c(p, 'x'), _c(p, 'y')) for p in path] for path in c.m_Points.m_Paths]
                kind, closed = "poly", True
            else:
                paths, kind, closed = [[(_c(p, 'x'), _c(p, 'y')) for p in c.m_Points]], "edge", False
            one_way = effector(go_id)
            for path in paths:
                pts = [list(apply(m, x + off[0], y + off[1])) for x, y in path]
                if len(pts) >= 2:
                    colliders.append({"name": go.m_Name, "kind": kind, "closed": closed, "one_way": one_way, "points": pts})
        elif o.type.name == "MonoBehaviour":
            try:
                mb = o.read()
                if mb.m_Script.read().m_ClassName != "TransitionPoint":
                    continue
            except Exception:
                continue
            go_id = mb.m_GameObject.path_id
            tid = go_transform.get(go_id)
            if tid is None:
                continue
            box = next((by_id[x.path_id].read() for x in _components(gos[go_id])
                        if x.path_id in by_id and by_id[x.path_id].type.name == "BoxCollider2D"), None)
            m = world(tid)
            ox, oy = (_c(box.m_Offset, 'x'), _c(box.m_Offset, 'y')) if box else (0.0, 0.0)
            cx, cy = apply(m, ox, oy)
            w, h = (abs(_c(box.m_Size, 'x') * m[0][0]), abs(_c(box.m_Size, 'y') * m[1][1])) if box else (0.0, 0.0)
            gates.append({"name": gos[go_id].m_Name, "x": cx, "y": cy, "w": w, "h": h})
    return {"scene": scene, "colliders": colliders, "gates": gates}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Extract Hollow Knight scenes' terrain colliders and gates to JSON (read-only).")
    ap.add_argument("scenes", nargs="*", help="scene names (default: all)")
    ap.add_argument("--data", default=DEFAULT_DATA)
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args(argv)
    a.out.mkdir(parents=True, exist_ok=True)
    names = a.scenes or sorted(scene_files(a.data))
    for name in names:
        try:
            room = extract_scene(a.data, name)
        except Exception as e:   # one broken scene must not stop the batch
            print(f"ERROR {name}: {e}")
            continue
        (a.out / f"{name}.json").write_text(json.dumps(room))
        print(f"{name}: {len(room['colliders'])} colliders, {len(room['gates'])} gates")
    return 0
