import socket

import pytest

from ssbmhk_tools.trace import read_trace, write_trace
from ssbmhk_tools.watcher import FrameAssembler, Receiver, Snapshot, TraceGap, parse_datagram

LOCS = ["80453130 2C 10", "80453130 2C B0", "80479D60"]


def test_parse_datagram_multiple_pairs_and_nul():
    data = b"80453130 2C 10\n14\n80479D60\nff\x00"
    assert parse_datagram(data) == [("80453130 2C 10", 0x14), ("80479D60", 0xFF)]


def test_assembler_emits_on_counter_and_carries_unchanged_values():
    a = FrameAssembler(LOCS)
    out = a.feed([("80453130 2C 10", 1), ("80453130 2C B0", 7), ("80479D60", 100)])
    out += a.feed([("80453130 2C 10", 2), ("80479D60", 101)])
    assert out == [
        Snapshot(100, {"80453130 2C 10": 1, "80453130 2C B0": 7}),
        Snapshot(101, {"80453130 2C 10": 2, "80453130 2C B0": 7}),
    ]


def test_assembler_rejects_gap():
    a = FrameAssembler(LOCS)
    a.feed([("80479D60", 100)])
    with pytest.raises(TraceGap, match="101"):
        a.feed([("80479D60", 103)])


def test_assembler_allow_gaps_records_them():
    a = FrameAssembler(LOCS, allow_gaps=True)
    a.feed([("80479D60", 100)])
    a.feed([("80479D60", 102)])
    assert a.gaps == [101]


def test_assembler_rejects_unknown_location():
    a = FrameAssembler(LOCS)
    with pytest.raises(ValueError, match="unknown"):
        a.feed([("DEADBEEF", 1)])


def test_receiver_roundtrip(tmp_path):
    path = tmp_path / "MemoryWatcher"
    with Receiver(path) as r:
        s = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        s.sendto(b"80479D60\n5\n", str(path))
        assert r.recv(timeout=1.0) == b"80479D60\n5\n"
        assert r.recv(timeout=0.05) is None


def test_trace_roundtrip(tmp_path):
    snaps = [Snapshot(1, {"a": 0x80000000}), Snapshot(2, {"a": 0x7FC00001})]
    write_trace(tmp_path / "t.jsonl", {"source": "test"}, snaps)
    header, back = read_trace(tmp_path / "t.jsonl")
    assert header["source"] == "test"
    assert back == snaps
