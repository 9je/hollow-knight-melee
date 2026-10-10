"""One non-branch instruction -> C. Semantics: Dolphin's interpreter (see Global Constraints).

core/difftest/insn_fuzz is the arbiter: where this file and Dolphin disagree, this file is wrong.
"""
from .decode import NOP_OPS, TRAP_OPS, Insn


def G(n): return f"c->gpr[{n}]"
def _ra0(i): return G(i.ra) if i.ra else "0u"
def _ea_d(i): return f"{_ra0(i)} + (uint32_t){i.simm}"
def _ea_x(i): return f"{_ra0(i)} + {G(i.rb)}"
def _ea_du(i): return f"{G(i.ra)} + (uint32_t){i.simm}"
def _ea_xu(i): return f"{G(i.ra)} + {G(i.rb)}"


def _mask(mb, me):
    b = 0xFFFFFFFF >> mb
    e = (0xFFFFFFFF << (31 - me)) & 0xFFFFFFFF
    return b & e if mb <= me else b | e


def _nibbles(m8):
    return sum(0xF << (4 * k) for k in range(8) if m8 & (1 << k))


def _h(v): return f"0x{v & 0xFFFFFFFF:08X}u"


RA_DEST = {"and", "andc", "or", "orc", "xor", "nor", "nand", "eqv", "slw", "srw", "sraw", "srawi",
           "cntlzw", "extsb", "extsh", "rlwinm", "rlwnm", "rlwimi"}
HOOKED_SPR = {22, 268, 269, 284, 285, 921, 922, 923}   # DEC, TB, WPAR, DMA: runtime decides
MT_HOOKED_SPR = HOOKED_SPR | {952, 956, 1020, 1021, 1022}  # writes to MMCR0/1, THRM1-3 have Dolphin side effects


def _int_ops():
    t = {}
    t["addi"] = lambda i: f"{G(i.rd)} = {_ra0(i)} + (uint32_t){i.simm};"
    t["addis"] = lambda i: f"{G(i.rd)} = {_ra0(i)} + {_h(i.simm << 16)};"
    t["addic"] = lambda i: f"{G(i.rd)} = add_ca(c, {G(i.ra)}, (uint32_t){i.simm}, 0);"
    t["addic."] = lambda i: f"{G(i.rd)} = add_ca(c, {G(i.ra)}, (uint32_t){i.simm}, 0); CR0({G(i.rd)});"
    t["subfic"] = lambda i: f"{G(i.rd)} = add_ca(c, ~{G(i.ra)}, (uint32_t){i.simm}, 1);"
    t["mulli"] = lambda i: f"{G(i.rd)} = (uint32_t)((int64_t)(int32_t){G(i.ra)} * {i.simm});"
    t["cmpi"] = lambda i: f"cr_cmp_s(c, {i.crfd}, (int32_t){G(i.ra)}, {i.simm});"
    t["cmpli"] = lambda i: f"cr_cmp_u(c, {i.crfd}, {G(i.ra)}, {i.uimm}u);"
    t["cmp"] = lambda i: f"cr_cmp_s(c, {i.crfd}, (int32_t){G(i.ra)}, (int32_t){G(i.rb)});"
    t["cmpl"] = lambda i: f"cr_cmp_u(c, {i.crfd}, {G(i.ra)}, {G(i.rb)});"
    t["ori"] = lambda i: f"{G(i.ra)} = {G(i.rs)} | {i.uimm}u;"
    t["oris"] = lambda i: f"{G(i.ra)} = {G(i.rs)} | {_h(i.uimm << 16)};"
    t["xori"] = lambda i: f"{G(i.ra)} = {G(i.rs)} ^ {i.uimm}u;"
    t["xoris"] = lambda i: f"{G(i.ra)} = {G(i.rs)} ^ {_h(i.uimm << 16)};"
    t["andi."] = lambda i: f"{G(i.ra)} = {G(i.rs)} & {i.uimm}u; CR0({G(i.ra)});"
    t["andis."] = lambda i: f"{G(i.ra)} = {G(i.rs)} & {_h(i.uimm << 16)}; CR0({G(i.ra)});"
    t["rlwinm"] = lambda i: f"{G(i.ra)} = rotl32({G(i.rs)}, {i.sh}) & {_h(_mask(i.mb, i.me))};"
    t["rlwnm"] = lambda i: f"{G(i.ra)} = rotl32({G(i.rs)}, {G(i.rb)} & 31) & {_h(_mask(i.mb, i.me))};"
    t["rlwimi"] = lambda i: (f"{G(i.ra)} = (rotl32({G(i.rs)}, {i.sh}) & {_h(_mask(i.mb, i.me))}) | "
                             f"({G(i.ra)} & {_h(~_mask(i.mb, i.me))});")
    a, b = (lambda i: G(i.ra)), (lambda i: G(i.rb))
    t["add"] = lambda i: f"{G(i.rd)} = {a(i)} + {b(i)};"
    t["addc"] = lambda i: f"{G(i.rd)} = add_ca(c, {a(i)}, {b(i)}, 0);"
    t["adde"] = lambda i: f"{G(i.rd)} = add_ca(c, {a(i)}, {b(i)}, CA);"
    t["addze"] = lambda i: f"{G(i.rd)} = add_ca(c, {a(i)}, 0u, CA);"
    t["addme"] = lambda i: f"{G(i.rd)} = add_ca(c, {a(i)}, 0xFFFFFFFFu, CA);"
    t["subf"] = lambda i: f"{G(i.rd)} = {b(i)} - {a(i)};"
    t["subfc"] = lambda i: f"{G(i.rd)} = add_ca(c, ~{a(i)}, {b(i)}, 1);"
    t["subfe"] = lambda i: f"{G(i.rd)} = add_ca(c, ~{a(i)}, {b(i)}, CA);"
    t["subfze"] = lambda i: f"{G(i.rd)} = add_ca(c, ~{a(i)}, 0u, CA);"
    t["subfme"] = lambda i: f"{G(i.rd)} = add_ca(c, ~{a(i)}, 0xFFFFFFFFu, CA);"
    t["neg"] = lambda i: f"{G(i.rd)} = 0u - {a(i)};"
    t["mullw"] = lambda i: f"{G(i.rd)} = (uint32_t)((int64_t)(int32_t){a(i)} * (int32_t){b(i)});"
    t["mulhw"] = lambda i: f"{G(i.rd)} = (uint32_t)(((int64_t)(int32_t){a(i)} * (int32_t){b(i)}) >> 32);"
    t["mulhwu"] = lambda i: f"{G(i.rd)} = (uint32_t)(((uint64_t){a(i)} * {b(i)}) >> 32);"
    t["divw"] = lambda i: (f"{{ int32_t a_ = (int32_t){a(i)}, b_ = (int32_t){b(i)}; {G(i.rd)} = "
                           f"(b_ == 0 || ((uint32_t)a_ == 0x80000000u && b_ == -1)) ? (a_ < 0 ? 0xFFFFFFFFu : 0u) "
                           f": (uint32_t)(a_ / b_); }}")
    t["divwu"] = lambda i: f"{{ uint32_t a_ = {a(i)}, b_ = {b(i)}; {G(i.rd)} = b_ ? a_ / b_ : 0u; }}"
    s = lambda i: G(i.rs)
    t["and"] = lambda i: f"{G(i.ra)} = {s(i)} & {b(i)};"
    t["andc"] = lambda i: f"{G(i.ra)} = {s(i)} & ~{b(i)};"
    t["or"] = lambda i: f"{G(i.ra)} = {s(i)} | {b(i)};"
    t["orc"] = lambda i: f"{G(i.ra)} = {s(i)} | ~{b(i)};"
    t["xor"] = lambda i: f"{G(i.ra)} = {s(i)} ^ {b(i)};"
    t["nor"] = lambda i: f"{G(i.ra)} = ~({s(i)} | {b(i)});"
    t["nand"] = lambda i: f"{G(i.ra)} = ~({s(i)} & {b(i)});"
    t["eqv"] = lambda i: f"{G(i.ra)} = ~({s(i)} ^ {b(i)});"
    t["slw"] = lambda i: f"{{ uint32_t n_ = {b(i)} & 63u; {G(i.ra)} = n_ > 31 ? 0u : {s(i)} << n_; }}"
    t["srw"] = lambda i: f"{{ uint32_t n_ = {b(i)} & 63u; {G(i.ra)} = n_ > 31 ? 0u : {s(i)} >> n_; }}"
    t["sraw"] = lambda i: (
        f"{{ uint32_t n_ = {b(i)}, s_ = {s(i)}; if (n_ & 0x20u) {{ {G(i.ra)} = (s_ & 0x80000000u) ? 0xFFFFFFFFu : 0u; "
        f"set_ca(c, s_ >> 31); }} else {{ n_ &= 31u; {G(i.ra)} = (uint32_t)((int32_t)s_ >> n_); "
        f"set_ca(c, (s_ >> 31) && n_ && (s_ << (32 - n_)) != 0); }} }}")
    t["srawi"] = lambda i: (
        f"{G(i.ra)} = {s(i)}; set_ca(c, 0);" if i.sh == 0 else
        f"{{ uint32_t s_ = {s(i)}; {G(i.ra)} = (uint32_t)((int32_t)s_ >> {i.sh}); "
        f"set_ca(c, (s_ >> 31) && (s_ << {32 - i.sh}) != 0); }}")
    t["cntlzw"] = lambda i: f"{{ uint32_t s_ = {s(i)}; {G(i.ra)} = s_ ? (uint32_t)__builtin_clz(s_) : 32u; }}"
    t["extsb"] = lambda i: f"{G(i.ra)} = (uint32_t)(int32_t)(int8_t){s(i)};"
    t["extsh"] = lambda i: f"{G(i.ra)} = (uint32_t)(int32_t)(int16_t){s(i)};"
    t["mfcr"] = lambda i: f"{G(i.rd)} = c->cr;"
    t["mtcrf"] = lambda i: f"c->cr = (c->cr & {_h(~_nibbles(i.crm))}) | ({s(i)} & {_h(_nibbles(i.crm))});"
    t["mcrf"] = lambda i: f"cr_set(c, {i.crfd}, cr_get(c, {i.crfs}));"
    t["mcrxr"] = lambda i: f"cr_set(c, {i.crfd}, XER >> 28); XER &= 0x0FFFFFFFu;"
    for name, expr in {"crand": "x_ & y_", "crandc": "x_ & ~y_", "cror": "x_ | y_", "crorc": "x_ | ~y_",
                       "crxor": "x_ ^ y_", "crnand": "~(x_ & y_)", "crnor": "~(x_ | y_)", "creqv": "~(x_ ^ y_)"}.items():
        t[name] = (lambda e: lambda i: f"{{ uint32_t x_ = cr_bit(c, {i.ra}), y_ = cr_bit(c, {i.rb}); "
                                       f"cr_setbit(c, {i.rd}, {e}); }}")(expr)
    upmc = {937: 953, 938: 954, 941: 957, 942: 958}   # Dolphin: user PMC reads copy the PMC first

    def mfspr(i):
        n = i.spr
        if n in (268, 269):
            return f"{G(i.rd)} = guest_mftb(c, {int(n == 269)});"
        if n in HOOKED_SPR:
            return f"{G(i.rd)} = guest_mfspr(c, {n});"
        if n in upmc:
            return f"c->spr[{n}] = c->spr[{upmc[n]}]; {G(i.rd)} = c->spr[{n}];"
        if n == 1010:   # IABR reads back with TE clear
            return f"{G(i.rd)} = c->spr[1010] & ~1u;"
        return f"{G(i.rd)} = c->spr[{n}];"

    def mtspr(i):
        n, v = i.spr, s(i)
        if n == 1:
            return f"XER = {v} & XER_MASK;"
        if n in MT_HOOKED_SPR:
            return f"guest_mtspr(c, {n}, {v});"
        if n == 287:    # PVR is read-only
            return "/* mtspr PVR ignored */"
        if n == 1008:   # HID0: ICFI (flash invalidate) self-clears
            return f"c->spr[1008] = {v} & ~0x800u;"
        if n == 1009:   # HID1: only bits 0-4 writable
            return f"c->spr[1009] = {v} & 0xF8000000u;"
        if n == 920:    # HID2: low half writable except DMAQL
            return f"c->spr[920] = ({v} & 0xF0FF0000u) | (c->spr[920] & 0x0F000000u);"
        return f"c->spr[{n}] = {v};"

    t["mfspr"] = mfspr
    t["mtspr"] = mtspr
    t["mftb"] = lambda i: f"{G(i.rd)} = guest_mftb(c, {int(i.spr == 269)});"
    t["mfmsr"] = lambda i: f"{G(i.rd)} = c->msr;"
    t["mtmsr"] = lambda i: f"c->msr = {s(i)};"
    t["mfsr"] = lambda i: f"{G(i.rd)} = c->sr[{i.sr}];"
    t["mtsr"] = lambda i: f"c->sr[{i.sr}] = {s(i)};"
    # integer loads/stores
    loads = {"lbz": ("ld8", ""), "lhz": ("ld16", ""), "lha": ("ld16", "(uint32_t)(int32_t)(int16_t)"),
             "lwz": ("ld32", "")}
    for base, (fn, cast) in loads.items():
        for suf, ea, upd in (("", _ea_d, False), ("u", _ea_du, True), ("x", _ea_x, False), ("ux", _ea_xu, True)):
            t[base + suf] = (lambda fn, cast, ea, upd: lambda i: (
                f"{{ uint32_t ea_ = {ea(i)}; {G(i.rd)} = {cast}{fn}(c, ea_);"
                + (f" {G(i.ra)} = ea_;" if upd else "") + " }"))(fn, cast, ea, upd)
    stores = {"stb": "st8", "sth": "st16", "stw": "st32"}
    for base, fn in stores.items():
        for suf, ea, upd in (("", _ea_d, False), ("u", _ea_du, True), ("x", _ea_x, False), ("ux", _ea_xu, True)):
            t[base + suf] = (lambda fn, ea, upd: lambda i: (
                f"{{ uint32_t ea_ = {ea(i)}; {fn}(c, ea_, {G(i.rs)});"
                + (f" {G(i.ra)} = ea_;" if upd else "") + " }"))(fn, ea, upd)
    t["lwbrx"] = lambda i: f"{G(i.rd)} = __builtin_bswap32(ld32(c, {_ea_x(i)}));"
    t["lhbrx"] = lambda i: f"{G(i.rd)} = __builtin_bswap16((uint16_t)ld16(c, {_ea_x(i)}));"
    t["stwbrx"] = lambda i: f"st32(c, {_ea_x(i)}, __builtin_bswap32({G(i.rs)}));"
    t["sthbrx"] = lambda i: f"st16(c, {_ea_x(i)}, __builtin_bswap16((uint16_t){G(i.rs)}));"
    t["lmw"] = lambda i: ("{ uint32_t ea_ = " + _ea_d(i) + "; if (ea_ & 3u) guest_fault(c, ea_, 4, 0); "
                          + " ".join(f"{G(r)} = ld32(c, ea_ + {4 * (r - i.rd)}u);" for r in range(i.rd, 32)) + " }")
    t["stmw"] = lambda i: ("{ uint32_t ea_ = " + _ea_d(i) + "; if (ea_ & 3u) guest_fault(c, ea_, 4, 1); "
                           + " ".join(f"st32(c, ea_ + {4 * (r - i.rs)}u, {G(r)});" for r in range(i.rs, 32)) + " }")
    t["dcbz"] = lambda i: f"memset(guest_ptr(c, ({_ea_x(i)}) & ~31u, 32, 1), 0, 32);"
    return t


def F(n): return f"c->fpr[{n}]"


def _fp_ops():
    t = {}
    A0, B0, C0 = (lambda i: f"{F(i.ra)}.ps0"), (lambda i: f"{F(i.rb)}.ps0"), (lambda i: f"{F(i.rc)}.ps0")
    for n in ("fadd", "fsub", "fdiv"):
        t[n] = (lambda n: lambda i: f"{F(i.rd)}.ps0 = gekko_{n}(c->fpscr, {A0(i)}, {B0(i)});")(n)
        t[n + "s"] = (lambda n: lambda i: f"{F(i.rd)} = gekko_{n}s(c->fpscr, {A0(i)}, {B0(i)});")(n)
    t["fmul"] = lambda i: f"{F(i.rd)}.ps0 = gekko_fmul(c->fpscr, {A0(i)}, {C0(i)});"
    t["fmuls"] = lambda i: f"{F(i.rd)} = gekko_fmuls(c->fpscr, {A0(i)}, {C0(i)});"
    for n in ("fmadd", "fmsub", "fnmadd", "fnmsub"):
        t[n] = (lambda n: lambda i: f"{F(i.rd)}.ps0 = gekko_{n}(c->fpscr, {A0(i)}, {C0(i)}, {B0(i)});")(n)
        t[n + "s"] = (lambda n: lambda i: f"{F(i.rd)} = gekko_{n}s(c->fpscr, {A0(i)}, {C0(i)}, {B0(i)});")(n)
    t["frsp"] = lambda i: f"{F(i.rd)} = gekko_frsp(c->fpscr, {B0(i)});"
    t["fres"] = lambda i: f"{F(i.rd)} = gekko_fres({B0(i)});"
    t["frsqrte"] = lambda i: f"{F(i.rd)}.ps0 = gekko_frsqrte({B0(i)});"
    t["fctiw"] = lambda i: f"{F(i.rd)}.ps0 = u2f(gekko_fctiw({B0(i)}));"
    t["fctiwz"] = lambda i: f"{F(i.rd)}.ps0 = u2f(gekko_fctiwz({B0(i)}));"
    t["fsel"] = lambda i: f"{F(i.rd)}.ps0 = gekko_fsel({A0(i)}, {B0(i)}, {C0(i)});"
    for n in ("fneg", "fabs", "fnabs", "fmr"):
        t[n] = (lambda n: lambda i: f"{F(i.rd)}.ps0 = gekko_{n}({B0(i)});")(n)
    t["fcmpu"] = t["fcmpo"] = lambda i: f"cr_set(c, {i.crfd}, fcmp4({A0(i)}, {B0(i)}));"
    t["mffs"] = lambda i: f"{F(i.rd)}.ps0 = u2f(0xFFF8000000000000ull | c->fpscr);"
    t["mtfsf"] = lambda i: (f"{{ uint32_t m_ = {_h(_nibbles(i.fm))}; c->fpscr = (c->fpscr & ~m_) | "
                            f"((uint32_t)f2u({B0(i)}) & m_); guest_fpscr_changed(c); }}")
    # paired singles: whole registers
    A, B, C = (lambda i: F(i.ra)), (lambda i: F(i.rb)), (lambda i: F(i.rc))
    for n in ("add", "sub", "div"):
        t["ps_" + n] = (lambda n: lambda i: f"{F(i.rd)} = gekko_ps_{n}(c->fpscr, {A(i)}, {B(i)});")(n)
    for n in ("mul", "muls0", "muls1"):
        t["ps_" + n] = (lambda n: lambda i: f"{F(i.rd)} = gekko_ps_{n}(c->fpscr, {A(i)}, {C(i)});")(n)
    for n in ("madd", "msub", "nmadd", "nmsub", "madds0", "madds1", "sum0", "sum1"):
        t["ps_" + n] = (lambda n: lambda i: f"{F(i.rd)} = gekko_ps_{n}(c->fpscr, {A(i)}, {C(i)}, {B(i)});")(n)
    for n in ("merge00", "merge01", "merge10", "merge11"):
        t["ps_" + n] = (lambda n: lambda i: f"{F(i.rd)} = gekko_ps_{n}({A(i)}, {B(i)});")(n)
    for n in ("neg", "abs", "nabs", "mr", "res"):
        t["ps_" + n] = (lambda n: lambda i: f"{F(i.rd)} = gekko_ps_{n}({B(i)});")(n)
    t["ps_rsqrte"] = lambda i: f"{F(i.rd)} = gekko_ps_rsqrte(c->fpscr, {B(i)});"
    t["ps_sel"] = lambda i: f"{F(i.rd)} = gekko_ps_sel({A(i)}, {B(i)}, {C(i)});"
    t["ps_cmpu0"] = t["ps_cmpo0"] = lambda i: f"cr_set(c, {i.crfd}, fcmp4({F(i.ra)}.ps0, {F(i.rb)}.ps0));"
    t["ps_cmpu1"] = t["ps_cmpo1"] = lambda i: f"cr_set(c, {i.crfd}, fcmp4({F(i.ra)}.ps1, {F(i.rb)}.ps1));"
    # FP loads/stores
    forms = (("", _ea_d, False), ("u", _ea_du, True), ("x", _ea_x, False), ("ux", _ea_xu, True))
    for suf, ea, upd in forms:
        u = (lambda upd: (lambda i: f" {G(i.ra)} = ea_;" if upd else ""))(upd)
        t["lfs" + suf] = (lambda ea, u: lambda i: (
            f"{{ uint32_t ea_ = {ea(i)}; {F(i.rd)}.ps0 = {F(i.rd)}.ps1 = u2f(gekko_convert_to_double(ld32(c, ea_)));{u(i)} }}"))(ea, u)
        t["lfd" + suf] = (lambda ea, u: lambda i: (
            f"{{ uint32_t ea_ = {ea(i)}; {F(i.rd)}.ps0 = u2f(ld64(c, ea_));{u(i)} }}"))(ea, u)
        t["stfs" + suf] = (lambda ea, u: lambda i: (
            f"{{ uint32_t ea_ = {ea(i)}; st32(c, ea_, gekko_convert_to_single(f2u({F(i.rs)}.ps0)));{u(i)} }}"))(ea, u)
        t["stfd" + suf] = (lambda ea, u: lambda i: (
            f"{{ uint32_t ea_ = {ea(i)}; st64(c, ea_, f2u({F(i.rs)}.ps0));{u(i)} }}"))(ea, u)
    t["stfiwx"] = lambda i: f"st32(c, {_ea_x(i)}, (uint32_t)f2u({F(i.rs)}.ps0));"
    ea_q = lambda i: f"{_ra0(i)} + (uint32_t){i.psq_d}"
    ea_qu = lambda i: f"{G(i.ra)} + (uint32_t){i.psq_d}"
    for name, ea, upd in (("psq_l", ea_q, False), ("psq_lu", ea_qu, True)):
        t[name] = (lambda ea, upd: lambda i: (
            f"{{ uint32_t ea_ = {ea(i)}, g_ = c->spr[{912 + i.psq_i}]; "
            f"{F(i.rd)} = psq_ld(c, ea_, (g_ >> 16) & 7u, (g_ >> 24) & 63u, {i.psq_w});"
            + (f" {G(i.ra)} = ea_;" if upd else "") + " }"))(ea, upd)
    for name, ea, upd in (("psq_st", ea_q, False), ("psq_stu", ea_qu, True)):
        t[name] = (lambda ea, upd: lambda i: (
            f"{{ uint32_t ea_ = {ea(i)}, g_ = c->spr[{912 + i.psq_i}]; "
            f"psq_st(c, ea_, {F(i.rs)}, g_ & 7u, (g_ >> 8) & 63u, {i.psq_w});"
            + (f" {G(i.ra)} = ea_;" if upd else "") + " }"))(ea, upd)
    return t


OPS = {**_int_ops(), **_fp_ops()}
FUZZ_EXCLUDE = {"mftb", "mtmsr"} | TRAP_OPS   # plus mfspr/mtspr of HOOKED_SPR, filtered by word


def emit_insn(i: Insn, pc: int) -> str:
    if i.op in NOP_OPS:
        return f"/* {i.op} */"
    if i.op in TRAP_OPS:
        return f"guest_trap(c, 0x{pc:08X}u, 0x{i.word:08X}u);"
    dot = i.op.endswith(".") and i.op not in ("andi.", "andis.", "addic.")
    base = i.op[:-1] if dot else i.op
    if base not in OPS:
        raise NotImplementedError(i.op)
    out = OPS[base](i)
    if dot:
        out += f" CR0({G(i.ra) if base in RA_DEST else G(i.rd)});"
    return out
