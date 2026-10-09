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


def pointer_location(port: int) -> str:
    """One-hop location whose value is the port's Fighter pointer (safe even before a match: never dereferences twice)."""
    entity = layout.PLAYER_BLOCK + layout.PLAYER_STRIDE * port + layout.ENTITY_OFF
    return f"{entity:08X} {layout.GOBJ_USER_DATA:X}"


def _is_ram_ptr(v: int) -> bool:
    return 0x80000000 <= v < 0x81800000


def discover_pointers(pairs_iter, ports: list[int], found: dict[int, int]) -> bool:
    """Feed (location, value) pairs; record valid Fighter pointers per port. True once all ports are known."""
    by_loc = {pointer_location(p): p for p in ports}
    for loc, v in pairs_iter:
        port = by_loc.get(loc)
        if port is not None and _is_ram_ptr(v):
            found[port] = v
    return all(p in found for p in ports)


# Trigger word: gm_80479D58.unk_0 (0x80479D58), incremented by gm_801A4D34 once per simulation step,
# right after HSD_GObj_RunProcs (all fighter logic). 0x80479D60 (unk_8) is only bumped conditionally and was
# observed to lag/lead fighter updates. MemoryWatcher keeps locations in a std::map and sends one datagram per
# changed address in *string* order within a sample, so the trigger must sort after every other location:
# use the uncached mirror 0xC0479D58 ("C..." sorts after "80..."/"81...").
TRIGGER_LOC = "C0479D58"


def absolute_map(pointers: dict[int, int]) -> dict[str, str]:
    """Absolute `F+off` address -> logical layout key, for every field of every port."""
    out = {}
    for port, fptr in pointers.items():
        for f in layout.FIGHTER_FIELDS:
            out[f"{fptr + f.offset:08X}"] = layout.location(port, f)
    out[TRIGGER_LOC] = layout.counter_location()
    return out


class PointerChanged(RuntimeError):
    pass


def translate(pairs: list[tuple[str, int]], amap: dict[str, str], pointers: dict[int, int]) -> list[tuple[str, int]]:
    """Rewrite absolute addresses to logical keys; drop pointer locations after checking they are unchanged."""
    ptr_locs = {pointer_location(p): p for p in pointers}
    out = []
    for loc, v in pairs:
        if loc in ptr_locs:
            port = ptr_locs[loc]
            if _is_ram_ptr(v) and v != pointers[port]:
                raise PointerChanged(f"port {port} Fighter pointer changed {pointers[port]:08X} -> {v:08X}")
            continue
        out.append((amap.get(loc, loc), v))
    return out


def _launch(cfg: DevConfig, replay: Path, run_dir: Path, locs: list[str]):
    prepare_user_dir(run_dir / "user", locs)
    write_comm_file(run_dir / "comm.json", replay)
    return Receiver(run_dir / "user" / "MemoryWatcher" / "MemoryWatcher"), \
        playback_command(cfg.playback_appimage, run_dir / "user", run_dir / "comm.json", cfg.iso)


def _stop(proc: subprocess.Popen) -> None:
    proc.terminate()
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait()


def find_fighter_pointers(cfg: DevConfig, replay: Path, run_dir: Path, ports: list[int],
                          timeout_s: float = 300) -> dict[int, int]:
    """Pass 1: run Playback watching only one-hop pointer locations until every port has a Fighter pointer.

    The Playback build's MemoryWatcher does not null-check pointer chains, so two-hop locations crash it
    before a match starts; one-hop locations are safe."""
    locs = [pointer_location(p) for p in ports] + [layout.counter_location()]
    rx, cmd = _launch(cfg, replay, run_dir, locs)
    found: dict[int, int] = {}
    with rx:
        proc = subprocess.Popen(cmd)
        deadline = time.monotonic() + timeout_s
        try:
            while time.monotonic() < deadline:
                data = rx.recv(timeout=5.0)
                if data is None:
                    if proc.poll() is not None:
                        raise RuntimeError(f"Dolphin exited with {proc.returncode} during pointer discovery")
                    continue
                if discover_pointers(parse_datagram(data), ports, found):
                    return found
            raise TimeoutError(f"no Fighter pointers for ports {ports} within {timeout_s}s (found {found})")
        finally:
            _stop(proc)


def record_replay(cfg: DevConfig, replay: Path, out: Path, ports: list[int], timeout_s: float = 600) -> Path:
    parsed = read_slp(replay.read_bytes())
    last_frame = max(parsed.frames)
    base = cfg.work_dir / "playback" / uuid.uuid4().hex[:8]  # short: AF_UNIX paths are limited to ~107 bytes
    pointers = find_fighter_pointers(cfg, replay, base / "p1", ports)
    amap = absolute_map(pointers)
    locs = [pointer_location(p) for p in ports] + list(amap)  # TRIGGER_LOC is last in amap, hence last here
    assert locs[-1] == TRIGGER_LOC and max(locs[:-1]) < TRIGGER_LOC
    logical = layout.locations(ports)
    assembler = FrameAssembler(logical, allow_gaps=True)  # sim steps within one emulated ms collapse; recorded in header
    snaps: list[Snapshot] = []
    rx, cmd = _launch(cfg, replay, base / "p2", locs)
    with rx:
        proc = subprocess.Popen(cmd)
        deadline = time.monotonic() + timeout_s
        offset = None
        started = False
        try:
            while time.monotonic() < deadline:
                data = rx.recv(timeout=5.0)
                if data is None:
                    if proc.poll() is not None:
                        raise RuntimeError(f"Dolphin exited with {proc.returncode}")
                    continue
                pairs = translate(parse_datagram(data), amap, pointers)
                if not started:
                    # The counter holds a stale value (e.g. 1) until the match starts and it restarts at 0;
                    # drop counter reports until that restart.
                    cl = layout.counter_location()
                    zero = [i for i, (k, v) in enumerate(pairs) if k == cl and v == 0]
                    started = bool(zero)
                    pairs = [(k, v) for i, (k, v) in enumerate(pairs)
                             if k != cl or (started and i >= zero[0])]
                snaps += assembler.feed(pairs)
                if offset is None and len(snaps) > 600:
                    offset = align_offset(snaps, parsed, ports[0])
                if offset is not None and snaps[-1].counter >= last_frame + offset:
                    break
            else:
                raise TimeoutError(f"playback of {replay} did not finish in {timeout_s}s")
        finally:
            _stop(proc)
    offset = align_offset(snaps, parsed, ports[0])
    write_trace(out, {"source": "playback", "replay": str(replay), "ports": ports,
                      "slp_offset": offset, "gaps": assembler.gaps, "fields": [f.name for f in layout.FIGHTER_FIELDS]}, snaps)
    return out
