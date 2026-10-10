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
