"""Build libmelee_core on the player's PC from their own Melee ISO (nothing of Melee is distributed).

Steps (each skipped when its output is current, keyed by its inputs):
  1. verify the ISO (NTSC 1.02, Redump);
  2. fetch the two pinned inputs the recompiler needs: the Melee decomp's function list (symbols.txt) and Slippi's Gecko
     codes (GALE01r2.ini), both SHA-256 checked;
  3. recompile main.dol into C (with the core's HLE, tap and Gecko lists) and mem1.bin;
  4. compile it and the runtime with a C compiler (zig cc for Windows; gcc or zig on Linux) in parallel, and link
     libmelee_core.dll / libmelee_core.so;
  5. extract fox.model, the sound banks, the stock icons and the character select (css.bin).

Only the standard library is used, so an embeddable Python runs it. usage:
  ssbmhk-build-core <iso> --core <repo core dir> --out <dir> [--cc "zig cc"] [--target windows|linux] [--jobs N]
"""
import argparse
import hashlib
import os
import re
import shlex
import shutil
import subprocess
import sys
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

DECOMP_COMMIT = "79b13a32467f7c4f91d6f831f0b9f891b7fb1146"
SYMBOLS_URL = f"https://raw.githubusercontent.com/doldecomp/melee/{DECOMP_COMMIT}/config/GALE01/symbols.txt"
SYMBOLS_SHA256 = "4e9e5a4f65c32d6bd914cf441723bd558b2ee97ec1d734f2410482caaaf2f8a8"
GECKO_URL = ("https://raw.githubusercontent.com/project-slippi/Ishiiruka/60f7b63496fb6ec7b9180a04f16f3edc0ad89fe2/"
             "Data/Sys/GameSettings/GALE01r2.ini")
GECKO_SHA256 = "b30b294df5c0d92deb3129afdfbc894a48aabeb1bce0cf92cce8c4e5408a2587"
# core/Makefile's GEN_CFLAGS (Melee-exact floating point depends on them)
CFLAGS = ["-O2", "-std=c11", "-ffp-contract=off", "-fno-fast-math", "-fwrapv", "-fno-strict-aliasing", "-w"]


def _log(msg: str) -> None:
    print(msg, flush=True)


def fetch(url: str, sha256: str, dest: Path) -> Path:
    """dest, downloaded once and checked against sha256."""
    if dest.exists() and hashlib.sha256(dest.read_bytes()).hexdigest() == sha256:
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url, timeout=60) as r:
        data = r.read()
    got = hashlib.sha256(data).hexdigest()
    if got != sha256:
        raise SystemExit(f"{url}: SHA-256 {got}, expected {sha256}")
    dest.write_bytes(data)
    return dest


def runtime_sources(core: Path) -> list[str]:
    """RUNTIME_SRCS from core/Makefile, so this build and the Makefile's can't drift apart."""
    m = re.search(r"^RUNTIME_SRCS := (.+)$", (core / "Makefile").read_text(), re.M)
    if not m:
        raise SystemExit(f"{core}/Makefile: no RUNTIME_SRCS")
    return m.group(1).split()


def sources(core: Path, gen: Path, target: str) -> list[tuple[Path, list[str]]]:
    """(source, extra include dirs) for every object of libmelee_core."""
    out = [(p, [gen]) for p in sorted(gen.glob("shard_*.c"))] + [(gen / "guest_table.c", [gen])]
    out += [(core / "runtime" / f"{s}.c", [gen]) for s in runtime_sources(core)]
    out += [(core / "runtime/dispatch.c", [gen]), (core / "gekko/gekko_fp.c", [])]
    if target == "windows":
        out.append((core / "gekko/soft_fma.c", []))
    return out


def headers_key(core: Path, gen: Path) -> str:
    """Every header an object may include (the runtime's, the FP emulation's, the generated ones): a change to any of
    them rebuilds every object, as a struct layout change must."""
    hs = sorted(core.glob("runtime/*.h")) + sorted(core.glob("gekko/*.h")) + sorted(gen.glob("*.h"))
    return tree_hash(hs)


def tree_hash(paths) -> str:
    h = hashlib.sha256()
    for p in sorted(paths):
        h.update(str(p.name).encode())
        h.update(p.read_bytes())
    return h.hexdigest()


def generate(iso: Path, core: Path, cache: Path) -> Path:
    """The recompiled C and mem1.bin in cache/gen (skipped when its inputs are unchanged)."""
    gen = cache / "gen"
    inputs = [core / "runtime/hle.txt", core / "runtime/taps.txt", core / "runtime/slippi_gameplay.txt",
              core / "runtime/online_rng.gecko"] + sorted((core.parent / "tools/src/ssbmhk_tools/recomp").glob("*.py"))
    key = tree_hash(inputs) + SYMBOLS_SHA256 + GECKO_SHA256 + str(iso.stat().st_size)
    stamp = gen / ".key"
    if stamp.exists() and stamp.read_text() == key and (gen / "mem1.bin").exists():
        _log("recompiled code: current")
        return gen
    decomp = cache / "decomp"
    fetch(SYMBOLS_URL, SYMBOLS_SHA256, decomp / "config/GALE01/symbols.txt")
    ini = fetch(GECKO_URL, GECKO_SHA256, cache / "GALE01r2.ini")
    codes = cache / "slippi_gameplay.gecko"
    from .recomp.gecko import apply_codes, select_ini   # as `python -m ssbmhk_tools.recomp.gecko` (fetch_gecko.sh)
    names = {ln.strip() for ln in (core / "runtime/slippi_gameplay.txt").read_text().splitlines() if ln.strip() and not ln.startswith("#")}
    text = select_ini(ini.read_text(), ("$Required:", "$Recommended:"), names)
    apply_codes(text)
    codes.write_text(text)
    gecko = cache / "core.gecko"
    gecko.write_text(codes.read_text() + (core / "runtime/online_rng.gecko").read_text())
    cfg = cache / "build.toml"
    q = lambda p: '"' + str(p).replace("\\", "/") + '"'
    cfg.write_text(encoding="utf-8", data=f"iso = {q(iso)}\ndecomp_dir = {q(decomp)}\n"   # the rest of DevConfig is unused here
                   f"playback_appimage = {q(cache)}\nnetplay_appimage = {q(cache)}\nreplay_dir = {q(cache)}\nwork_dir = {q(cache)}\n")
    if gen.exists():
        shutil.rmtree(gen)
    _log("recompiling Melee (about a minute)...")
    from .recomp.cli import recomp_main
    recomp_main(["--config", str(cfg), "--out", str(gen), "--hle", str(core / "runtime/hle.txt"),
                 "--gecko", str(gecko), "--tap", str(core / "runtime/taps.txt")])
    stamp.write_text(key)
    return gen


def compile_core(core: Path, gen: Path, cache: Path, cc: list[str], target: str, jobs: int) -> Path:
    """libmelee_core.dll (windows) or .so (linux) in cache/<target>, compiled in parallel."""
    objdir = cache / target
    objdir.mkdir(parents=True, exist_ok=True)
    srcs = sources(core, gen, target)
    pic = [] if target == "windows" else ["-fPIC"]
    flags_key = " ".join(cc + CFLAGS + pic) + headers_key(core, gen)
    todo = []
    for src, incs in srcs:
        obj = objdir / (src.parent.name + "_" + src.stem + ".o")
        key = obj.with_suffix(".key")
        h = hashlib.sha256((flags_key + src.read_text()).encode()).hexdigest()
        if not (obj.exists() and key.exists() and key.read_text() == h):
            todo.append((src, incs, obj, key, h))
    done = [0]

    def build(job):
        src, incs, obj, key, h = job
        cmd = cc + CFLAGS + pic + ["-I", str(core / "gekko"), "-I", str(core / "runtime")]
        for i in incs:
            cmd += ["-I", str(i)]
        cmd += ["-c", str(src), "-o", str(obj)]
        p = subprocess.run(cmd, capture_output=True, text=True)
        if p.returncode != 0:
            raise RuntimeError(f"{src.name}: {p.stderr.strip()[-2000:]}")
        key.write_text(h)
        done[0] += 1
        _log(f"compiled {done[0]}/{len(todo)}: {src.name}")

    if todo:
        _log(f"compiling {len(todo)} files with {jobs} jobs (several minutes the first time)...")
        with ThreadPoolExecutor(jobs) as ex:
            list(ex.map(build, todo))
    objs = [str(objdir / (s.parent.name + "_" + s.stem + ".o")) for s, _ in srcs]
    if target == "windows":
        out = objdir / "libmelee_core.dll"
        cmd = cc + ["-shared", "-o", str(out)] + objs
    else:
        out = objdir / "libmelee_core.so"
        cmd = cc + ["-shared", "-o", str(out), f"-Wl,--version-script={core / 'runtime/exports.map'}",
                    "-Wl,-soname,libmelee_core.so"] + objs + ["-lm", "-lpthread"]
    _log("linking " + out.name)
    subprocess.run(cmd, check=True)
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Build libmelee_core, mem1.bin, fox.model and the sounds from your Melee ISO.")
    ap.add_argument("iso", type=Path)
    ap.add_argument("--core", type=Path, required=True, help="the repo's core directory (sources)")
    ap.add_argument("--out", type=Path, required=True, help="where the built files go")
    ap.add_argument("--cache", type=Path, help="work directory kept between builds (default: <out>/.build)")
    ap.add_argument("--cc", default="", help='C compiler command, e.g. "zig cc -target x86_64-windows-gnu" (default: gcc on Linux)')
    ap.add_argument("--target", choices=["windows", "linux"], default="windows" if os.name == "nt" else "linux")
    ap.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    a = ap.parse_args(argv)
    from . import css, foxmodel, gciso, hsd, iso as isomod, meleesfx, stocks
    _log("checking the ISO...")
    isomod.verify_iso(a.iso)
    core = a.core.resolve()
    cache = (a.cache or a.out / ".build").resolve()
    cache.mkdir(parents=True, exist_ok=True)
    cc = shlex.split(a.cc) if a.cc else ["gcc"]
    gen = generate(a.iso.resolve(), core, cache)
    lib = compile_core(core, gen, cache, cc, a.target, a.jobs)
    a.out.mkdir(parents=True, exist_ok=True)
    shutil.copy2(lib, a.out / lib.name)
    shutil.copy2(gen / "mem1.bin", a.out / "mem1.bin")
    _log("extracting Fox's model and Melee's sounds...")
    dat = hsd.Dat(gciso.read_file(a.iso, "PlFxNr.dat"))
    foxmodel.write_model(foxmodel.build_model(hsd.read_joints(dat, dat.roots["PlyFox5K_Share_joint"])), a.out / "fox.model")
    meleesfx.main([str(a.iso), "--out", str(a.out)])
    stocks.main([str(a.iso), "--out", str(a.out)])
    css.main([str(a.iso), "--out", str(a.out)])
    _log(f"done: {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
