"""Offline view of BattleTech's static game data (StreamingAssets/data JSON).

This works without the game running. It covers the loose-JSON content only: DLC
content (Heavy Metal, Flashpoint, Urban Warfare) ships inside Unity asset bundles
and is only visible through the live bridge. The live bridge is also authoritative
for validation; `check_build` here is a pre-check for offline design work.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

DEFAULT_GAME_DIR = r"F:\SteamLibrary\steamapps\common\BATTLETECH"

# Component folders under data/, keyed by the ComponentDefType the game uses in mechdef inventories.
COMPONENT_DIRS = {
    "Weapon": "weapon",
    "AmmunitionBox": "ammunitionBox",
    "HeatSink": "heatsinks",
    "JumpJet": "jumpjets",
    "Upgrade": "upgrades",
}

LOCATIONS = ["Head", "CenterTorso", "LeftTorso", "RightTorso", "LeftArm", "RightArm", "LeftLeg", "RightLeg"]
REAR_LOCATIONS = {"CenterTorso", "LeftTorso", "RightTorso"}

# MechStatisticsConstants.json: ARMOR_PER_TENTH_TON = 8.0, i.e. 80 armor points per ton.
ARMOR_PER_TON = 80.0

_TRAILING_COMMA = re.compile(r",(\s*[}\]])")

# ChassisLocations is a [Flags] enum; component AllowedLocations uses these group names.
LOCATION_GROUPS = {
    "All": set(LOCATIONS),
    "Torso": {"CenterTorso", "LeftTorso", "RightTorso"},
    "Arms": {"LeftArm", "RightArm"},
    "Legs": {"LeftLeg", "RightLeg"},
    "SideTorsos": {"LeftTorso", "RightTorso"},
    "None": set(),
}


def _norm(s: str) -> str:
    """'Medium Laser' and 'Weapon_Laser_MediumLaser_0-STOCK' both reduce to comparable alphanumerics."""
    return re.sub(r"[^a-z0-9]", "", s.lower())


def expand_locations(spec: str | None) -> set[str]:
    """'Torso, Legs' -> {'CenterTorso', 'LeftTorso', 'RightTorso', 'LeftLeg', 'RightLeg'}."""
    out: set[str] = set()
    for part in (spec or "All").split(","):
        part = part.strip()
        out |= LOCATION_GROUPS.get(part, {part})
    return out


def load_lenient_json(path: Path) -> Any:
    """The game's parser tolerates trailing commas; a few shipped files rely on that."""
    text = path.read_text(encoding="utf-8-sig")
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return json.loads(_TRAILING_COMMA.sub(r"\1", text))


@dataclass
class Component:
    id: str
    name: str
    type: str
    tonnage: float
    slots: int
    allowed_locations: str
    cost: int
    raw: dict = field(repr=False)
    full_name: str = ""

    # Set by GameData for ammo boxes: the ammo category ("AC20", "LRM"...) resolved via AmmoID.
    ammo_category: str | None = None

    @property
    def weapon_category(self) -> str | None:
        """Ballistic / Energy / Missile / AntiPersonnel for weapons, else None."""
        return self.raw.get("Category") if self.type == "Weapon" else None

    @property
    def uses_ammo(self) -> str | None:
        """Ammo category a weapon consumes, or None for energy weapons and internal-ammo weapons."""
        if self.type != "Weapon":
            return None
        cat = self.raw.get("ammoCategoryID")
        if not cat or cat == "NotSet" or self.raw.get("StartingAmmoCapacity", 0) > 0:
            return None
        return cat

    def summary(self) -> dict:
        d = {
            "id": self.id,
            "name": self.name,
            "full_name": self.full_name,
            "type": self.type,
            "tonnage": self.tonnage,
            "slots": self.slots,
            "cost": self.cost,
            "allowed_locations": self.allowed_locations,
        }
        r = self.raw
        if self.type == "Weapon":
            d["weapon"] = {
                "category": r.get("Category"),
                "weapon_type": r.get("Type"),
                "subtype": r.get("WeaponSubType"),
                "damage": r.get("Damage"),
                "shots": r.get("ShotsWhenFired"),
                "heat": r.get("HeatGenerated"),
                "stability_damage": r.get("Instability"),
                "accuracy_mod": r.get("AccuracyModifier"),
                "crit_mult": r.get("CriticalChanceMultiplier"),
                "range": {"min": r.get("MinRange"), "max": r.get("MaxRange"), "brackets": r.get("RangeSplit")},
                "ammo_category": r.get("ammoCategoryID"),
                "indirect_fire": r.get("IndirectFireCapable"),
            }
        elif self.type == "AmmunitionBox":
            d["ammo"] = {"category": self.ammo_category, "capacity": r.get("Capacity")}
        elif self.type == "HeatSink":
            d["dissipation"] = r.get("DissipationCapacity")
        bonuses = [b for b in (r.get("BonusValueA"), r.get("BonusValueB")) if b]
        if bonuses:
            d["bonuses"] = bonuses
        return d


@dataclass
class Chassis:
    id: str
    name: str
    variant: str
    tonnage: float
    initial_tonnage: float
    weight_class: str
    max_jumpjets: int
    locations: dict[str, dict]
    raw: dict = field(repr=False)

    def hardpoints(self, location: str) -> dict[str, int]:
        counts: dict[str, int] = {}
        for hp in self.locations[location].get("Hardpoints") or []:
            key = "Omni" if hp.get("Omni") else hp.get("WeaponMount", "Unknown")
            counts[key] = counts.get(key, 0) + 1
        return counts

    def summary(self) -> dict:
        return {
            "id": self.id,
            "name": self.name,
            "variant": self.variant,
            "tonnage": self.tonnage,
            "initial_tonnage": self.initial_tonnage,
            "free_tonnage": self.tonnage - self.initial_tonnage,
            "weight_class": self.weight_class,
            "max_jumpjets": self.max_jumpjets,
            "stock_role": self.raw.get("StockRole"),
            "top_speed": self.raw.get("TopSpeed"),
            "melee_damage": self.raw.get("MeleeDamage"),
            "locations": {
                loc: {
                    "slots": d.get("InventorySlots"),
                    "max_armor": d.get("MaxArmor"),
                    "max_rear_armor": d.get("MaxRearArmor") if loc in REAR_LOCATIONS else None,
                    "structure": d.get("InternalStructure"),
                    "hardpoints": self.hardpoints(loc),
                }
                for loc, d in self.locations.items()
            },
            "notes": self.raw.get("YangsThoughts"),
        }


class GameData:
    def __init__(self, game_dir: str | os.PathLike | None = None):
        game_dir = Path(game_dir or os.environ.get("BATTLETECH_DIR") or DEFAULT_GAME_DIR)
        self.data_dir = game_dir / "BattleTech_Data" / "StreamingAssets" / "data"
        if not self.data_dir.is_dir():
            raise FileNotFoundError(f"BattleTech data directory not found: {self.data_dir}")
        self.components: dict[str, Component] = {}
        self.chassis: dict[str, Chassis] = {}
        self.mechs: dict[str, dict] = {}
        self.load_errors: dict[str, str] = {}
        self._load()

    # -- loading ---------------------------------------------------------

    def _iter_json(self, subdir: str):
        for path in sorted((self.data_dir / subdir).rglob("*.json")):
            if "template" in path.name.lower():
                continue
            try:
                yield path, load_lenient_json(path)
            except (json.JSONDecodeError, UnicodeDecodeError) as e:
                self.load_errors[str(path.relative_to(self.data_dir))] = str(e)

    def _load(self) -> None:
        for ctype, subdir in COMPONENT_DIRS.items():
            for _, j in self._iter_json(subdir):
                desc = j.get("Description") or {}
                if not desc.get("Id"):
                    continue
                self.components[desc["Id"]] = Component(
                    id=desc["Id"],
                    name=desc.get("UIName") or desc.get("Name") or desc["Id"],
                    type=j.get("ComponentType", ctype),
                    tonnage=float(j.get("Tonnage", 0)),
                    slots=int(j.get("InventorySize", 0)),
                    allowed_locations=j.get("AllowedLocations", "All"),
                    cost=int(desc.get("Cost", 0)),
                    raw=j,
                    full_name=desc.get("Name") or "",
                )
        ammo_categories = {
            (j.get("Description") or {}).get("Id"): j.get("Category") for _, j in self._iter_json("ammunition")
        }
        for c in self.components.values():
            if c.type == "AmmunitionBox":
                c.ammo_category = ammo_categories.get(c.raw.get("AmmoID"))
        for _, j in self._iter_json("chassis"):
            desc = j.get("Description") or {}
            if not desc.get("Id"):
                continue
            self.chassis[desc["Id"]] = Chassis(
                id=desc["Id"],
                name=desc.get("Name", desc["Id"]),
                variant=j.get("VariantName", ""),
                tonnage=float(j["Tonnage"]),
                initial_tonnage=float(j["InitialTonnage"]),
                weight_class=j.get("weightClass", ""),
                max_jumpjets=int(j.get("MaxJumpjets", 0)),
                locations={loc["Location"]: loc for loc in j.get("Locations", [])},
                raw=j,
            )
        for _, j in self._iter_json("mech"):
            desc = j.get("Description") or {}
            if desc.get("Id") and j.get("ChassisID"):
                self.mechs[desc["Id"]] = j

    # -- queries ---------------------------------------------------------

    def search_components(self, query: str = "", type: str | None = None, category: str | None = None,
                          include_variants: bool = True, limit: int = 50) -> list[dict]:
        """Substring match on id or name. Variants are the +/++ manufacturer versions (non -STOCK ids)."""
        q = query.lower()
        out = []
        for c in self.components.values():
            if type and c.type.lower() != type.lower():
                continue
            if category and (c.weapon_category or "").lower() != category.lower():
                continue
            if not include_variants and c.type == "Weapon" and not c.id.endswith("-STOCK"):
                continue
            if q and not any(_norm(q) in _norm(s) for s in (c.id, c.name, c.full_name)):
                continue
            out.append(c.summary())
        out.sort(key=lambda d: (d["type"], d["name"], d["id"]))
        return out[:limit]

    def list_chassis(self, weight_class: str | None = None, min_tons: float = 0, max_tons: float = 200) -> list[dict]:
        out = [
            {"id": c.id, "name": c.name, "variant": c.variant, "tonnage": c.tonnage, "weight_class": c.weight_class,
             "free_tonnage": c.tonnage - c.initial_tonnage, "max_jumpjets": c.max_jumpjets,
             "hardpoints": self._total_hardpoints(c)}
            for c in self.chassis.values()
            if min_tons <= c.tonnage <= max_tons and (not weight_class or c.weight_class.lower() == weight_class.lower())
        ]
        return sorted(out, key=lambda d: (d["tonnage"], d["name"], d["variant"]))

    def _total_hardpoints(self, c: Chassis) -> dict[str, int]:
        total: dict[str, int] = {}
        for loc in c.locations:
            for k, v in c.hardpoints(loc).items():
                total[k] = total.get(k, 0) + v
        return total

    def stock_mechs_for_chassis(self, chassis_id: str) -> list[str]:
        return sorted(mid for mid, m in self.mechs.items() if m["ChassisID"] == chassis_id)

    # -- build analysis --------------------------------------------------

    def analyze_mechdef(self, mech: dict) -> dict:
        """Summarize a mechdef-shaped dict (the game's own JSON format) and pre-check its legality."""
        chassis = self.chassis.get(mech.get("ChassisID", ""))
        if chassis is None:
            return {"ok": False, "errors": [f"unknown chassis {mech.get('ChassisID')!r}"]}

        errors: list[str] = []
        warnings: list[str] = []
        inventory = mech.get("inventory") or []
        armor = {loc["Location"]: loc for loc in mech.get("Locations") or []}

        per_loc: dict[str, dict] = {
            loc: {"slots_used": 0, "weapons": {}, "items": []} for loc in chassis.locations
        }
        component_tons = 0.0
        heat = 0
        damage = 0.0
        dissipation = 0
        jumpjets = 0
        ammo_needed: set[str] = set()
        ammo_present: set[str] = set()

        for item in inventory:
            cid = item.get("ComponentDefID")
            loc = item.get("MountedLocation")
            comp = self.components.get(cid)
            if comp is None:
                errors.append(f"unknown component {cid!r} (DLC items are only known to the live game)")
                continue
            if loc not in per_loc:
                errors.append(f"{comp.name}: invalid location {loc!r}")
                continue
            component_tons += comp.tonnage
            per_loc[loc]["slots_used"] += comp.slots
            per_loc[loc]["items"].append(comp.name)
            if loc not in expand_locations(comp.allowed_locations):
                errors.append(f"{comp.name} cannot be mounted in {loc} (allowed: {comp.allowed_locations})")
            if comp.type == "Weapon":
                cat = comp.weapon_category or "Unknown"
                per_loc[loc]["weapons"][cat] = per_loc[loc]["weapons"].get(cat, 0) + 1
                heat += int(comp.raw.get("HeatGenerated", 0))
                damage += float(comp.raw.get("Damage", 0)) * int(comp.raw.get("ShotsWhenFired", 1))
                if comp.uses_ammo:
                    ammo_needed.add(comp.uses_ammo)
            elif comp.type == "AmmunitionBox":
                if comp.ammo_category:
                    ammo_present.add(comp.ammo_category)
            elif comp.type == "HeatSink":
                dissipation += int(comp.raw.get("DissipationCapacity", 0))
            elif comp.type == "JumpJet":
                jumpjets += 1

        armor_points = 0.0
        for loc, cl in chassis.locations.items():
            a = armor.get(loc, {})
            front = float(a.get("AssignedArmor", 0))
            rear = float(a.get("AssignedRearArmor", 0)) if loc in REAR_LOCATIONS else 0.0
            armor_points += front + max(rear, 0.0)
            if front > cl.get("MaxArmor", 0):
                errors.append(f"{loc}: front armor {front:g} exceeds max {cl.get('MaxArmor'):g}")
            if loc in REAR_LOCATIONS and rear > cl.get("MaxRearArmor", 0):
                errors.append(f"{loc}: rear armor {rear:g} exceeds max {cl.get('MaxRearArmor'):g}")

            used = per_loc[loc]["slots_used"]
            if used > cl.get("InventorySlots", 0):
                errors.append(f"{loc}: {used} slots used, only {cl.get('InventorySlots')} available")

            available = chassis.hardpoints(loc)
            omni_left = available.get("Omni", 0)
            for cat, n in per_loc[loc]["weapons"].items():
                over = n - available.get(cat, 0)
                if over > 0:
                    take = min(over, omni_left)
                    omni_left -= take
                    if over - take > 0:
                        errors.append(f"{loc}: {n} {cat} weapons but only {available.get(cat, 0)} {cat} hardpoints")

        if jumpjets > chassis.max_jumpjets:
            errors.append(f"{jumpjets} jump jets exceeds chassis max {chassis.max_jumpjets}")

        tonnage = chassis.initial_tonnage + armor_points / ARMOR_PER_TON + component_tons
        if tonnage > chassis.tonnage + 1e-6:
            errors.append(f"overweight: {tonnage:.2f} / {chassis.tonnage:g} tons")
        elif chassis.tonnage - tonnage >= 0.5:
            warnings.append(f"underweight: {chassis.tonnage - tonnage:.2f} tons unused")
        if not any(v["weapons"] for v in per_loc.values()):
            errors.append("no weapons installed")
        for cat in sorted(ammo_needed - ammo_present):
            errors.append(f"no ammunition for {cat} weapons")
        for cat in sorted(ammo_present - ammo_needed):
            warnings.append(f"ammunition for {cat} but no weapon uses it")

        return {
            "ok": not errors,
            "errors": errors,
            "warnings": warnings,
            "chassis": chassis.id,
            "tonnage": {"used": round(tonnage, 3), "max": chassis.tonnage,
                        "breakdown": {"chassis": chassis.initial_tonnage,
                                      "armor": round(armor_points / ARMOR_PER_TON, 3),
                                      "components": round(component_tons, 3)}},
            "armor_points": armor_points,
            "alpha_strike": {"damage": damage, "heat": heat},
            "heat_sink_dissipation_bonus": dissipation,
            "jump_jets": {"installed": jumpjets, "max": chassis.max_jumpjets},
            "locations": {
                loc: {"slots": f"{v['slots_used']}/{chassis.locations[loc].get('InventorySlots')}",
                      "hardpoints_used": v["weapons"], "hardpoints": chassis.hardpoints(loc), "items": v["items"]}
                for loc, v in per_loc.items()
            },
            "note": "offline pre-check against base-game data; the live game validator is authoritative",
        }
