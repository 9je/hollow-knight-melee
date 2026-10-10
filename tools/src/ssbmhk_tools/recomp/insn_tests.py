"""Per-instruction differential fuzz cases: every distinct non-branch word inside main.dol's functions."""
import random
from collections import Counter
from pathlib import Path

from .decode import BRANCH_OPS, NOP_OPS, decode
from .emit import FUZZ_EXCLUDE, HOOKED_SPR, MT_HOOKED_SPR, emit_insn

# ea_kind: 0 none, 1 D-form rA|0 + disp, 2 X-form rA|0 + rB, 3 update D-form, 4 update X-form
_D = {"lbz", "lhz", "lha", "lwz", "stb", "sth", "stw", "lmw", "stmw", "lfs", "lfd", "stfs", "stfd", "psq_l", "psq_st"}
_DU = {"lbzu", "lhzu", "lhau", "lwzu", "stbu", "sthu", "stwu", "lfsu", "lfdu", "stfsu", "stfdu", "psq_lu", "psq_stu"}
_X = {"lbzx", "lhzx", "lhax", "lwzx", "stbx", "sthx", "stwx", "lwbrx", "lhbrx", "stwbrx", "sthbrx",
      "lfsx", "lfdx", "stfsx", "stfdx", "stfiwx", "dcbz"}
_XU = {"lbzux", "lhzux", "lhaux", "lwzux", "stbux", "sthux", "stwux", "lfsux", "lfdux", "stfsux", "stfdux"}
WINDOW = 0x80100000


def ea_kind(op: str) -> int:
    return 1 if op in _D else 3 if op in _DU else 2 if op in _X else 4 if op in _XU else 0


def _wanted(i) -> bool:
    if i.op in BRANCH_OPS or i.op in FUZZ_EXCLUDE or i.op in NOP_OPS:
        return False
    if i.op == "mfspr" and i.spr in HOOKED_SPR:
        return False
    if i.op == "mtspr" and i.spr in MT_HOOKED_SPR:
        return False
    return True


def write_cases(dol, fns, out_dir: Path, per_op: int, seed: int, ops: set[str] | None) -> dict:
    words = set()
    for f in fns:
        for a in range(f.addr, f.addr + f.size, 4):
            words.add(dol.word(a))
    by_op: dict[str, list] = {}
    for w in sorted(words):
        i = decode(w)
        if _wanted(i) and (ops is None or i.op in ops):
            by_op.setdefault(i.op, []).append(i)
    rng = random.Random(seed)
    cases, skipped, ra0 = [], Counter(), Counter()
    for op in sorted(by_op):
        lst = by_op[op]
        if len(lst) > per_op:
            lst = rng.sample(lst, per_op)
        for i in lst:
            k = ea_kind(op)
            if k in (1, 3) and i.ra == 0 or k == 4 and i.ra == 0:
                ra0[op] += 1
                continue
            try:
                body = emit_insn(i, WINDOW)
            except NotImplementedError:
                skipped[op] += 1
                continue
            disp = i.psq_d if op.startswith("psq") else i.simm if k in (1, 3) else 0
            cases.append((i, body, k, disp))
    out_dir.mkdir(parents=True, exist_ok=True)
    lines = ['#include "cpu.h"', '#include "insn_case.h"']
    for n, (i, body, _, _) in enumerate(cases):
        lines.append(f"static void t_{n}(Cpu *c) {{ {body} }}")
    lines.append("const InsnCase g_cases[] = {")
    for n, (i, _, k, disp) in enumerate(cases):
        lines.append(f'  {{0x{i.word:08X}u, "{i.op}", t_{n}, {k}, {i.ra}, {i.rb}, {disp}}},')
    lines.append("};")
    lines.append(f"const size_t g_ncases = {len(cases)};")
    (out_dir / "insn_cases.c").write_text("\n".join(lines) + "\n")
    return {"cases": len(cases), "ops": len({c[0].op for c in cases}),
            "skipped_not_implemented": dict(skipped), "skipped_ra0": dict(ra0)}
