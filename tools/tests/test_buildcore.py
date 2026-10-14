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


def test_frame_dump_links_against_the_cores_objects_for_this_machine(tmp_path):
    core, gen = REPO / "core", tmp_path / "gen"
    objs = [tmp_path / "a.o", tmp_path / "b.o"]
    win = buildcore.frame_dump_command(core, gen, objs, ["zig", "cc", "-target", "x86_64-windows-gnu"], "windows", tmp_path / "frame_dump.exe")
    assert win[:4] == ["zig", "cc", "-target", "x86_64-windows-gnu"] and str(core / "tools/frame_dump.c") in win
    assert win[-2:] == ["-o", str(tmp_path / "frame_dump.exe")] and str(objs[1]) in win and "-lpthread" not in win
    lin = buildcore.frame_dump_command(core, gen, objs, ["gcc"], "linux", tmp_path / "frame_dump")
    assert lin[-2:] == ["-lm", "-lpthread"] and "-I" in lin and str(gen) in lin


def test_sheik_is_not_rendered_by_a_cross_build(tmp_path):
    other = "linux" if buildcore.os.name == "nt" else "windows"
    assert buildcore.render_sheik(tmp_path / "melee.iso", REPO / "core", tmp_path, tmp_path, ["cc"], other) == ([], None)


def test_object_paths_follow_the_sources(tmp_path):
    core, gen = REPO / "core", tmp_path / "gen"
    paths = buildcore.object_paths(core, gen, tmp_path, "linux")
    assert len(paths) == len(buildcore.sources(core, gen, "linux")) and all(p.parent == tmp_path / "linux" for p in paths)
    assert (tmp_path / "linux" / "runtime_gx_draw.o") in paths
