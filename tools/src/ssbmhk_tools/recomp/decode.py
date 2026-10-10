"""Gekko decoder for every instruction form main.dol uses (spikes/dol-census/census.txt).

Words outside that set decode to op "unknown" so they fail loudly downstream.
"""
from dataclasses import dataclass


def _sext(v: int, bits: int) -> int:
    return v - (1 << bits) if v & (1 << (bits - 1)) else v


PRIMARY = {
    7: "mulli", 8: "subfic", 10: "cmpli", 11: "cmpi", 12: "addic", 13: "addic.", 14: "addi", 15: "addis",
    16: "bc", 17: "sc", 18: "b", 20: "rlwimi", 21: "rlwinm", 23: "rlwnm", 24: "ori", 25: "oris",
    26: "xori", 27: "xoris", 28: "andi.", 29: "andis.", 32: "lwz", 33: "lwzu", 34: "lbz", 35: "lbzu",
    36: "stw", 37: "stwu", 38: "stb", 39: "stbu", 40: "lhz", 41: "lhzu", 42: "lha", 43: "lhau",
    44: "sth", 45: "sthu", 46: "lmw", 47: "stmw", 48: "lfs", 49: "lfsu", 50: "lfd", 51: "lfdu",
    52: "stfs", 53: "stfsu", 54: "stfd", 55: "stfdu", 56: "psq_l", 57: "psq_lu", 60: "psq_st", 61: "psq_stu",
}
OP19 = {0: "mcrf", 16: "bclr", 33: "crnor", 50: "rfi", 129: "crandc", 150: "isync", 193: "crxor",
        225: "crnand", 257: "crand", 289: "creqv", 417: "crorc", 449: "cror", 528: "bcctr"}
OP31 = {
    0: "cmp", 4: "tw", 19: "mfcr", 23: "lwzx", 24: "slw", 26: "cntlzw", 28: "and", 32: "cmpl", 54: "dcbst",
    55: "lwzux", 60: "andc", 83: "mfmsr", 86: "dcbf", 87: "lbzx", 119: "lbzux", 124: "nor", 144: "mtcrf",
    146: "mtmsr", 151: "stwx", 183: "stwux", 210: "mtsr", 215: "stbx", 246: "dcbtst", 247: "stbux",
    278: "dcbt", 279: "lhzx", 284: "eqv", 311: "lhzux", 316: "xor", 339: "mfspr", 343: "lhax", 371: "mftb",
    375: "lhaux", 407: "sthx", 412: "orc", 439: "sthux", 444: "or", 467: "mtspr", 470: "dcbi", 476: "nand",
    512: "mcrxr", 534: "lwbrx", 535: "lfsx", 536: "srw", 567: "lfsux", 595: "mfsr", 598: "sync",
    599: "lfdx", 631: "lfdux", 662: "stwbrx", 663: "stfsx", 695: "stfsux", 727: "stfdx", 759: "stfdux",
    790: "lhbrx", 792: "sraw", 824: "srawi", 854: "eieio", 918: "sthbrx", 922: "extsh", 954: "extsb",
    982: "icbi", 983: "stfiwx", 1014: "dcbz",
}
OP31_XO = {8: "subfc", 10: "addc", 11: "mulhwu", 40: "subf", 75: "mulhw", 104: "neg", 136: "subfe",
           138: "adde", 200: "subfze", 202: "addze", 232: "subfme", 234: "addme", 235: "mullw",
           266: "add", 459: "divwu", 491: "divw"}
OP31_RC = {"cmp", "slw", "cntlzw", "and", "andc", "nor", "eqv", "xor", "orc", "or", "nand", "srw",
           "sraw", "srawi", "extsh", "extsb"} | set(OP31_XO.values())
OP31_RC.discard("cmp")
OP59 = {18: "fdivs", 20: "fsubs", 21: "fadds", 24: "fres", 25: "fmuls", 28: "fmsubs", 29: "fmadds",
        30: "fnmsubs", 31: "fnmadds"}
OP63_A = {18: "fdiv", 20: "fsub", 21: "fadd", 23: "fsel", 25: "fmul", 26: "frsqrte", 28: "fmsub",
          29: "fmadd", 30: "fnmsub", 31: "fnmadd"}
OP63_X = {0: "fcmpu", 12: "frsp", 14: "fctiw", 15: "fctiwz", 32: "fcmpo", 38: "mtfsb1", 40: "fneg",
          64: "mcrfs", 70: "mtfsb0", 72: "fmr", 134: "mtfsfi", 136: "fnabs", 264: "fabs", 583: "mffs",
          711: "mtfsf"}
OP4_A = {10: "ps_sum0", 11: "ps_sum1", 12: "ps_muls0", 13: "ps_muls1", 14: "ps_madds0", 15: "ps_madds1",
         18: "ps_div", 20: "ps_sub", 21: "ps_add", 23: "ps_sel", 24: "ps_res", 25: "ps_mul",
         26: "ps_rsqrte", 28: "ps_msub", 29: "ps_madd", 30: "ps_nmsub", 31: "ps_nmadd"}
OP4_X = {0: "ps_cmpu0", 32: "ps_cmpo0", 64: "ps_cmpu1", 96: "ps_cmpo1", 40: "ps_neg", 72: "ps_mr",
         136: "ps_nabs", 264: "ps_abs", 528: "ps_merge00", 560: "ps_merge01", 592: "ps_merge10",
         624: "ps_merge11", 1014: "dcbz_l"}
ROT_RC = {"rlwimi", "rlwinm", "rlwnm"}

BRANCH_OPS = {"b", "bc", "bclr", "bcctr"}
TRAP_OPS = {"rfi", "tw", "mtfsb0", "mtfsb1", "mtfsfi", "mcrfs", "dcbz_l", "unknown"}
NOP_OPS = {"sync", "isync", "eieio", "dcbf", "dcbst", "dcbt", "dcbtst", "dcbi", "icbi"}


@dataclass(frozen=True)
class Insn:
    op: str
    word: int

    rd = property(lambda s: (s.word >> 21) & 31)
    rs = rd
    ra = property(lambda s: (s.word >> 16) & 31)
    rb = property(lambda s: (s.word >> 11) & 31)
    rc = property(lambda s: (s.word >> 6) & 31)
    simm = property(lambda s: _sext(s.word & 0xFFFF, 16))
    uimm = property(lambda s: s.word & 0xFFFF)
    sh = rb
    mb = property(lambda s: (s.word >> 6) & 31)
    me = property(lambda s: (s.word >> 1) & 31)
    crfd = property(lambda s: (s.word >> 23) & 7)
    crfs = property(lambda s: (s.word >> 18) & 7)
    bo = rd
    bi = ra
    bd = property(lambda s: _sext(s.word & 0xFFFC, 16))
    li = property(lambda s: _sext(s.word & 0x03FFFFFC, 26))
    aa = property(lambda s: (s.word >> 1) & 1)
    lk = property(lambda s: s.word & 1)
    spr = property(lambda s: ((s.word >> 16) & 31) | (((s.word >> 11) & 31) << 5))
    sr = property(lambda s: (s.word >> 16) & 15)
    crm = property(lambda s: (s.word >> 12) & 0xFF)
    fm = property(lambda s: (s.word >> 17) & 0xFF)
    psq_w = property(lambda s: (s.word >> 15) & 1)
    psq_i = property(lambda s: (s.word >> 12) & 7)
    psq_d = property(lambda s: _sext(s.word & 0xFFF, 12))


def _name(word: int) -> str:
    p = word >> 26
    rc = word & 1
    if p in PRIMARY:
        n = PRIMARY[p]
        if n in ROT_RC and rc:
            return n + "."
        return n
    if p == 19:
        n = OP19.get((word >> 1) & 0x3FF)
        return n or "unknown"
    if p == 31:
        xo10 = (word >> 1) & 0x3FF
        n = OP31.get(xo10)
        if n is None:
            n = OP31_XO.get(xo10 & 0x1FF)
            if n is None or (word >> 10) & 1:
                return "unknown"
        if rc:
            return n + "." if n in OP31_RC else "unknown"
        return n
    if p == 59:
        n = OP59.get((word >> 1) & 31)
        return "unknown" if n is None or rc else n
    if p == 63:
        xo5 = (word >> 1) & 31
        n = OP63_A.get(xo5) if xo5 >= 18 else OP63_X.get((word >> 1) & 0x3FF)
        return "unknown" if n is None or rc else n
    if p == 4:
        n = OP4_X.get((word >> 1) & 0x3FF) or OP4_A.get((word >> 1) & 31)
        return "unknown" if n is None or rc else n
    return "unknown"


def decode(word: int) -> Insn:
    return Insn(_name(word), word)
