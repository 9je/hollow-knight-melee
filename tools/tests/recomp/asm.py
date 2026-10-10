"""Tiny PowerPC encoder for tests."""


def _d(op, a, b, imm): return (op << 26) | (a << 21) | (b << 16) | (imm & 0xFFFF)
def _x(xo, d, a, b, rc=0): return (31 << 26) | (d << 21) | (a << 16) | (b << 11) | (xo << 1) | rc
def addi(rd, ra, simm): return _d(14, rd, ra, simm)
def cmpwi(crf, ra, simm): return _d(11, crf << 2, ra, simm)
def lwz(rd, d, ra): return _d(32, rd, ra, d)
def stw(rs, d, ra): return _d(36, rs, ra, d)
def lwzx(rd, ra, rb): return _x(23, rd, ra, rb)
def add(rd, ra, rb): return _x(266, rd, ra, rb)
def slwi(ra, rs, n): return (21 << 26) | (rs << 21) | (ra << 16) | (n << 11) | (0 << 6) | ((31 - n) << 1)
def b(off, lk=0): return (18 << 26) | (off & 0x03FFFFFC) | lk
def bl(off): return b(off, 1)
def bc(bo, bi, off, lk=0): return (16 << 26) | (bo << 21) | (bi << 16) | (off & 0xFFFC) | lk
def blr(): return 0x4E800020
def blrl(): return 0x4E800021
def bctr(): return 0x4E800420
def _spr(xo, r, spr): return (31 << 26) | (r << 21) | ((spr & 31) << 16) | ((spr >> 5) << 11) | (xo << 1)
def mtctr(rs): return _spr(467, rs, 9)
def mtlr(rs): return _spr(467, rs, 8)
def mflr(rd): return _spr(339, rd, 8)
