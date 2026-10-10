import struct

import pytest

from ssbmhk_tools.replaycheck import CORE_BUILD, MAGIC, CheckResult, ReplayUnsupported, check_file, compare, pack_run, parse_states
from ssbmhk_tools.slp.reader import Frame, GameStart, PlayerSlot, Post, Pre, Replay, read_slp

PRE = {"phys_buttons": 0x0100, "raw_x": 100, "raw_y": -40, "raw_cx": 0, "raw_cy": 5, "phys_l": 0.5, "phys_r": 0.0}
POST = {"action": 14, "x": -60.0, "y": 0.0001, "facing": 1.0, "percent": 0.0, "shield": 60.0, "self_air_x": 0.0,
        "self_y": 0.0, "kb_x": 0.0, "kb_y": 0.0, "self_ground_x": 0.0, "airborne": 0}


def replay(frames=(-123, -122), pre=PRE, follower=False, major_scene=2):
    gi = bytes(range(256)) + bytes(0x38)
    start = GameStart(version=(3, 19, 1), game_info=gi, seed=0x1234, stage=32,
                      players=[PlayerSlot(0, 2, 0, 0), PlayerSlot(1, 20, 0, 0), PlayerSlot(2, 0, 3, 0), PlayerSlot(3, 0, 3, 0)],
                      ucf=[(1, 1)] * 4, major_scene=major_scene)
    fr = {}
    for n in frames:
        posts = {(0, False): Post(n, 0, False, dict(POST)), (1, False): Post(n, 1, False, dict(POST))}
        if follower:
            posts[(1, True)] = Post(n, 1, True, dict(POST))
        fr[n] = Frame(number=n, pre={(0, False): Pre(n, 0, False, dict(pre)), (1, False): Pre(n, 1, False, dict(pre))}, post=posts)
    return Replay(start=start, frames=fr, end_method=2)


def test_online_matches_are_flagged():
    flags = pack_run(replay(major_scene=8))[8 + 0x138 + 13]
    assert flags == 1   # MC_ONLINE_RNG: Slippi online re-seeds the RNG every frame


def test_pack_run_layout():
    b = pack_run(replay())
    assert b[:8] == MAGIC and b[8:8 + 0x138] == replay().start.game_info
    assert MAGIC == b"MCRUN2\0\0"
    seed, first, n, mask, flags = struct.unpack_from(">IiIBB", b, 8 + 0x138)
    assert (seed, first, n, mask, flags) == (0x1234, -123, 2, 0b11, 0)
    body = b[8 + 0x138 + 16:]
    assert len(body) == 2 * 4 * 8
    assert struct.unpack_from(">HbbbbBB", body, 0) == (0x0100, 100, -40, 0, 5, 70, 0)   # 0.5 * 140
    assert body[16:32] == bytes(16)                                                    # ports 3-4 absent


@pytest.mark.parametrize("r, why", [
    (replay(pre={k: v for k, v in PRE.items() if k != "raw_y"}), "raw stick"),
    (replay(frames=(-123, -121)), "contiguous"),
    (replay(frames=()), "no frames"),
])
def test_unsupported_replays_are_refused(r, why):
    with pytest.raises(ReplayUnsupported, match=why):
        pack_run(r)


def _line(frame, port, follower, action=14, x=-60.0):
    f = lambda v: struct.pack(">f", v).hex()   # noqa: E731
    vals = [x, 0.0001, 1.0, 0.0, 60.0, 0.0, 0.0, 0.0, 0.0, 0.0]
    return f"{frame} {port} {follower} {action} " + " ".join(f(v) for v in vals) + " 0"


def test_compare_exact_match_and_first_divergence():
    r = replay(follower=True)
    lines = [_line(n, p, fo) for n in (-123, -122) for p, fo in ((0, 0), (1, 0), (1, 1))]
    res = compare(r, parse_states("\n".join(lines)))
    assert res == CheckResult(frames=2, values=2 * 3 * 12, mismatch=None)
    lines[4] = _line(-122, 1, 0, x=-59.5)
    res = compare(r, parse_states("\n".join(lines)))
    assert res.mismatch == (-122, 1, 0, "x", -59.5, -60.0)


def test_missing_fighter_state_is_a_mismatch():
    r = replay(follower=True)
    lines = [_line(n, p, 0) for n in (-123, -122) for p in (0, 1)]   # follower never printed
    assert compare(r, parse_states("\n".join(lines))).mismatch[:4] == (-123, 1, 1, "missing")


def test_real_replay_matches_bit_for_bit(dev_config):
    exe, mem1 = CORE_BUILD / "replay_check", CORE_BUILD / "gen-core" / "mem1.bin"
    if not exe.exists() or not mem1.exists():
        pytest.skip("run `make -C core replay-check-tool` first")
    for path in sorted(dev_config.replay_dir.rglob("*.slp")):
        try:
            pack_run(read_slp(path.read_bytes()))
        except ReplayUnsupported:
            continue
        res = check_file(path, mem1, dev_config.iso, exe)
        assert res.mismatch is None, res.mismatch
        assert res.frames > 1000 and res.values > res.frames
        return
    pytest.skip("no supported replay in replay_dir")
