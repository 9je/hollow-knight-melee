import hashlib
import struct

import pytest

from ssbmhk_tools.recomp.dol import MAIN_DOL_SHA1, WrongDol, load_main_dol, parse_dol
from ssbmhk_tools.recomp.symbols import functions, parse_symbols


def make_dol(text=b"\x60\x00\x00\x00" * 4, data=b"\x12\x34\x56\x78"):
    hdr = bytearray(0x100)
    offs = [0] * 18
    addrs = [0] * 18
    sizes = [0] * 18
    offs[0], addrs[0], sizes[0] = 0x100, 0x80003100, len(text)
    offs[7], addrs[7], sizes[7] = 0x100 + len(text), 0x80004000, len(data)
    hdr[0:0x48] = struct.pack(">18I", *offs)
    hdr[0x48:0x90] = struct.pack(">18I", *addrs)
    hdr[0x90:0xD8] = struct.pack(">18I", *sizes)
    hdr[0xD8:0xE4] = struct.pack(">III", 0x80004000, 0x100, 0x80003100)
    return bytes(hdr) + text + data


def test_parse_dol_sections_and_words():
    dol = parse_dol(make_dol())
    assert [s.addr for s in dol.text_sections] == [0x80003100]
    assert [s.addr for s in dol.data_sections] == [0x80004000]
    assert dol.word(0x80003104) == 0x60000000
    assert dol.entry == 0x80003100


def test_mem1_image_zeroes_bss_then_copies_sections():
    img = parse_dol(make_dol()).mem1_image()
    assert len(img) == 0x01800000
    assert img[0x4000:0x4004] == b"\x12\x34\x56\x78"   # data overrides bss
    assert img[0x4004:0x4100] == bytes(0xFC)
    assert img[0x3100:0x3104] == b"\x60\x00\x00\x00"


def test_wrong_dol_is_refused(tmp_path):
    iso = tmp_path / "fake.iso"
    blob = make_dol()
    raw = bytearray(0x440)
    raw[0x420:0x424] = struct.pack(">I", 0x440)
    iso.write_bytes(bytes(raw) + blob)
    with pytest.raises(WrongDol) as e:
        load_main_dol(iso)
    assert hashlib.sha1(blob).hexdigest() in str(e.value)
    assert load_main_dol(iso, check_sha1=False).entry == 0x80003100


def test_parse_symbols_functions():
    text = (
        "__start = .init:0x8000522C; // type:function size:0x114 scope:weak\n"
        "@219 = extab:0x80005520; // type:object size:0x8 scope:local hidden\n"
        "lbl_80001000 = .text:0x80001000; // type:label\n"
        "memcpy = .init:0x80003294; // type:function size:0x50 scope:global\n"
    )
    syms = parse_symbols(text)
    assert len(syms) == 4
    fns = functions(syms)
    assert [(f.name, f.addr, f.size) for f in fns] == [("memcpy", 0x80003294, 0x50), ("__start", 0x8000522C, 0x114)]


def test_real_main_dol(dev_config):
    dol = load_main_dol(dev_config.iso)
    assert dol.sha1 == MAIN_DOL_SHA1
    assert dol.entry == 0x8000522C
    assert dol.bss_addr == 0x804316C0


def test_real_symbols(dev_config):
    if dev_config.decomp_dir is None:
        pytest.skip("decomp_dir not set")
    fns = functions(parse_symbols((dev_config.decomp_dir / "config/GALE01/symbols.txt").read_text()))
    assert len(fns) == 19827
    assert all(f.size > 0 for f in fns)
