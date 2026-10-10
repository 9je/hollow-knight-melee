"""Gecko codes applied at recompile time, the way Dolphin's code handler applies them.

Supported: 04 (32-bit write) and C2 (insert ASM). A C2 body is placed in the code-handler area
from CAVE; its final 00000000 word becomes the branch back, and the patched site becomes `b body`.
"""

import re

from .decode import decode
from .function import BLRL

CAVE = 0x80001800
CAVE_END = 0x80002F00  # 0x80002F00-0x80002FFF: runtime words (core/runtime/frame.c); the DOL starts at 0x80003100


class GeckoError(ValueError):
    pass


def _words(text: str) -> list[int]:
    out = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line[0] in "$*#!":
            continue
        a, b = line.split()[:2]
        out += [int(a, 16), int(b, 16)]
    return out


def parse(text: str) -> tuple[list[tuple[int, int]], list[tuple[int, list[int]]]]:
    """Returns (writes [(addr, value)], injections [(addr, body words)])."""
    w = _words(text)
    writes, injections = [], []
    k = 0
    while k < len(w):
        kind, addr = w[k] >> 24 & 0xFE, 0x80000000 | (w[k] & 0x01FFFFFF)
        if kind == 0x04:
            writes.append((addr, w[k + 1]))
            k += 2
        elif kind == 0xC2:
            n = w[k + 1]
            body = w[k + 2:k + 2 + 2 * n]
            if len(body) != 2 * n or body[-1] != 0:
                raise GeckoError(f"malformed C2 code at {addr:08X}")
            injections.append((addr, body))
            k += 2 + 2 * n
        else:
            raise GeckoError(f"unsupported Gecko code type {w[k] >> 24:02X}")
    return writes, injections


def _b(src: int, dst: int) -> int:
    return (18 << 26) | ((dst - src) & 0x03FFFFFC)


def apply_codes(text: str) -> tuple[dict[int, int], list[tuple[int, list[int]]]]:
    """Returns (patched words {addr: value}, bodies [(addr, words)])."""
    writes, injections = parse(text)
    patched = dict(writes)
    bodies, at = [], CAVE
    for addr, body in injections:
        body = list(body)
        body[-1] = _b(at + 4 * (len(body) - 1), addr + 4)
        patched[addr] = _b(addr, at)
        bodies.append((at, body))
        at += 4 * len(body)
    if at > CAVE_END:
        raise GeckoError(f"code bodies need {at - CAVE:#x} bytes; the cave holds {CAVE_END - CAVE:#x}")
    return patched, bodies


_CODE = re.compile(r"^([0-9A-Fa-f]{8} [0-9A-Fa-f]{8})\s*(?:#(.*))?$")


def select_ini(text: str, sections: tuple[str, ...], paths: set[str]) -> str:
    """Gecko text for the codes named by their `#path` comment, taken from INI sections whose `$` header
    starts with one of `sections`. Raises if any named code is missing."""
    out, found, sec, keep = [], set(), "", False
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("$") or line.startswith("["):
            sec, keep = line, False
            continue
        m = _CODE.match(line)
        if not m:
            continue
        if m.group(2) is not None:
            name = m.group(2).strip()
            keep = sec.startswith(sections) and name in paths
            if keep:
                found.add(name)
                out.append(f"* {name}")
        if keep:
            out.append(m.group(1))
    if missing := paths - found:
        raise GeckoError(f"codes not found: {', '.join(sorted(missing))}")
    return "\n".join(out) + "\n"


if __name__ == "__main__":  # python -m ssbmhk_tools.recomp.gecko <ini> <code list> <out>
    import sys
    ini, names, dst = sys.argv[1:4]
    paths = {ln.strip() for ln in open(names) if ln.strip() and not ln.startswith("#")}
    text = select_ini(open(ini).read(), ("$Required:", "$Recommended:"), paths)
    apply_codes(text)  # validates: parses, fits the cave
    open(dst, "w").write(text)


def code_words(addr: int, words) -> tuple[int, ...]:
    """A body's words with everything not reachable from its first word replaced by 0, so data embedded
    in the code (the `bl Table` / `Table: blrl` idiom) is not recompiled as instructions."""
    n, seen, todo = len(words), set(), [0]
    inside = lambda t: 0 <= (t - addr) // 4 < n and t >= addr  # noqa: E731
    while todo:
        k = todo.pop()
        if k in seen or not 0 <= k < n:
            continue
        seen.add(k)
        i, pc = decode(words[k]), addr + 4 * k
        always = i.op == "b" or (i.op in ("bc", "bclr", "bcctr") and i.bo & 0x14 == 0x14)
        if i.op in ("b", "bc"):
            t = ((i.li if i.op == "b" else i.bd) if i.aa else pc + (i.li if i.op == "b" else i.bd)) & 0xFFFFFFFF
            if i.lk and inside(t) and words[(t - addr) // 4] == BLRL:
                seen.add((t - addr) // 4)   # kept, so the call site still recognises the idiom
                todo.append(k + 1)
                continue
            if inside(t):
                todo.append((t - addr) // 4)
            if i.lk or not always:
                todo.append(k + 1)
        elif i.op in ("bclr", "bcctr"):
            if i.lk or not always:
                todo.append(k + 1)
        elif i.op != "rfi":
            todo.append(k + 1)
    return tuple(w if k in seen else 0 for k, w in enumerate(words))


def branch_targets(words) -> set[int]:
    """Constant targets of `lis rX,hi; ori rX,rX,lo; mtctr rX; bctr[l]` (Slippi's branch / branchl macros):
    indirect jumps that may land mid-function, so the recompiler needs them as entries."""
    out = set()
    for k in range(len(words) - 3):
        a, b, m, j = words[k:k + 4]
        rx = a >> 21 & 31
        if (a >> 26 == 15 and a >> 16 & 31 == 0 and b >> 26 == 24 and b >> 21 & 31 == rx and b >> 16 & 31 == rx
                and m == 0x7C0903A6 | rx << 21 and j in (0x4E800420, 0x4E800421)):
            out.add((a & 0xFFFF) << 16 | (b & 0xFFFF))
    return out


def parent_return_skips(words) -> set[int]:
    """K for each `addi rX,rX,K; mtlr rX; blr` (K > 0): code that returns to an address past a return site,
    e.g. UCF Shield Drop returning from the function it is injected in to that function's caller + 8."""
    out = set()
    for k in range(len(words) - 2):
        a, m, r = words[k:k + 3]
        rx = a >> 21 & 31
        simm = (a & 0xFFFF) - ((a & 0x8000) << 1)
        if a >> 26 == 14 and a >> 16 & 31 == rx and rx and simm > 0 and m == 0x7C0803A6 | rx << 21 and r == 0x4E800020:
            out.add(simm)
    return out
