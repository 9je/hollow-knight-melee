import struct

import pytest

from ssbmhk_tools.slp.events import Event, WRITER_SIZES
from ssbmhk_tools.slp.reader import read_slp
from ssbmhk_tools.slp.writer import PostFrame, PreFrame, SlpWriter, make_game_info


def _replay(frames):
    w = SlpWriter(make_game_info(32, [(2, 0, 3), (20, 1, 0)]), seed=0xDEADBEEF)
    for f, x in frames:
        w.frame(f, [PreFrame(frame=f, port=0, joy_x=0.25)], [PostFrame(frame=f, port=0, x=x, kb_x=-0.0)])
    w.end()
    return w.to_bytes()


def test_game_start_fields():
    r = read_slp(_replay([(-123, 0.0)]))
    assert r.start.version == (3, 5, 0)
    assert r.start.stage == 32
    assert r.start.seed == 0xDEADBEEF
    assert [(p.port, p.ext_char, p.ptype, p.costume) for p in r.start.players] == [(0, 2, 0, 3), (1, 20, 1, 0)]
    assert len(r.start.game_info) == 312


def test_frames_pre_post():
    r = read_slp(_replay([(-123, 1.5), (-122, 2.5)]))
    assert sorted(r.frames) == [-123, -122]
    assert r.frames[-122].post[(0, False)].x == 2.5
    assert r.frames[-122].pre[(0, False)].joy_x == 0.25
    assert struct.pack(">f", r.frames[-122].post[(0, False)].kb_x) == struct.pack(">f", -0.0)
    assert r.end_method == 2


def test_rollback_last_copy_wins():
    r = read_slp(_replay([(-123, 1.0), (-122, 2.0), (-122, 9.0)]))
    assert r.frames[-122].post[(0, False)].x == 9.0


def test_missing_game_end_and_metadata():
    data = _replay([(-123, 1.0)])
    raw_len = struct.unpack(">i", data[11:15])[0]
    raw = data[15:15 + raw_len]
    cut = raw[: raw_len - (1 + WRITER_SIZES[Event.GAME_END])]
    data2 = b"{U\x03raw[$U#l" + struct.pack(">i", len(cut)) + cut + b"}"
    r = read_slp(data2)
    assert r.end_method is None
    assert -123 in r.frames


def test_longer_payloads_from_newer_versions_are_skipped():
    data = bytearray(_replay([(-123, 1.0)]))
    raw = bytearray(data[15:-1])
    n = raw[1]
    table = raw[2:1 + n]
    for i in range(0, len(table), 3):
        if table[i] == Event.POST_FRAME:
            struct.pack_into(">H", table, i + 1, WRITER_SIZES[Event.POST_FRAME] + 8)
    raw[2:1 + n] = table
    out = bytearray()
    pos = 1 + n
    out += raw[:pos]
    while pos < len(raw):
        code = raw[pos]
        size = WRITER_SIZES[Event(code)]
        out += raw[pos:pos + 1 + size]
        if code == Event.POST_FRAME:
            out += b"\xAA" * 8
        pos += 1 + size
    blob = b"{U\x03raw[$U#l" + struct.pack(">i", len(out)) + bytes(out) + b"}"
    r = read_slp(blob)
    assert r.frames[-123].post[(0, False)].x == 1.0
    assert r.end_method == 2


def test_truncated_mid_event_rejected():
    data = _replay([(-123, 1.0)])
    raw = data[15:-1]
    # raw tail: POST_FRAME, FRAME_BOOKEND (5 bytes), GAME_END (3 bytes)
    cut = raw[: len(raw) - 3 - 5 - 10]  # 10 bytes into the last Post Frame
    blob = b"{U\x03raw[$U#l" + struct.pack(">i", len(cut)) + cut + b"}"
    with pytest.raises(ValueError, match="truncated"):
        read_slp(blob)


def test_in_progress_replay_rejected():
    with pytest.raises(ValueError, match="still being written"):
        read_slp(b"{U\x03raw[$U#l" + struct.pack(">i", 0) + b"\x35")


import itertools

from ssbmhk_tools.slp.reader import read_game_start


@pytest.mark.oracle
def test_real_replays_parse(dev_config):
    files = sorted(dev_config.replay_dir.rglob("*.slp"))[-20:]
    assert files, "no replays found"
    for path in files:
        r = read_slp(path.read_bytes())
        assert read_game_start(path).game_info == r.start.game_info
        numbers = sorted(r.frames)
        assert numbers == list(range(numbers[0], numbers[-1] + 1)), path
        for fr in itertools.islice(r.frames.values(), 0, None):
            assert set(fr.pre) == set(fr.post), (path, fr.number)
