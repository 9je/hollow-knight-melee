import hashlib
from pathlib import Path

import pytest

from ssbmhk_tools.config import load_dev_config
from ssbmhk_tools.iso import IsoMismatch, REDUMP_SHA1, REDUMP_SIZE, verify_iso


def test_redump_constants():
    assert REDUMP_SHA1 == "d4e70c064cc714ba8400a849cf299dbd1aa326fc"
    assert REDUMP_SIZE == 1_459_978_240


def test_verify_iso_accepts_matching_file(tmp_path: Path):
    f = tmp_path / "game (v1.02).iso"
    f.write_bytes(b"melee" * 10)
    verify_iso(f, hashlib.sha1(b"melee" * 10).hexdigest(), 50)


def test_verify_iso_rejects_wrong_size(tmp_path: Path):
    f = tmp_path / "x.iso"
    f.write_bytes(b"abc")
    with pytest.raises(IsoMismatch, match="size"):
        verify_iso(f, hashlib.sha1(b"abc").hexdigest(), 4)


def test_verify_iso_rejects_wrong_hash(tmp_path: Path):
    f = tmp_path / "x.iso"
    f.write_bytes(b"abc")
    with pytest.raises(IsoMismatch, match="SHA-1"):
        verify_iso(f, "0" * 40, 3)


def test_load_dev_config_expands_user_and_relative(tmp_path: Path, monkeypatch):
    monkeypatch.setenv("HOME", str(tmp_path))
    cfg = tmp_path / "dev.toml"
    cfg.write_text(
        'iso = "~/Melee (USA) (v1.02).iso"\n'
        'playback_appimage = "~/pb.AppImage"\n'
        'netplay_appimage = "~/np.AppImage"\n'
        'replay_dir = "~/Slippi"\n'
        'work_dir = ".oracle"\n'
    )
    c = load_dev_config(cfg)
    assert c.iso == tmp_path / "Melee (USA) (v1.02).iso"
    assert c.work_dir == tmp_path / ".oracle"
