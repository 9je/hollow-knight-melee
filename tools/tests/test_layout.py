import struct

from ssbmhk_tools import layout


def test_chain_for_port0_motion_id():
    f = next(f for f in layout.FIGHTER_FIELDS if f.name == "motion_id")
    assert layout.location(0, f) == "80453130 2C 10"


def test_chain_for_port2_offsets_by_stride():
    f = next(f for f in layout.FIGHTER_FIELDS if f.name == "pos.x")
    assert layout.location(2, f) == f"{0x80453130 + 2 * 0xE90:08X} 2C B0"


def test_ecb_and_capsules_present_with_decomp_offsets():
    by = {f.name: f.offset for f in layout.FIGHTER_FIELDS}
    assert by["ecb.top.x"] == 0x794
    assert by["ecb.left.y"] == 0x794 + 0x1C
    assert by["hit0.pos.x"] == 0x914 + 0x4C
    assert by["hit3.state"] == 0x914 + 3 * 0x138
    assert by["hurt0.a_pos.x"] == 0x11A0 + 0x28
    assert by["hurt14.b_pos.z"] == 0x11A0 + 14 * 0x4C + 0x34 + 8
    assert by["percent"] == 0x1830
    assert by["shield"] == 0x1998
    assert by["in.lstick.x"] == 0x620 and by["in.held"] == 0x65C


def test_fields_are_word_aligned_and_unique():
    names = [f.name for f in layout.FIGHTER_FIELDS]
    assert len(names) == len(set(names))
    assert all(f.offset % 4 == 0 for f in layout.FIGHTER_FIELDS)


def test_counter_sorts_last_in_file_and_lexicographically():
    locs = layout.locations([0, 1, 2, 3])
    assert locs[-1] == layout.counter_location() == "80479D60"
    assert max(locs) == locs[-1]
    assert len(locs) == len(set(locs))


def test_decode():
    w = struct.unpack(">I", struct.pack(">f", -1.5))[0]
    assert layout.decode("f32", 0, w) == -1.5
    assert layout.decode("s32", 0, 0xFFFFFFFF) == -1
    assert layout.decode("u8", 1, 0x00AB0000) == 0xAB
    assert layout.decode("s16", 0, 0xFFFE0000) == -2
