import tomllib
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class DevConfig:
    iso: Path
    playback_appimage: Path
    netplay_appimage: Path
    replay_dir: Path
    work_dir: Path
    decomp_dir: Path | None = None


def _resolve(raw: str, base: Path) -> Path:
    p = Path(raw).expanduser()
    return p if p.is_absolute() else base / p


def load_dev_config(path: Path) -> DevConfig:
    data = tomllib.loads(path.read_text())
    base = path.parent
    return DevConfig(**{k: _resolve(data[k], base) for k in DevConfig.__dataclass_fields__ if k in data})
