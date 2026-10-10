"""Whole-DOL generation: shards of C, the entry table, guest metadata, MEM1 image and a report."""
import struct
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from .decode import TRAP_OPS, decode
from .function import Func, Program, c_name


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


def generate(dol, fns, out: Path, shard_funcs: int = 150) -> Report:
    out.mkdir(parents=True, exist_ok=True)
    for old in out.glob("shard_*.c"):
        old.unlink()
    funcs = [Func(s.name, s.addr, s.size, tuple(dol.word(a) for a in range(s.addr, s.addr + s.size, 4)))
             for s in fns]
    prog = Program(funcs, _data_words(dol))
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
    (out / "guest_fns.h").write_text(
        "#pragma once\n#include \"cpu.h\"\n" + "".join(f"uint32_t {c_name(e)}(Cpu *c);\n" for e in entries))
    for n in range(0, len(funcs), shard_funcs):
        parts = ['#include "cpu.h"', '#include "guest_fns.h"', ""]
        for f in funcs[n:n + shard_funcs]:
            for e in by_func[f.addr]:
                parts.append(prog.emit(e))
                parts.append("")
        (out / f"shard_{n // shard_funcs:03d}.c").write_text("\n".join(parts))
        rep.n_shards += 1
    table = ['#include "guest_table.h"', '#include "guest_fns.h"', '#include "guest_meta.h"',
             "const GuestEntry g_guest_entries[] = {"]
    table += [f"  {{0x{e:08X}u, {c_name(e)}}}," for e in entries]
    table += ["};", "const size_t g_guest_nentries = sizeof g_guest_entries / sizeof g_guest_entries[0];",
              "const GuestName g_guest_names[] = {"]
    table += [f'  {{0x{f.addr:08X}u, "{f.name}"}},' for f in funcs]
    table += ["};", "const size_t g_guest_nnames = sizeof g_guest_names / sizeof g_guest_names[0];"]
    (out / "guest_table.c").write_text("\n".join(table) + "\n")
    (out / "guest_meta.h").write_text("\n".join([
        "#pragma once", "#include <stddef.h>", "#include <stdint.h>",
        f"#define GUEST_R2 0x{rep.sda[0]:08X}u", f"#define GUEST_R13 0x{rep.sda[1]:08X}u",
        f"#define GUEST_ENTRY 0x{dol.entry:08X}u", f"#define GUEST_BSS_ADDR 0x{dol.bss_addr:08X}u",
        f"#define GUEST_BSS_SIZE 0x{dol.bss_size:08X}u", f'#define GUEST_MAIN_DOL_SHA1 "{dol.sha1}"',
        "typedef struct { uint32_t addr; const char *name; } GuestName;",
        "extern const GuestName g_guest_names[];", "extern const size_t g_guest_nnames;", ""]))
    (out / "mem1.bin").write_bytes(bytes(dol.mem1_image()))
    lines = [rep.summary(), "", "Trap instructions (HLE candidates for plan 1c):"]
    for op, fc in sorted(rep.trap_funcs.items()):
        lines.append(f"  {op}: " + ", ".join(f"{n}x{k}" for n, k in sorted(fc.items())))
    lines += ["", "Unresolved branches (pc -> target):"]
    lines += [f"  {pc:08X} -> {t:08X}" for pc, t in sorted(rep.unresolved.items())]
    lines += ["", "Largest functions:"]
    lines += [f"  {f.name} 0x{f.addr:08X} {f.size // 4} insns" for f in sorted(funcs, key=lambda f: -f.size)[:20]]
    (out / "report.txt").write_text("\n".join(lines) + "\n")
    return rep
