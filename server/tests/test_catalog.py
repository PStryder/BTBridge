"""Catalog tests run against the real installed game data.

The stock mechs are the ground truth: HBS shipped them as legal builds, so the
offline checker must accept them, and each mutation below must be caught.
"""

import copy

import pytest

from btai.catalog import GameData, expand_locations, load_lenient_json

BASELINE = "mechdef_hunchback_HBK-4G"


@pytest.fixture(scope="module")
def game():
    try:
        return GameData()
    except FileNotFoundError as e:
        pytest.skip(str(e))


def _is_special(game, mech):
    tags = (mech.get("MechTags") or {}).get("items") or []
    return "BLACKLISTED" in tags or "DUMMY" in mech["Description"]["Id"].upper()


def _all_parts_known(game, mech):
    return all(i["ComponentDefID"] in game.components for i in mech["inventory"])


def test_loads_everything(game):
    assert len(game.components) > 200
    assert len(game.chassis) > 80
    assert len(game.mechs) > 100
    assert game.load_errors == {}


def test_tonnage_formula_matches_stock_mechs(game):
    # Stock mechs are built to exactly their chassis weight, so this pins the
    # armor-per-ton constant and the chassis + armor + components formula.
    checked = 0
    for mid, mech in game.mechs.items():
        if _is_special(game, mech) or not _all_parts_known(game, mech):
            continue
        r = game.analyze_mechdef(mech)
        assert r["tonnage"]["used"] <= r["tonnage"]["max"] + 1e-6, mid
        assert r["tonnage"]["max"] - r["tonnage"]["used"] < 1.0, mid
        checked += 1
    assert checked >= 70


def test_every_regular_stock_mech_passes(game):
    failures = {
        mid: r["errors"]
        for mid, mech in game.mechs.items()
        if not _is_special(game, mech) and _all_parts_known(game, mech)
        for r in [game.analyze_mechdef(mech)]
        if not r["ok"]
    }
    assert failures == {}


def _baseline(game):
    return copy.deepcopy(game.mechs[BASELINE])


def _errors(game, mech):
    return " | ".join(game.analyze_mechdef(mech)["errors"])


def test_baseline_is_clean(game):
    assert game.analyze_mechdef(_baseline(game))["ok"]


def test_detects_overweight(game):
    m = _baseline(game)
    m["inventory"].append({"ComponentDefID": "Gear_HeatSink_Generic_Standard", "MountedLocation": "LeftTorso"})
    assert "overweight" in _errors(game, m)


def test_detects_slot_overflow(game):
    m = _baseline(game)
    # Strip armor to make tonnage room, then cram the head (1 slot).
    for loc in m["Locations"]:
        loc["AssignedArmor"] = 0
        loc["AssignedRearArmor"] = 0
    m["inventory"].append({"ComponentDefID": "Gear_HeatSink_Generic_Standard", "MountedLocation": "Head"})
    assert "Head:" in _errors(game, m) and "slots used" in _errors(game, m)


def test_detects_hardpoint_violation(game):
    m = _baseline(game)
    for item in m["inventory"]:
        if item["ComponentDefID"].startswith("Weapon_Autocannon_AC20"):
            item["MountedLocation"] = "LeftTorso"  # the HBK-4G's left torso has no ballistic mount
    assert "Ballistic hardpoints" in _errors(game, m)


def test_detects_missing_ammo(game):
    m = _baseline(game)
    m["inventory"] = [i for i in m["inventory"] if "Ammo" not in i["ComponentDefID"]]
    assert "no ammunition for AC20" in _errors(game, m)


def test_detects_armor_over_max(game):
    m = _baseline(game)
    head = next(loc for loc in m["Locations"] if loc["Location"] == "Head")
    head["AssignedArmor"] = 10_000
    assert "Head: front armor" in _errors(game, m)


def test_detects_disallowed_location(game):
    m = _baseline(game)
    m["inventory"].append({"ComponentDefID": "Gear_JumpJet_Generic_Standard", "MountedLocation": "LeftArm"})
    assert "cannot be mounted in LeftArm" in _errors(game, m)


def test_detects_unknown_chassis(game):
    m = _baseline(game)
    m["ChassisID"] = "chassisdef_nope"
    assert not game.analyze_mechdef(m)["ok"]


def test_expand_locations():
    assert expand_locations("Torso, Legs") == {"CenterTorso", "LeftTorso", "RightTorso", "LeftLeg", "RightLeg"}
    assert expand_locations("Head") == {"Head"}
    assert len(expand_locations("All")) == 8


def test_lenient_json_accepts_trailing_commas(tmp_path):
    p = tmp_path / "x.json"
    p.write_text('{"a": [1, 2,], "b": {"c": 3,},}', encoding="utf-8")
    assert load_lenient_json(p) == {"a": [1, 2], "b": {"c": 3}}


def test_search_and_chassis_queries(game):
    lasers = game.search_components("medium laser", type="Weapon")
    assert lasers and all(c["weapon"]["category"] == "Energy" for c in lasers)
    lights = game.list_chassis(weight_class="LIGHT")
    assert lights and all(20 <= c["tonnage"] <= 35 for c in lights)
    ammo = game.search_components("AC/20", type="AmmunitionBox")
    assert ammo and ammo[0]["ammo"]["category"] == "AC20"
