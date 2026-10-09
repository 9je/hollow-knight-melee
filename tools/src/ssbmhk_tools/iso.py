import hashlib
from pathlib import Path

REDUMP_SHA1 = "d4e70c064cc714ba8400a849cf299dbd1aa326fc"
REDUMP_SIZE = 1_459_978_240


class IsoMismatch(Exception):
    pass


def verify_iso(path: Path, expected_sha1: str = REDUMP_SHA1, expected_size: int = REDUMP_SIZE) -> None:
    size = path.stat().st_size
    if size != expected_size:
        raise IsoMismatch(f"{path}: size {size} != expected {expected_size}")
    h = hashlib.sha1()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    if h.hexdigest() != expected_sha1:
        raise IsoMismatch(f"{path}: SHA-1 {h.hexdigest()} != expected {expected_sha1}")
