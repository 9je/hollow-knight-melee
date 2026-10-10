"""Decomp config/GALE01/symbols.txt parsing."""
import re
from dataclasses import dataclass

_PAT = re.compile(r"^(\S+) = ([\w.]+):0x([0-9A-Fa-f]+); // type:(\w+)(?: size:0x([0-9A-Fa-f]+))?")


@dataclass(frozen=True)
class Symbol:
    name: str
    section: str
    addr: int
    size: int
    kind: str


def parse_symbols(text: str) -> list[Symbol]:
    out = []
    for line in text.splitlines():
        m = _PAT.match(line)
        if m:
            name, sec, addr, kind, size = m.groups()
            out.append(Symbol(name, sec, int(addr, 16), int(size or "0", 16), kind))
    return out


def functions(syms: list[Symbol]) -> list[Symbol]:
    return sorted((s for s in syms if s.kind == "function"), key=lambda s: s.addr)
