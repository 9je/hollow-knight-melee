import json
import struct
import subprocess
import time
import uuid
from collections import Counter
from pathlib import Path

from . import layout
from .config import DevConfig
from .slp.reader import Replay, read_slp
from .trace import write_trace
from .watcher import FrameAssembler, Receiver, Snapshot, parse_datagram

DOLPHIN_INI = """[Core]
GFXBackend = Null
EmulationSpeed = 1.0
[DSP]
Backend = No audio output
[Display]
RenderToMain = False
"""


def prepare_user_dir(user_dir: Path, locations: list[str]) -> None:
    (user_dir / "Config").mkdir(parents=True, exist_ok=True)
    (user_dir / "MemoryWatcher").mkdir(parents=True, exist_ok=True)
    (user_dir / "Config" / "Dolphin.ini").write_text(DOLPHIN_INI)
    (user_dir / "MemoryWatcher" / "Locations.txt").write_text("".join(l + "\n" for l in locations))


def write_comm_file(path: Path, replay: Path) -> None:
    path.write_text(json.dumps({"mode": "normal", "replay": str(replay), "isRealTimeMode": False,
                                "commandId": uuid.uuid4().hex}))


def playback_command(appimage: Path, user_dir: Path, comm: Path, iso: Path) -> list[str]:
    return [str(appimage), "-u", str(user_dir), "-i", str(comm), "-e", str(iso), "-b"]


def _post_bits(replay: Replay, port: int) -> dict[int, tuple[int, int]]:
    out = {}
    for n, fr in replay.frames.items():
        p = fr.post.get((port, False))
        if p is not None:
            out[n] = tuple(struct.unpack(">I", struct.pack(">f", v))[0] for v in (p.x, p.y))
    return out


def align_offset(snaps: list[Snapshot], replay: Replay, port: int) -> int:
    fx = next(f for f in layout.FIGHTER_FIELDS if f.name == "pos.x")
    fy = next(f for f in layout.FIGHTER_FIELDS if f.name == "pos.y")
    lx, ly = layout.location(port, fx), layout.location(port, fy)
    ours = {s.counter: (s.words.get(lx), s.words.get(ly)) for s in snaps}
    theirs = _post_bits(replay, port)
    index: dict[tuple, list[int]] = {}
    for n, v in theirs.items():
        index.setdefault(v, []).append(n)
    votes = Counter(c - n for c, v in ours.items() for n in index.get(v, [])[:4])
    need = min(len(theirs), 100)

    def consistent(k: int) -> bool:
        overlap = [n for n in theirs if n + k in ours]
        return len(overlap) >= need and all(ours[n + k] == theirs[n] for n in overlap)

    good = [k for k, _ in votes.most_common(10) if consistent(k)]
    assert len(good) == 1, f"no offset aligns trace with replay positions (consistent offsets: {good})"
    return good[0]


def record_replay(cfg: DevConfig, replay: Path, out: Path, ports: list[int], timeout_s: float = 600) -> Path:
    parsed = read_slp(replay.read_bytes())
    last_frame = max(parsed.frames)
    run_dir = cfg.work_dir / "playback" / uuid.uuid4().hex[:8]  # short: AF_UNIX paths are limited to ~107 bytes
    locs = layout.locations(ports)
    prepare_user_dir(run_dir / "user", locs)
    write_comm_file(run_dir / "comm.json", replay)
    assembler = FrameAssembler(locs)
    snaps: list[Snapshot] = []
    with Receiver(run_dir / "user" / "MemoryWatcher" / "MemoryWatcher") as rx:
        proc = subprocess.Popen(playback_command(cfg.playback_appimage, run_dir / "user",
                                                 run_dir / "comm.json", cfg.iso))
        deadline = time.monotonic() + timeout_s
        offset = None
        try:
            while time.monotonic() < deadline:
                data = rx.recv(timeout=5.0)
                if data is None:
                    if proc.poll() is not None:
                        raise RuntimeError(f"Dolphin exited with {proc.returncode}")
                    continue
                snaps += assembler.feed(parse_datagram(data))
                if offset is None and len(snaps) > 600:
                    offset = align_offset(snaps, parsed, ports[0])
                if offset is not None and snaps[-1].counter >= last_frame + offset:
                    break
            else:
                raise TimeoutError(f"playback of {replay} did not finish in {timeout_s}s")
        finally:
            proc.terminate()
            proc.wait(timeout=30)
    offset = align_offset(snaps, parsed, ports[0])
    write_trace(out, {"source": "playback", "replay": str(replay), "ports": ports,
                      "slp_offset": offset, "fields": [f.name for f in layout.FIGHTER_FIELDS]}, snaps)
    return out
