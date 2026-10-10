import argparse
from pathlib import Path

from ..config import load_dev_config
from .dol import load_main_dol
from .symbols import functions, parse_symbols


def _inputs(config_path):
    cfg = load_dev_config(Path(config_path))
    if cfg.decomp_dir is None:
        raise SystemExit("dev.toml: decomp_dir is required")
    dol = load_main_dol(cfg.iso)
    fns = functions(parse_symbols((cfg.decomp_dir / "config/GALE01/symbols.txt").read_text()))
    return dol, fns


def insn_tests_main(argv=None):
    from .insn_tests import write_cases
    ap = argparse.ArgumentParser(description="Emit per-instruction differential fuzz cases")
    ap.add_argument("--config", default="dev.toml")
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--per-op", type=int, default=4000)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--ops", default="")
    a = ap.parse_args(argv)
    dol, fns = _inputs(a.config)
    summary = write_cases(dol, fns, a.out, a.per_op, a.seed, set(filter(None, a.ops.split(","))) or None)
    print(summary)


def recomp_main(argv=None):
    from .dol import WrongDol
    from .gen import generate
    ap = argparse.ArgumentParser(description="Recompile main.dol into C")
    ap.add_argument("--config", default="dev.toml")
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--hle", type=Path, help="file listing guest functions replaced by host hle_<name>")
    a = ap.parse_args(argv)
    hle_names = a.hle.read_text().split() if a.hle else []
    try:
        dol, fns = _inputs(a.config)
    except WrongDol as e:
        raise SystemExit(f"error: {e}")
    rep = generate(dol, fns, a.out, hle_names=hle_names)
    print(rep.summary())
    if rep.unresolved:
        raise SystemExit(f"error: {len(rep.unresolved)} unresolved branches, see {a.out / 'report.txt'}")
