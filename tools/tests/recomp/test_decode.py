import pytest

from ssbmhk_tools.recomp.decode import decode
from ssbmhk_tools.recomp.dol import load_main_dol
from ssbmhk_tools.recomp.symbols import functions, parse_symbols


@pytest.mark.parametrize("word,op,fields", [
    (0x7C0802A6, "mfspr", {"rd": 0, "spr": 8}),
    (0x9421FFE0, "stwu", {"rs": 1, "ra": 1, "simm": -32}),
    (0x4E800020, "bclr", {"bo": 20, "lk": 0}),
    (0x4E800021, "bclr", {"lk": 1}),
    (0x4E800420, "bcctr", {"bo": 20}),
    (0xFC000840, "fcmpo", {"crfd": 0, "ra": 0, "rb": 1}),
    (0x7C000400, "mcrxr", {"crfd": 0}),
    (0x7C632214, "add", {"rd": 3, "ra": 3, "rb": 4}),
    (0x7C632215, "add.", {}),
    (0x7C632614, "unknown", {}),                      # addo: OE=1 is not used by main.dol
    (0x1022182A, "ps_add", {"rd": 1, "ra": 2, "rb": 3}),
    (0xE0232008, "psq_l", {"rd": 1, "ra": 3, "psq_w": 0, "psq_i": 2, "psq_d": 8}),
    (0x5483103A, "rlwinm", {"rs": 4, "ra": 3, "sh": 2, "mb": 0, "me": 29}),
    (0xEC2220FA, "fmadds", {"rd": 1, "ra": 2, "rb": 4, "rc": 3}),
    (0x4BFFFFFC, "b", {"li": -4, "aa": 0, "lk": 0}),
    (0x48000005, "b", {"li": 4, "lk": 1}),
    (0x4200FFF8, "bc", {"bo": 16, "bi": 0, "bd": -8}),
    (0x70630001, "andi.", {"uimm": 1}),                # low bit is immediate, not Rc
    (0xFC20081E, "fctiwz", {"rd": 1, "rb": 1}),
    (0xFC200851, "unknown", {}),                      # fneg. (Rc on FP) is not used by main.dol
    (0x00000000, "unknown", {}),
])
def test_decode_known_words(word, op, fields):
    i = decode(word)
    assert i.op == op
    for k, v in fields.items():
        assert getattr(i, k) == v, k


def test_every_word_in_every_function_decodes(dev_config):
    if dev_config.decomp_dir is None:
        pytest.skip("decomp_dir not set")
    dol = load_main_dol(dev_config.iso)
    fns = functions(parse_symbols((dev_config.decomp_dir / "config/GALE01/symbols.txt").read_text()))
    bad = {}
    for f in fns:
        for a in range(f.addr, f.addr + f.size, 4):
            w = dol.word(a)
            if decode(w).op == "unknown" and w != 0:
                bad[a] = w
    assert bad == {}, {f"{a:08X}": f"{w:08X}" for a, w in list(bad.items())[:20]}
