"""main.dol: extraction from the ISO, section access, MEM1 image."""
import hashlib
import struct
from dataclasses import dataclass, field
from pathlib import Path

MAIN_DOL_SHA1 = "08e0bf20134dfcb260699671004527b2d6bb1a45"
MEM1_BASE = 0x80000000
MEM1_SIZE = 0x01800000


class WrongDol(Exception):
    pass


@dataclass(frozen=True)
class Section:
    addr: int
    data: bytes
    text: bool

    @property
    def end(self) -> int:
        return self.addr + len(self.data)


@dataclass(frozen=True)
class Dol:
    sections: list[Section]
    bss_addr: int
    bss_size: int
    entry: int
    sha1: str = field(default="")

    @property
    def text_sections(self) -> list[Section]:
        return [s for s in self.sections if s.text]

    @property
    def data_sections(self) -> list[Section]:
        return [s for s in self.sections if not s.text]

    def word(self, addr: int) -> int:
        for s in self.sections:
            if s.addr <= addr and addr + 4 <= s.end:
                return struct.unpack_from(">I", s.data, addr - s.addr)[0]
        raise KeyError(f"{addr:08X} not in any DOL section")

    def mem1_image(self) -> bytearray:
        img = bytearray(MEM1_SIZE)
        img[self.bss_addr - MEM1_BASE:self.bss_addr - MEM1_BASE + self.bss_size] = bytes(self.bss_size)
        for s in self.sections:
            img[s.addr - MEM1_BASE:s.end - MEM1_BASE] = s.data
        return img


def parse_dol(blob: bytes) -> Dol:
    offs = struct.unpack_from(">18I", blob, 0)
    addrs = struct.unpack_from(">18I", blob, 0x48)
    sizes = struct.unpack_from(">18I", blob, 0x90)
    bss_addr, bss_size, entry = struct.unpack_from(">III", blob, 0xD8)
    sections = [Section(addrs[i], bytes(blob[offs[i]:offs[i] + sizes[i]]), i < 7)
                for i in range(18) if sizes[i]]
    return Dol(sections, bss_addr, bss_size, entry, hashlib.sha1(blob).hexdigest())


def read_main_dol(iso: Path) -> bytes:
    with iso.open("rb") as f:
        f.seek(0x420)
        off = struct.unpack(">I", f.read(4))[0]
        f.seek(off)
        hdr = f.read(0x100)
        offs = struct.unpack_from(">18I", hdr, 0)
        sizes = struct.unpack_from(">18I", hdr, 0x90)
        end = max(o + s for o, s in zip(offs, sizes) if s)
        f.seek(off)
        return f.read(end)


def load_main_dol(iso: Path, *, check_sha1: bool = True) -> Dol:
    blob = read_main_dol(iso)
    sha1 = hashlib.sha1(blob).hexdigest()
    if check_sha1 and sha1 != MAIN_DOL_SHA1:
        raise WrongDol(f"main.dol SHA-1 is {sha1}, expected {MAIN_DOL_SHA1} (NTSC 1.02, unpatched)")
    return parse_dol(blob)
