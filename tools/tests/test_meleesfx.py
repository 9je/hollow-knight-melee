import struct

import pytest

from ssbmhk_tools import meleesfx


@pytest.fixture
def iso(dev_config):
    if not dev_config.iso.exists():
        pytest.skip("Melee ISO (dev.toml) not available")
    return dev_config.iso


def _sem(bank_base, scripts):
    """smash2.sem with five tables; scripts: list of word lists, placed after the tables."""
    head = 4 * (5 + len(bank_base) + len(scripts))
    offs, body = [], b""
    for s in scripts:
        offs.append(head + len(body))
        body += struct.pack(f">{len(s)}I", *s)
    tables = [[], [], bank_base, offs, []]
    return b"".join(struct.pack(f">I{len(t)}I", len(t), *t) for t in tables) + body


def test_sem_scripts_parse_into_commands():
    laser = [0x01000222, 0xFD000000, 0x10000014, 0x0400000F, 0x060000CC, 0x0E000000]
    fade = [0x00000003, 0x01000010, 0x0D04FF9C, 0x02000000, 0x07020Af6, 0x03000001, 0x0F000000]
    sem = meleesfx.Sem(_sem([0, 1, 2], [laser, fade]))
    assert sem.bank_size(0) == 1 and sem.bank_size(1) == 1
    assert sem.commands(0) == [(1, 546, 0), (0xFD, 0, 0), (16, 20, 0), (4, 15, 0), (6, 204, 0), (14, 0, 0)]
    assert sem.commands(10000) == [(0, 3, 3), (1, 16, 0), (13, -100, 4), (2, 0, 0), (7, -10, 0x020A),
                                   (3, 1, 0), (15, 0, 0)]
    assert sem.commands(1) is None and sem.commands(30000) is None


def test_dsp_adpcm_decodes_a_frame():
    # one frame: header 0x00 (predictor 0 = coefs 1024, 0; scale 1), nibbles 1, -1, 7, 0
    coefs = (1024, 0) + (0,) * 14
    data = bytes([0x00, 0x1F, 0x70]) + bytes(5)
    ch = dict(coefs=coefs, yn1=0, yn2=0, cur_addr=2, end_addr=5, loop=0, loop_addr=0)
    # s = (nib << 11 + 1024 + 1024*h1) >> 11
    assert meleesfx.dsp_decode(data, ch) == [1, 0, 7, 4]


def test_bundle_round_trips(tmp_path):
    scripts = {110103: [(1, 546, 0), (6, 204, 0), (14, 0, 0)]}
    samples = {546: meleesfx.Sample(546, 16000, [[1, 2, 3], [-1, -2, -3]], loop_start=-1)}
    p = tmp_path / "melee.sfx"
    meleesfx.write_bundle(p, scripts, samples)
    s2, m2 = meleesfx.read_bundle(p)
    assert s2 == scripts
    assert m2[546].rate == 16000 and m2[546].channels == [[1, 2, 3], [-1, -2, -3]] and m2[546].loop_start == -1


def test_extract_from_iso_fox_laser(iso, tmp_path):
    assert meleesfx.main([str(iso), "--out", str(tmp_path)]) == 0
    scripts, samples = meleesfx.read_bundle(tmp_path / "melee.sfx")
    assert all(i // 10000 in (0, 11) for i in scripts)
    assert 110103 in scripts and 110106 in scripts and 0 in scripts
    cmds = scripts[110103]
    assert (1, 546, 0) in cmds and (6, 204, 0) in cmds and cmds[-1][0] == 14
    laser = samples[546]
    assert len(laser.channels) == 2 and laser.rate == 16000 and laser.loop_start == -1
    assert 0.18 < len(laser.channels[0]) / laser.rate < 0.22
    assert max(abs(x) for x in laser.channels[0]) > 1000                 # not silence
    assert samples[517].rate > 0                                        # blaster draw (110100)
    used = {v for cs in scripts.values() for op, v, _ in cs if op == 1}
    assert used <= set(samples)                                         # every referenced sample is there
    assert any(s.loop_start >= 0 for s in samples.values())             # main.ssm's looping samples keep it
