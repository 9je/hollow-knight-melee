import re, struct, sys, bisect
from pathlib import Path
iso, symbols = Path(sys.argv[1]), Path(sys.argv[2])
funcs = sorted((int(a,16), n) for n, a in re.findall(r"^(\S+) = \.\w+:0x([0-9A-F]+); // type:function", symbols.read_text(), re.M))
addrs = [a for a, _ in funcs]
with iso.open("rb") as f:
    f.seek(0x420); d = struct.unpack(">I", f.read(4))[0]; f.seek(d); h = f.read(0x100)
    o = struct.unpack(">18I", h[:0x48]); a = struct.unpack(">18I", h[0x48:0x90]); s = struct.unpack(">18I", h[0x90:0xD8])
    for i in range(7):
        if not s[i]: continue
        f.seek(d + o[i]); blob = f.read(s[i])
        for k in range(0, len(blob), 4):
            w = struct.unpack(">I", blob[k:k+4])[0]
            p, xo = w >> 26, (w >> 1) & 0x3FF
            name = {583: "mffs", 711: "mtfsf", 38: "mtfsb1", 70: "mtfsb0", 134: "mtfsfi", 64: "mcrfs"}.get(xo) if p == 63 else None
            if name:
                pc = a[i] + k
                print(f"{pc:08X} {name} in {funcs[bisect.bisect_right(addrs, pc)-1][1]}")
