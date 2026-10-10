"""Control flow: one C function per guest entry point.

Guest calls become C calls, guest returns become C returns (the returned value is the LR the guest
returned through, checked by the caller), in-function branches become gotos, and bcctr becomes a
switch over every data-section word that points into the function.
"""
import bisect
from dataclasses import dataclass

from .decode import Insn, decode
from .emit import emit_insn


@dataclass(frozen=True)
class Func:
    name: str
    addr: int
    size: int
    words: tuple[int, ...]

    @property
    def end(self) -> int:
        return self.addr + self.size


def c_name(addr: int) -> str:
    return f"fn_{addr:08X}"


def _target(i: Insn, pc: int) -> int:
    off = i.li if i.op == "b" else i.bd
    return (off if i.aa else pc + off) & 0xFFFFFFFF


def _cond(i: Insn) -> tuple[str, str]:
    """(pre-statement, test expression) for a conditional branch; test '' means always."""
    if i.op == "b":
        return "", ""
    bo, bi = i.bo, i.bi
    pre, tests = "", []
    if not bo & 4:
        pre = "CTR -= 1; "
        tests.append("CTR == 0" if bo & 2 else "CTR != 0")
    if not bo & 16:
        tests.append(f"cr_bit(c, {bi}) == {(bo >> 3) & 1}u")
    return pre, " && ".join(tests)


def _guard(pre: str, test: str, action: str) -> str:
    if not test:
        return f"{pre}{{ {action} }}"
    return f"{pre}if ({test}) {{ {action} }}"


class Program:
    def __init__(self, funcs: list[Func], data_words: list[int]):
        self.funcs = sorted(funcs, key=lambda f: f.addr)
        self._starts = [f.addr for f in self.funcs]
        self.entries: set[int] = set(self._starts)
        self.unresolved: dict[int, int] = {}
        self.labels: dict[int, set[int]] = {f.addr: set() for f in self.funcs}
        self.cases: dict[int, list[int]] = {}
        has_bcctr = set()
        for f in self.funcs:
            for k, w in enumerate(f.words):
                pc = f.addr + 4 * k
                i = decode(w)
                if i.op == "bcctr" and not i.lk:
                    has_bcctr.add(f.addr)
                if i.op not in ("b", "bc"):
                    continue
                t = _target(i, pc)
                g = self.func_at(t)
                if g is None:
                    self.unresolved[pc] = t
                    continue
                self.labels[g.addr].add(t)
                if i.lk:
                    if t != pc + 4:
                        self.entries.add(t)
                elif g is not f:
                    self.entries.add(t)
        for w in data_words:
            if w & 3:
                continue
            f = self.func_at(w)
            if f is not None and f.addr in has_bcctr:
                self.cases.setdefault(f.addr, set()).add(w)
        self.cases = {a: sorted(s) for a, s in self.cases.items()}
        for a, ts in self.cases.items():
            self.labels[a].update(ts)
        for e in self.entries:
            self.labels[self.func_at(e).addr].add(e)

    def func_at(self, addr: int) -> Func | None:
        k = bisect.bisect_right(self._starts, addr) - 1
        if k >= 0 and addr < self.funcs[k].end:
            return self.funcs[k]
        return None

    def _call(self, call: str, ra: int) -> str:
        return (f"{{ uint32_t r_ = {call}; if (r_ != 0x{ra:08X}u) return guest_bad_return(c, 0x{ra:08X}u, r_); }}")

    def _branch(self, f: Func, i: Insn, pc: int) -> str:
        ra = pc + 4
        pre, test = _cond(i)
        if i.op in ("b", "bc"):
            t = _target(i, pc)
            g = self.func_at(t)
            if g is None:
                return f"guest_trap(c, 0x{pc:08X}u, 0x{i.word:08X}u);"
            if i.lk:
                if t == ra:
                    return _guard(pre, test, f"LR = 0x{ra:08X}u;")
                return _guard(pre, test, f"LR = 0x{ra:08X}u; " + self._call(f"{c_name(t)}(c)", ra))
            if g is f:
                return _guard(pre, test, f"goto L_{t:08X};")
            return _guard(pre, test, f"return {c_name(t)}(c);")
        if i.op == "bclr":
            if i.lk:
                return _guard(pre, test, f"uint32_t t_ = LR & ~3u; LR = 0x{ra:08X}u; "
                                         + self._call("guest_call_indirect(c, t_)", ra))
            return _guard(pre, test, "return LR;")
        # bcctr
        if i.lk:
            return _guard(pre, test, f"uint32_t t_ = CTR & ~3u; LR = 0x{ra:08X}u; "
                                     + self._call("guest_call_indirect(c, t_)", ra))
        cases = " ".join(f"case 0x{t:08X}u: goto L_{t:08X};" for t in self.cases.get(f.addr, []))
        return _guard(pre, test, f"switch (CTR & ~3u) {{ {cases} default: return guest_call_indirect(c, CTR & ~3u); }}")

    def emit(self, entry: int) -> str:
        f = self.func_at(entry)
        labels = self.labels[f.addr]
        out = [f"uint32_t {c_name(entry)}(Cpu *c) {{  /* {f.name}{'' if entry == f.addr else f' +0x{entry - f.addr:X}'} */"]
        if entry != f.addr:
            out.append(f"  goto L_{entry:08X};")
        for k, w in enumerate(f.words):
            pc = f.addr + 4 * k
            i = decode(w)
            if pc in labels:
                out.append(f"L_{pc:08X}:;")
            body = self._branch(f, i, pc) if i.op in ("b", "bc", "bclr", "bcctr") else emit_insn(i, pc)
            out.append(f"  /* {pc:08X}: {w:08X} {i.op} */ {body}")
        end = f.end
        if end in self.entries:
            out.append(f"  return {c_name(end)}(c);")
        else:
            out.append(f"  guest_trap(c, 0x{end:08X}u, 0);")
        out.append("}")
        return "\n".join(out)
