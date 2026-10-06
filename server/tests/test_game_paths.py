"""The catalog finds the game's data folder on Windows/Linux and macOS layouts."""

from pathlib import Path

import pytest

from btai.catalog import default_game_dir, resolve_data_dir


def make(root: Path, *parts: str) -> Path:
    d = root.joinpath(*parts)
    d.mkdir(parents=True)
    return d


def test_windows_and_linux_layout(tmp_path):
    data = make(tmp_path, "BattleTech_Data", "StreamingAssets", "data")
    assert resolve_data_dir(tmp_path) == data


def test_mac_layout_from_the_steam_folder(tmp_path):
    data = make(tmp_path, "BattleTech.app", "Contents", "Resources", "Data", "StreamingAssets", "data")
    assert resolve_data_dir(tmp_path) == data


def test_mac_layout_pointing_at_the_app_bundle(tmp_path):
    app = tmp_path / "BattleTech.app"
    data = make(app, "Contents", "Resources", "Data", "StreamingAssets", "data")
    assert resolve_data_dir(app) == data


def test_missing_data_lists_every_place_it_looked(tmp_path):
    with pytest.raises(FileNotFoundError) as e:
        resolve_data_dir(tmp_path)
    assert "BattleTech_Data" in str(e.value) and "BattleTech.app" in str(e.value)


def test_default_install_location_per_platform():
    assert str(default_game_dir("darwin")).endswith(str(Path("Library/Application Support/Steam/steamapps/common/BATTLETECH")))
    assert str(default_game_dir("linux")).endswith(str(Path(".local/share/Steam/steamapps/common/BATTLETECH")))
    assert default_game_dir("win32").name == "BATTLETECH"
