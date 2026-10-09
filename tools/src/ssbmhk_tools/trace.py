import json
from pathlib import Path
from typing import Iterable

from .watcher import Snapshot


def write_trace(path: Path, header: dict, snapshots: Iterable[Snapshot]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w") as f:
        f.write(json.dumps({"kind": "header", **header}) + "\n")
        for s in snapshots:
            f.write(json.dumps({"kind": "frame", "counter": s.counter,
                                "words": {k: f"{v:08x}" for k, v in s.words.items()}}) + "\n")


def read_trace(path: Path) -> tuple[dict, list[Snapshot]]:
    header, snaps = {}, []
    for line in path.read_text().splitlines():
        rec = json.loads(line)
        if rec.pop("kind") == "header":
            header = rec
        else:
            snaps.append(Snapshot(rec["counter"], {k: int(v, 16) for k, v in rec["words"].items()}))
    return header, snaps
