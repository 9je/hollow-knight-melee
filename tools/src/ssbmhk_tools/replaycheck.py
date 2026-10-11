"""Whole-match replay check (spec §9.1): run a .slp's inputs through libmelee_core and compare every
fighter's state on every frame with what Slippi recorded. Any difference is a bug."""
import argparse
import struct
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

from .config import load_dev_config
from .slp.reader import Replay, read_slp

MAGIC = b"MCRUN2\0\0"
SCENE_ONLINE = 8   # Game Start major scene of a Slippi online match
ONLINE_RNG = 1     # run flag: MC_ONLINE_RNG
RELOAD_COLLISION = 2   # run flag: reload the stage's own collision through mc_load_collision (room API oracle)
REPO = Path(__file__).resolve().parents[3]
CORE_BUILD = REPO / "core" / "build"
# float columns printed by core/tools/replay_check, named like Slippi's post-frame keys; compared bit for bit
FIELDS = ("x", "y", "facing", "percent", "shield", "self_air_x", "self_y", "kb_x", "kb_y", "self_ground_x")


class ReplayUnsupported(ValueError):
    pass


@dataclass(frozen=True)
class CheckResult:
    frames: int
    values: int
    mismatch: tuple | None   # (frame, port, follower, field, ours, slippi)


def _trigger(v: float) -> int:
    return max(0, min(255, round(v * 140)))


def pack_run(r: Replay, reload_collision: bool = False) -> bytes:
    """Refuses (ReplayUnsupported) replays the core cannot run as they were played: a refusal is a SKIP,
    never a divergence."""
    frames = sorted(r.frames)
    if not frames:
        raise ReplayUnsupported("replay has no frames")
    if frames != list(range(frames[0], frames[-1] + 1)):
        raise ReplayUnsupported("frames are not contiguous")
    if frames[0] != -123:
        raise ReplayUnsupported(f"replay starts at frame {frames[0]}, not frame -123")
    if r.start.version < (3, 7, 0):
        raise ReplayUnsupported("recorded by Slippi older than 3.7 (no scene: online RNG sync unknown)")
    if r.start.pal:
        raise ReplayUnsupported("PAL replay (the core is NTSC 1.02)")
    ports = [p.port for p in r.start.players if p.ptype != 3]
    if any(tuple(r.start.ucf[p]) != (1, 1) for p in ports):
        raise ReplayUnsupported("played without UCF 0.84 dashback and shield drop (the core always has UCF)")
    out = bytearray(MAGIC + r.start.game_info)
    flags = (ONLINE_RNG if r.start.major_scene == SCENE_ONLINE else 0) | (RELOAD_COLLISION if reload_collision else 0)
    out += struct.pack(">IiIBB2x", r.start.seed, frames[0], len(frames), sum(1 << p for p in ports), flags)
    for n in frames:
        for port in range(4):
            pre = r.frames[n].pre.get((port, False))
            if port not in ports or pre is None:
                out += bytes(8)
                continue
            v = pre.values
            if "raw_y" not in v or "raw_cx" not in v:
                raise ReplayUnsupported("replay has no raw stick values (recorded by an older Slippi)")
            out += struct.pack(">HbbbbBB", v["phys_buttons"], v["raw_x"], v["raw_y"], v["raw_cx"], v["raw_cy"],
                               _trigger(v["phys_l"]), _trigger(v["phys_r"]))
    return bytes(out)


def parse_states(text: str) -> dict[tuple[int, int, int], dict]:
    out = {}
    for line in text.splitlines():
        t = line.split()
        if len(t) != 15:
            continue
        d = {"action": int(t[3]), "airborne": int(t[14])}
        d.update({k: struct.unpack(">f", bytes.fromhex(h))[0] for k, h in zip(FIELDS, t[4:14])})
        out[(int(t[0]), int(t[1]), int(t[2]))] = d
    return out


def _same(a: float, b: float) -> bool:
    return struct.pack(">f", a) == struct.pack(">f", b)


def _equal(key: str, ours, theirs) -> bool:
    if key == "action":
        return ours == theirs
    if key == "airborne":
        return bool(ours) == bool(theirs)
    return _same(ours, theirs)


def compare(r: Replay, states: dict) -> CheckResult:
    values = 0
    for n in sorted(r.frames):
        for (port, follower), post in sorted(r.frames[n].post.items()):
            ours = states.get((n, port, int(follower)))
            if ours is None:
                return CheckResult(len(r.frames), values, (n, port, int(follower), "missing", None, None))
            for key in ("action", *FIELDS, "airborne"):
                if key not in post.values:
                    continue
                if not _equal(key, ours[key], post.values[key]):
                    return CheckResult(len(r.frames), values, (n, port, int(follower), key, ours[key], post.values[key]))
                values += 1
    return CheckResult(len(r.frames), values, None)


def check_file(path: Path, mem1: Path, iso: Path, exe: Path, reload_collision: bool = False) -> CheckResult:
    r = read_slp(path.read_bytes())
    run = pack_run(r, reload_collision)
    with tempfile.NamedTemporaryFile(suffix=".mcrun") as f:
        f.write(run)
        f.flush()
        p = subprocess.run([str(exe), str(mem1), str(iso), f.name], capture_output=True, text=True)
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or f"replay_check exited {p.returncode}")
    if p.stderr.strip():
        print(p.stderr.strip().splitlines()[-1], file=sys.stderr)
    return compare(r, parse_states(p.stdout))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Replay .slp files through libmelee_core and compare every frame.")
    ap.add_argument("paths", nargs="*", type=Path, help=".slp files or directories (default: replay_dir)")
    ap.add_argument("--config", type=Path, default=REPO / "dev.toml")
    ap.add_argument("--limit", type=int, default=0, help="stop after N replays")
    ap.add_argument("--reload-collision", action="store_true",
                    help="reload each stage's own collision through mc_load_collision first (oracle for the room API)")
    a = ap.parse_args(argv)
    cfg = load_dev_config(a.config)
    exe, mem1 = CORE_BUILD / "replay_check", CORE_BUILD / "gen-core" / "mem1.bin"
    files = []
    for p in a.paths or [cfg.replay_dir]:
        files += sorted(p.rglob("*.slp")) if p.is_dir() else [p]
    if a.limit:
        files = files[:a.limit]
    bad = 0
    for path in files:
        try:
            res = check_file(path, mem1, cfg.iso, exe, a.reload_collision)
        except ReplayUnsupported as e:
            print(f"SKIP {path.name}: {e}")
            continue
        except RuntimeError as e:
            print(f"ERROR {path.name}: {e}")
            bad += 1
            continue
        if res.mismatch:
            bad += 1
            n, port, fo, key, ours, theirs = res.mismatch
            print(f"DIVERGED {path.name}: frame {n} port {port + 1}{' follower' if fo else ''} {key}: ours {ours} slippi {theirs}")
        else:
            print(f"OK {path.name}: {res.frames} frames, {res.values} values")
    return 1 if bad else 0
