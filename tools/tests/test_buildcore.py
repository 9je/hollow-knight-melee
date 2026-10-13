from pathlib import Path

from ssbmhk_tools import buildcore

REPO = Path(__file__).resolve().parents[2]


def test_sources_follow_the_makefile():
    core = REPO / "core"
    gen = Path("/nonexistent/gen")
    names = [p.name for p, _ in buildcore.sources(core, gen, "windows")]
    for s in buildcore.runtime_sources(core):
        assert f"{s}.c" in names
    assert "soft_fma.c" in names and "dispatch.c" in names and "gekko_fp.c" in names
    assert "soft_fma.c" not in [p.name for p, _ in buildcore.sources(core, gen, "linux")]


def test_a_header_change_rebuilds_everything(tmp_path):
    core = tmp_path / "core"
    (core / "runtime").mkdir(parents=True)
    (core / "gekko").mkdir()
    gen = tmp_path / "gen"
    gen.mkdir()
    (core / "runtime/mc_internal.h").write_text("struct Machine { int a; };\n")
    (core / "gekko/gekko_fp.h").write_text("\n")
    (gen / "guest_meta.h").write_text("\n")
    k1 = buildcore.headers_key(core, gen)
    (core / "runtime/mc_internal.h").write_text("struct Machine { int a, b; };\n")
    assert buildcore.headers_key(core, gen) != k1
    k2 = buildcore.headers_key(core, gen)
    (gen / "guest_meta.h").write_text("#define X 1\n")
    assert buildcore.headers_key(core, gen) != k2
