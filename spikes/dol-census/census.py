"""Feasibility census: can main.dol be statically recompiled?

Extracts main.dol from the ISO, checks function coverage of executable
sections against the decomp's symbols.txt, finds indirect branches and jump
tables, and counts instruction mnemonics (incl. paired singles).
"""
import re
import struct
import sys
from collections import Counter
from pathlib import Path

import capstone

iso, symbols = Path(sys.argv[1]), Path(sys.argv[2])
out = Path(__file__).parent

with iso.open("rb") as f:
    f.seek(0x420)
    dol_off = struct.unpack(">I", f.read(4))[0]
    f.seek(dol_off)
    hdr = f.read(0x100)
    offs = struct.unpack(">18I", hdr[0:0x48])
    addrs = struct.unpack(">18I", hdr[0x48:0x90])
    sizes = struct.unpack(">18I", hdr[0x90:0xD8])
    text = []
    for i in range(7):
        if sizes[i]:
            f.seek(dol_off + offs[i])
            text.append((addrs[i], f.read(sizes[i])))
            print(f"text{i}: {addrs[i]:08X}..{addrs[i]+sizes[i]:08X} ({sizes[i]:#x})")

funcs = []
objs = {}
pat = re.compile(r"^(\S+) = (\.\w+):0x([0-9A-F]+); // type:(\w+)(?: size:0x([0-9A-F]+))?")
for line in symbols.read_text().splitlines():
    m = pat.match(line)
    if not m:
        continue
    name, sec, addr, typ, size = m.groups()
    addr, size = int(addr, 16), int(size or "0", 16)
    if typ == "function":
        funcs.append((addr, size, name))
    elif typ == "object" and name.startswith("jumptable"):
        objs[addr] = size

covered = 0
total = sum(len(b) for _, b in text)
for base, blob in text:
    end = base + len(blob)
    covered += sum(min(a + s, end) - max(a, base) for a, s, _ in funcs if a < end and a + s > base)
print(f"functions: {len(funcs)}  text bytes covered: {covered}/{total} ({100*covered/total:.2f}%)")
print(f"jump tables: {len(objs)}")

md = capstone.Cs(capstone.CS_ARCH_PPC, capstone.CS_MODE_32 | capstone.CS_MODE_BIG_ENDIAN | capstone.CS_MODE_PS)
md.skipdata = True
mn = Counter()
bad = 0
indirect = Counter()
for base, blob in text:
    for ins in md.disasm(blob, base):
        if ins.id == 0:
            bad += 1
            continue
        mn[ins.mnemonic] += 1
        if ins.mnemonic in ("bctr", "bctrl", "blrl"):
            indirect[ins.mnemonic] += 1
print(f"undecodable words: {bad}")
print(f"indirect: {dict(indirect)}")
fp = {k: v for k, v in mn.items() if k.startswith(("f", "ps_", "psq", "mtfs", "mffs"))}
sys_ops = {k: v for k, v in mn.items() if k in ("mtspr", "mfspr", "mtmsr", "mfmsr", "rfi", "sc", "isync", "sync", "icbi", "dcbi", "dcbf", "dcbst", "dcbz", "dcbz_l", "mtsr", "mfsr", "tw", "twi", "eieio", "mftb")}
(out / "census.txt").write_text(
    "\n".join(f"{v:8d} {k}" for k, v in mn.most_common()) + "\n")
print(f"distinct mnemonics: {len(mn)}")
print("fp/ps ops:", dict(sorted(fp.items(), key=lambda kv: -kv[1])))
print("system ops:", sys_ops)

# classify undecodable words by primary opcode / extended opcode
prim = Counter()
for base, blob in text:
    for ins in md.disasm(blob, base):
        if ins.id == 0:
            w = struct.unpack(">I", ins.bytes)[0]
            prim[(w >> 26, (w >> 1) & 0x3FF if w >> 26 in (4, 31, 63, 59) else None)] += 1
print("undecodable by (primary, xo):", prim.most_common(12))
