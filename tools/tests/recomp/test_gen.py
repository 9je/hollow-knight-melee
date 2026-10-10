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
