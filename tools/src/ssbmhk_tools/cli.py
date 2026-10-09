import argparse
import sys
from pathlib import Path

from .config import load_dev_config
from .iso import IsoMismatch, verify_iso


def _config(args) -> "DevConfig":
    return load_dev_config(Path(args.config))


def verify_iso_main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Verify the Melee NTSC 1.02 ISO against Redump.")
    ap.add_argument("--config", default="dev.toml")
    args = ap.parse_args(argv)
    cfg = _config(args)
    try:
        verify_iso(cfg.iso)
    except IsoMismatch as e:
        print(f"ISO NOT OK: {e}", file=sys.stderr)
        return 1
    print(f"ISO OK: {cfg.iso}")
    return 0
