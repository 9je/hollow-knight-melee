"""GameCube disc images: read a file by name through the file system table (FST)."""
import struct
from pathlib import Path


def _fst(f):
    f.seek(0x424)
    off, size = struct.unpack(">II", f.read(8))
    f.seek(off)
    return f.read(size)


def files(iso: Path) -> dict:
    with open(iso, "rb") as f:
        fst = _fst(f)
    n = struct.unpack_from(">I", fst, 8)[0]
    strs = fst[n * 12:]
    out = {}
    for i in range(1, n):
        name_off, a, b = struct.unpack_from(">III", fst, i * 12)
        if not name_off >> 24:   # files only (directories have flag 1)
            o = name_off & 0xFFFFFF
            out[strs[o:strs.index(b"\0", o)].decode()] = (a, b)
    return out


def read_file(iso: Path, name: str) -> bytes:
    off, size = files(iso)[name]
    with open(iso, "rb") as f:
        f.seek(off)
        return f.read(size)
