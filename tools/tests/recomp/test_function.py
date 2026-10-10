import subprocess
import textwrap
from pathlib import Path

import pytest

from ssbmhk_tools.recomp.function import Func, Program, c_name

from . import asm

CORE = Path(__file__).resolve().parents[3] / "core"


def build_and_run(tmp_path, funcs, data_words, main_body):
    prog = Program(funcs, data_words)
    defs = "\n".join(prog.emit(e) for e in sorted(prog.entries))
    protos = "\n".join(f"uint32_t {c_name(e)}(Cpu *c);" for e in sorted(prog.entries))
    table = ",\n".join(f"{{0x{e:08X}u, {c_name(e)}}}" for e in sorted(prog.entries))
    src = tmp_path / "t.c"
    src.write_text(textwrap.dedent(f"""
        #include <stdio.h>
        #include <stdlib.h>
        #include "cpu.h"
        #include "guest_table.h"
        #include "test_hooks.h"
        {protos}
        {defs}
        const GuestEntry g_guest_entries[] = {{ {table} }};
        const size_t g_guest_nentries = sizeof g_guest_entries / sizeof g_guest_entries[0];
        int main(void) {{
          Cpu *c = calloc(1, sizeof *c); c->mem = calloc(1, MEM1_SIZE); th_lookup = guest_lookup;
          uint32_t ret = 0; int st;
          {main_body}
          return 0;
        }}
    """))
    exe = tmp_path / "t"
    subprocess.run(["gcc", "-O2", "-std=c11", "-ffp-contract=off", "-fwrapv", "-fno-strict-aliasing",
                    "-Wno-unused-label", f"-I{CORE}/runtime", f"-I{CORE}/gekko", str(src),
                    str(CORE / "runtime/test_hooks.c"), str(CORE / "runtime/dispatch.c"),
                    str(CORE / "gekko/gekko_fp.c"), "-lm", "-o", str(exe)], check=True)
    return subprocess.run([str(exe)], capture_output=True, text=True, check=True).stdout


def F(addr, words, name=None):
    return Func(name or f"f{addr:X}", addr, 4 * len(words), tuple(words))


def test_loop_call_and_return(tmp_path):
    # f1: r3 = 0; ctr = 5; loop: r3 += 2 (via call to f2); bdnz loop; return
    f1 = F(0x80001000, [asm.mflr(0), asm.stw(0, 4, 1), asm.addi(3, 0, 0), asm.addi(4, 0, 5), asm.mtctr(4),
                        asm.bl(0x80001100 - 0x80001014), asm.bc(16, 0, -4), asm.lwz(0, 4, 1), asm.mtlr(0), asm.blr()])
    f2 = F(0x80001100, [asm.addi(3, 3, 2), asm.blr()])
    out = build_and_run(tmp_path, [f1, f2], [], """
        c->gpr[1] = 0x80100000u; LR = 0x80000100u;
        st = th_call(c, fn_80001000, &ret);
        printf("%d %u %08X\\n", st, c->gpr[3], ret);""")
    assert out.strip() == "0 10 80000100"


def test_jump_table_switch_and_indirect_miss(tmp_path):
    # f: r4 = table[r3]; mtctr r4; bctr; case0: r3=10 blr; case1: r3=11 blr
    base = 0x80002000
    words = [asm.slwi(5, 3, 2), asm.lwzx(4, 6, 5), asm.mtctr(4), asm.bctr(),
             asm.addi(3, 0, 10), asm.blr(), asm.addi(3, 0, 11), asm.blr()]
    f = F(base, words)
    table = [base + 16, base + 24]
    out = build_and_run(tmp_path, [f], table, f"""
        c->gpr[6] = 0x80200000u; st32(c, 0x80200000u, 0x{table[0]:08X}u); st32(c, 0x80200004u, 0x{table[1]:08X}u);
        LR = 0x80000100u; c->gpr[3] = 1; st = th_call(c, fn_{base:08X}, &ret); printf("%d %u\\n", st, c->gpr[3]);
        st32(c, 0x80200000u, 0x80009990u); c->gpr[3] = 0; st = th_call(c, fn_{base:08X}, &ret);
        printf("%d %08X\\n", st, th_last_target);""")
    assert out.split("\n")[:2] == ["0 11", "4 80009990"]   # TH_INDIRECT_MISS reports the address


def test_branch_into_middle_of_other_function_gets_entry(tmp_path):
    # g tail-branches into the middle of h (shared epilogue)
    h = F(0x80003000, [asm.addi(3, 0, 1), asm.addi(3, 3, 100), asm.blr()])
    g = F(0x80003100, [asm.addi(3, 0, 5), asm.b(0x80003004 - 0x80003104)])
    prog = Program([h, g], [])
    assert 0x80003004 in prog.entries
    out = build_and_run(tmp_path, [h, g], [], """
        LR = 0x80000100u; st = th_call(c, fn_80003100, &ret); printf("%d %u %08X\\n", st, c->gpr[3], ret);""")
    assert out.strip() == "0 105 80000100"


def test_bad_return_is_reported(tmp_path):
    # f calls g; g overwrites LR then returns -> caller sees wrong return address
    f = F(0x80004000, [asm.mflr(0), asm.stw(0, 4, 1), asm.bl(0x100), asm.lwz(0, 4, 1), asm.mtlr(0), asm.blr()])
    g = F(0x80004108, [asm.addi(5, 0, 0x40), asm.mtlr(5), asm.blr()])
    out = build_and_run(tmp_path, [f, g], [], """
        c->gpr[1] = 0x80100000u; LR = 0x80000100u; st = th_call(c, fn_80004000, &ret); printf("%d\\n", st);""")
    assert out.strip() == "3"   # TH_BAD_RETURN
