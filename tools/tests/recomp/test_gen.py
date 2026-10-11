from ssbmhk_tools.recomp.dol import load_main_dol
from ssbmhk_tools.recomp.gen import generate, sda_bases
from ssbmhk_tools.recomp.symbols import functions, parse_symbols

import pytest


@pytest.fixture
def real(dev_config):
    if dev_config.decomp_dir is None:
        pytest.skip("decomp_dir not set")
    dol = load_main_dol(dev_config.iso)
    fns = functions(parse_symbols((dev_config.decomp_dir / "config/GALE01/symbols.txt").read_text()))
    return dol, fns


def test_sda_bases_are_in_sdata(real):
    dol, fns = real
    r2, r13 = sda_bases(dol, fns)
    assert 0x804D0000 < r13 < 0x804E0000 and 0x804D0000 < r2 < 0x804F0000


def test_generate_whole_dol(real, tmp_path):
    dol, fns = real
    rep = generate(dol, fns, tmp_path)
    assert rep.n_funcs == 19827
    assert rep.n_entries >= rep.n_funcs
    assert rep.unresolved == {}
    assert set(rep.trap_ops) <= {"sc", "rfi", "tw", "dcbz_l", "unknown"}
    assert (tmp_path / "mem1.bin").stat().st_size == 0x01800000
    assert "GUEST_R13" in (tmp_path / "guest_meta.h").read_text()


def test_guest_table_has_function_names(real, tmp_path):
    dol, fns = real
    generate(dol, fns, tmp_path)
    assert '"memcpy"' in (tmp_path / "guest_table.c").read_text()


def test_hle_names_and_unchanged_files_keep_mtime(real, tmp_path):
    dol, fns = real
    generate(dol, fns, tmp_path, hle_names=["OSReport"])
    shards = sorted(tmp_path.glob("shard_*.c"))
    assert any("return hle_OSReport(c);" in p.read_text() for p in shards)
    assert "uint32_t hle_OSReport(Cpu *c);" in (tmp_path / "guest_fns.h").read_text()
    before = {p: p.stat().st_mtime_ns for p in shards}
    generate(dol, fns, tmp_path, hle_names=["OSReport"])
    assert {p: p.stat().st_mtime_ns for p in shards} == before


def test_gecko_codes_patch_code_and_image(real, tmp_path):
    dol, fns = real
    # inject at OSReport's first instruction: body = (li r0,0 ; original-ish nop) then branch back
    at = next(s.addr for s in fns if s.name == "OSReport")
    code = f"C2{at & 0x01FFFFFF:06X} 00000001\n38000000 00000000\n04{(at + 4) & 0x01FFFFFF:06X} 60000000\n"
    generate(dol, fns, tmp_path, gecko=code)
    shards = "".join(p.read_text() for p in tmp_path.glob("shard_*.c"))
    assert "uint32_t fn_80001800(Cpu *c) {  /* gecko_" in shards
    assert f"return fn_80001800(c);" in shards and f"return fn_{at + 4:08X}(c);" in shards
    img = (tmp_path / "mem1.bin").read_bytes()
    assert img[(at + 4) & 0x01FFFFFF:(at + 8) & 0x01FFFFFF] == bytes.fromhex("60000000")
    assert img[0x1800:0x1804] == bytes.fromhex("38000000")


def test_guest_meta_has_mem1_crc32(real, tmp_path):
    import re
    import zlib
    dol, fns = real
    generate(dol, fns, tmp_path)
    meta = (tmp_path / "guest_meta.h").read_text()
    m = re.search(r"#define GUEST_MEM1_CRC32 0x([0-9A-F]{8})u", meta)
    assert m and int(m.group(1), 16) == zlib.crc32((tmp_path / "mem1.bin").read_bytes())


def test_gecko_body_returning_past_the_call_site_gets_landings(real, tmp_path):
    dol, fns = real
    # as UCF Shield Drop does at 800998A4: return to the caller's return address + 8. Its function is called
    # from 80099870, so that call must be able to land at 8009987C.
    code = "C20998A4 00000002\n38E70008 7CE803A6\n4E800020 00000000\n"
    generate(dol, fns, tmp_path, gecko=code)
    shards = "".join(p.read_text() for p in tmp_path.glob("shard_*.c"))
    assert "if (r_ == 0x8009987Cu) goto L_8009987C;" in shards


def test_tap_names_declared_and_called(real, tmp_path):
    dol, fns = real
    generate(dol, fns, tmp_path, tap_names=["HSD_AudioSFXKeyOffAll"])
    shards = "".join(p.read_text() for p in tmp_path.glob("shard_*.c"))
    assert "  tap_HSD_AudioSFXKeyOffAll(c);\n" in shards
    assert "void tap_HSD_AudioSFXKeyOffAll(Cpu *c);" in (tmp_path / "guest_fns.h").read_text()
    with pytest.raises(ValueError):
        generate(dol, fns, tmp_path, tap_names=["no_such_function"])
