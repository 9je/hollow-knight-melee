import struct

from ssbmhk_tools.slp.events import Event, WRITER_SIZES
from ssbmhk_tools.slp.writer import PostFrame, PreFrame, SlpWriter, make_game_info


def _raw(data: bytes) -> bytes:
    assert data.startswith(b"{U\x03raw[$U#l")
    (n,) = struct.unpack(">i", data[11:15])
    assert data[15 + n:] == b"}"
    return data[15:15 + n]


def test_game_info_layout():
    gi = make_game_info(stage=32, players=[(2, 0, 1), (20, 1, 0)])
    assert len(gi) == 312
    assert struct.unpack(">H", gi[0xE:0x10])[0] == 32
    assert gi[0x60:0x64] == bytes([2, 0, 4, 1])
    assert gi[0x60 + 0x24:0x60 + 0x24 + 2] == bytes([20, 1])
    assert gi[0x60 + 2 * 0x24 + 1] == 3  # empty slot


def test_payload_table_is_first_event():
    w = SlpWriter(make_game_info(32, [(2, 0, 0)]))
    raw = _raw(w.to_bytes())
    assert raw[0] == Event.PAYLOADS
    n = raw[1]
    assert n == 3 * len(WRITER_SIZES) + 1
    table = {raw[2 + 3 * i]: struct.unpack(">H", raw[3 + 3 * i:5 + 3 * i])[0] for i in range(len(WRITER_SIZES))}
    assert table == {int(k): v for k, v in WRITER_SIZES.items()}
    assert raw[1 + n] == Event.GAME_START
    assert raw[2 + n:6 + n] == bytes([3, 5, 0, 0])


def test_frame_events_order_and_sizes():
    w = SlpWriter(make_game_info(32, [(2, 0, 0)]))
    w.frame(-123, [PreFrame(frame=-123, port=0, joy_x=0.5)], [PostFrame(frame=-123, port=0, x=1.0)])
    w.end()
    raw = _raw(w.to_bytes())
    pos = 1 + raw[1]
    pos += 1 + WRITER_SIZES[Event.GAME_START]
    codes = []
    while pos < len(raw):
        code = raw[pos]
        codes.append(code)
        if code == Event.PRE_FRAME:
            assert struct.unpack(">f", raw[pos + 0x19:pos + 0x1D])[0] == 0.5
        if code == Event.POST_FRAME:
            assert struct.unpack(">f", raw[pos + 0xA:pos + 0xE])[0] == 1.0
        pos += 1 + WRITER_SIZES[Event(code)]
    assert codes == [Event.FRAME_START, Event.PRE_FRAME, Event.POST_FRAME, Event.FRAME_BOOKEND, Event.GAME_END]
