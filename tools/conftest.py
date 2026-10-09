from pathlib import Path

import pytest

from ssbmhk_tools.config import load_dev_config

REPO = Path(__file__).resolve().parent.parent


def pytest_addoption(parser):
    parser.addoption("--run-oracle", action="store_true", help="run tests that launch Slippi Dolphin")


def pytest_collection_modifyitems(config, items):
    if config.getoption("--run-oracle"):
        return
    skip = pytest.mark.skip(reason="needs --run-oracle")
    for item in items:
        if "oracle" in item.keywords:
            item.add_marker(skip)


@pytest.fixture
def dev_config():
    path = REPO / "dev.toml"
    if not path.exists():
        pytest.skip("dev.toml not present")
    return load_dev_config(path)
