import json
from pathlib import Path

import pytest

from ssbmhk_tools import layout
from ssbmhk_tools.playback import (
    align_offset, playback_command, prepare_user_dir, record_replay, write_comm_file,
)
from ssbmhk_tools.slp.reader import read_slp
from ssbmhk_tools.slp.writer import PostFrame, PreFrame, SlpWriter, make_game_info
from ssbmhk_tools.watcher import Snapshot

ISO = Path("/media/x/Super Smash Bros. Melee (USA) (En,Ja) (v1.02)/Melee (v1.02).iso")


def test_prepare_user_dir(tmp_path):
    prepare_user_dir(tmp_path, ["80453130 2C 10", "80479D60"])
    assert (tmp_path / "MemoryWatcher" / "Locations.txt").read_text() == "80453130 2C 10\n80479D60\n"
    ini = (tmp_path / "Config" / "Dolphin.ini").read_text()
    assert "GFXBackend = Null" in ini
    assert "Backend = No audio output" in ini


def test_comm_file(tmp_path):
    write_comm_file(tmp_path / "comm.json", Path("/r/Game (1).slp"))
    d = json.loads((tmp_path / "comm.json").read_text())
    assert d["mode"] == "normal" and d["replay"] == "/r/Game (1).slp" and d["isRealTimeMode"] is False


def test_command_keeps_paths_with_spaces_as_single_args(tmp_path):
    cmd = playback_command(Path("/a b/pb.AppImage"), tmp_path / "u dir", tmp_path / "c.json", ISO)
    assert cmd[0] == "/a b/pb.AppImage"
    assert cmd[cmd.index("-e") + 1] == str(ISO)
    assert cmd[cmd.index("-u") + 1] == str(tmp_path / "u dir")
    assert "-b" in cmd


def _fake(xs, offset):
    w = SlpWriter(make_game_info(32, [(2, 0, 0)]))
    for i, x in enumerate(xs):
        w.frame(-123 + i, [PreFrame(frame=-123 + i, port=0)], [PostFrame(frame=-123 + i, port=0, x=x, y=0.0)])
    w.end()
    replay = read_slp(w.to_bytes())
    fx = next(f for f in layout.FIGHTER_FIELDS if f.name == "pos.x")
    fy = next(f for f in layout.FIGHTER_FIELDS if f.name == "pos.y")
    import struct
    snaps = [Snapshot(-123 + i + offset, {layout.location(0, fx): struct.unpack(">I", struct.pack(">f", x))[0],
                                          layout.location(0, fy): 0}) for i, x in enumerate(xs)]
    return snaps, replay


def test_align_offset_finds_constant_shift():
    snaps, replay = _fake([1.0, 2.0, 3.0, 4.0, 5.0], offset=1000)
    assert align_offset(snaps, replay, port=0) == 1000


def test_align_offset_fails_when_nothing_matches():
    snaps, replay = _fake([1.0, 2.0, 3.0], offset=0)
    snaps[1].words[next(iter(snaps[1].words))] ^= 1
    with pytest.raises(AssertionError, match="no offset"):
        align_offset(snaps, replay, port=0)


from ssbmhk_tools.slp.reader import read_game_start
from ssbmhk_tools.trace import read_trace


@pytest.mark.oracle
def test_real_replay_positions_match_bit_for_bit(dev_config, tmp_path):
    candidates = sorted(dev_config.replay_dir.rglob("*.slp"), reverse=True)
    replay = next(p for p in candidates
                  if any(s.ext_char == 2 for s in read_game_start(p).players)
                  and read_game_start(p).version >= (3, 5, 0))
    ports = [p.port for p in read_game_start(replay).players]
    out = record_replay(dev_config, replay, tmp_path / "trace.jsonl", ports)
    header, snaps = read_trace(out)
    parsed = read_slp(replay.read_bytes())
    for port in ports:
        assert align_offset(snaps, parsed, port) == header["slp_offset"]
