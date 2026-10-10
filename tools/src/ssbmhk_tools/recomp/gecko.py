"""Gecko codes applied at recompile time, the way Dolphin's code handler applies them.

Supported: 04 (32-bit write) and C2 (insert ASM). A C2 body is placed in the code-handler area
from CAVE; its final 00000000 word becomes the branch back, and the patched site becomes `b body`.
"""

CAVE = 0x80001800


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
    return patched, bodies
