"""Whole-DOL generation: shards of C, the entry table, guest metadata, MEM1 image and a report."""
import struct
import zlib
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from .decode import TRAP_OPS, decode
from .function import Func, Program, c_name
from .gecko import apply_codes, branch_targets, code_words, parent_return_skips


# Branches whose target is outside every function by design; emitted as guest_trap.
# __OSDBJump: `bla 0x60` into the debugger stub the OS copies to the exception vectors.
KNOWN_UNRESOLVED = {0x803435B0: 0x00000060}


@dataclass
class Report:
    n_funcs: int = 0
    n_entries: int = 0
    n_shards: int = 0
    unresolved: dict[int, int] = field(default_factory=dict)
    trap_ops: Counter = field(default_factory=Counter)
    trap_funcs: dict[str, Counter] = field(default_factory=dict)
    padding_words: int = 0
    jump_table_funcs: int = 0
    sda: tuple[int, int] = (0, 0)

    def summary(self) -> str:
        return (f"functions={self.n_funcs} entries={self.n_entries} shards={self.n_shards} "
                f"jump_table_funcs={self.jump_table_funcs} unresolved={len(self.unresolved)} "
                f"padding_words={self.padding_words} traps={dict(self.trap_ops)} "
                f"r2=0x{self.sda[0]:08X} r13=0x{self.sda[1]:08X}")


def sda_bases(dol, fns) -> tuple[int, int]:
    f = next(s for s in fns if s.name == "__init_registers")
    regs: dict[int, int] = {}
    for a in range(f.addr, f.addr + f.size, 4):
        i = decode(dol.word(a))
        if i.op == "addis" and i.ra == 0:
            regs[i.rd] = (i.simm << 16) & 0xFFFFFFFF
        elif i.op == "ori" and i.rs in regs:
            regs[i.ra] = regs[i.rs] | i.uimm
        elif i.op == "addi" and i.ra in regs:
            regs[i.rd] = (regs[i.ra] + i.simm) & 0xFFFFFFFF
    return regs[2], regs[13]


def _data_words(dol) -> list[int]:
    out = []
    for s in dol.data_sections:
        n = len(s.data) // 4
        out.extend(struct.unpack_from(f">{n}I", s.data, 0))
    return out


def _write(path: Path, text: str) -> None:
    """Write only when the content changed, so make rebuilds only what changed."""
    if not path.exists() or path.read_text() != text:
        path.write_text(text)


def _injection_sites(patched: dict[int, int], bodies) -> list[tuple[int, int]]:
    """(patched site, body address) for each C2 injection: the site holds `b body`."""
    starts = {a for a, _ in bodies}
    out = []
    for site, w in patched.items():
        if w >> 26 == 18 and not w & 3:
            t = (site + ((w & 0x03FFFFFC) ^ 0x02000000) - 0x02000000) & 0xFFFFFFFF
            if t in starts:
                out.append((site, t))
    return out


def generate(dol, fns, out: Path, shard_funcs: int = 150, hle_names=(), gecko: str = "") -> Report:
    """hle_names: guest functions replaced by host functions `uint32_t hle_<name>(Cpu *)`.
    gecko: Gecko code text (04/C2) applied to code and to the MEM1 image."""
    out.mkdir(parents=True, exist_ok=True)
    by_name = {s.name: s.addr for s in fns}
    missing = [n for n in hle_names if n not in by_name]
    if missing:
        raise ValueError(f"unknown HLE functions: {missing}")
    hle = {by_name[n]: n for n in hle_names}
    patched, bodies = apply_codes(gecko) if gecko else ({}, [])
    word = lambda a: patched.get(a, dol.word(a))  # noqa: E731
    funcs = [Func(s.name, s.addr, s.size, tuple(word(a) for a in range(s.addr, s.addr + s.size, 4)))
             for s in fns]
    funcs += [Func(f"gecko_{a:08X}", a, 4 * len(b), code_words(a, b)) for a, b in bodies]
    # a Gecko body runs inside the function it is injected in; one that returns past its caller's return site
    # (parent_return_skips) needs landings at every call of that function
    sites = {body_addr: site for site, body_addr in _injection_sites(patched, bodies)}
    skips: dict[int, set[int]] = {}
    for a, b in bodies:
        ks = parent_return_skips(code_words(a, b))
        host = next((s for s in fns if s.addr <= sites[a] < s.addr + s.size), None) if ks else None
        if host is not None:
            skips.setdefault(host.addr, set()).update(ks)
    prog = Program(funcs, _data_words(dol), hle, entries={t for _, b in bodies for t in branch_targets(b)},
                   return_offsets=skips)
    rep = Report(n_funcs=len(funcs), n_entries=len(prog.entries), unresolved={pc: t for pc, t in prog.unresolved.items() if KNOWN_UNRESOLVED.get(pc) != t},
                 jump_table_funcs=len(prog.cases), sda=sda_bases(dol, fns))
    for f in funcs:
        for w in f.words:
            op = decode(w).op
            if op == "unknown" and w == 0:
                rep.padding_words += 1
            elif op in TRAP_OPS:
                rep.trap_ops[op] += 1
                rep.trap_funcs.setdefault(op, Counter())[f.name] += 1
    entries = sorted(prog.entries)
    by_func: dict[int, list[int]] = {}
    for e in entries:
        by_func.setdefault(prog.func_at(e).addr, []).append(e)
    _write(out / "guest_fns.h",
           "#pragma once\n#include \"cpu.h\"\n" + "".join(f"uint32_t {c_name(e)}(Cpu *c);\n" for e in entries)
           + "".join(f"uint32_t hle_{n}(Cpu *c);\n" for n in sorted(hle.values())))
    for n in range(0, len(funcs), shard_funcs):
        parts = ['#include "cpu.h"', '#include "guest_fns.h"', ""]
        for f in funcs[n:n + shard_funcs]:
            for e in by_func[f.addr]:
                parts.append(prog.emit(e))
                parts.append("")
        _write(out / f"shard_{n // shard_funcs:03d}.c", "\n".join(parts))
        rep.n_shards += 1
    table = ['#include "guest_table.h"', '#include "guest_fns.h"', '#include "guest_meta.h"',
             "const GuestEntry g_guest_entries[] = {"]
    table += [f"  {{0x{e:08X}u, {c_name(e)}}}," for e in entries]
    table += ["};", "const size_t g_guest_nentries = sizeof g_guest_entries / sizeof g_guest_entries[0];",
              "const GuestName g_guest_names[] = {"]
    table += [f'  {{0x{f.addr:08X}u, "{f.name}"}},' for f in funcs]
    table += ["};", "const size_t g_guest_nnames = sizeof g_guest_names / sizeof g_guest_names[0];"]
    _write(out / "guest_table.c", "\n".join(table) + "\n")
    img = bytearray(dol.mem1_image())
    for a, v in [*patched.items(), *((a + 4 * k, w) for a, b in bodies for k, w in enumerate(b))]:
        img[a & 0x01FFFFFF:(a & 0x01FFFFFF) + 4] = v.to_bytes(4, "big")
    img = bytes(img)
    _write(out / "guest_meta.h", "\n".join([
        "#pragma once", "#include <stddef.h>", "#include <stdint.h>",
        f"#define GUEST_R2 0x{rep.sda[0]:08X}u", f"#define GUEST_R13 0x{rep.sda[1]:08X}u",
        f"#define GUEST_ENTRY 0x{dol.entry:08X}u", f"#define GUEST_BSS_ADDR 0x{dol.bss_addr:08X}u",
        f"#define GUEST_BSS_SIZE 0x{dol.bss_size:08X}u", f'#define GUEST_MAIN_DOL_SHA1 "{dol.sha1}"',
        f"#define GUEST_MEM1_CRC32 0x{zlib.crc32(img):08X}u",
        "typedef struct { uint32_t addr; const char *name; } GuestName;",
        "extern const GuestName g_guest_names[];", "extern const size_t g_guest_nnames;", ""]))
    if not (out / "mem1.bin").exists() or (out / "mem1.bin").read_bytes() != img:
        (out / "mem1.bin").write_bytes(img)
    lines = [rep.summary(), "", "Trap instructions (HLE candidates for plan 1c):"]
    for op, fc in sorted(rep.trap_funcs.items()):
        lines.append(f"  {op}: " + ", ".join(f"{n}x{k}" for n, k in sorted(fc.items())))
    lines += ["", "Unresolved branches (pc -> target):"]
    lines += [f"  {pc:08X} -> {t:08X}" for pc, t in sorted(rep.unresolved.items())]
    lines += ["", "Largest functions:"]
    lines += [f"  {f.name} 0x{f.addr:08X} {f.size // 4} insns" for f in sorted(funcs, key=lambda f: -f.size)[:20]]
    (out / "report.txt").write_text("\n".join(lines) + "\n")
    return rep
