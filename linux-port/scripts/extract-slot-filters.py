#!/usr/bin/env python3
"""Extracts the slot and rarity dropdown tables from upstream's UIHelper.cs.

Emits one line per entry so a diff against the port's own table reads as a list of
labels, not as a C# reformatting.

    iatag_slot_weapon1h  WeaponMelee_Dagger,WeaponMelee_Mace,...
    iatag_rarity_green_p1  Green/1

"Other" is skipped on both sides: upstream computes it from every other entry, so it can
only differ if one of those already has.
"""
import re
import sys

def slots(text: str):
    body = text.split("SlotFilter {", 1)[1].split("public static", 1)[0] \
        if "SlotFilter {" in text else ""

    # Each entry is a ComboBoxItem with a GetTag("...") and an optional Filter array.
    for entry in re.split(r"new ComboBoxItem", body)[1:]:
        tag = re.search(r'GetTag\("([^"]+)"\)', entry)
        if not tag or tag.group(1) == "iatag_slot_other":
            continue
        classes = re.findall(r'"([A-Za-z_0-9]+)"', entry.split("Filter", 1)[1]) \
            if "Filter" in entry and "null" not in entry.split("Filter", 1)[1][:20] else []
        yield f"{tag.group(1)}\t{','.join(classes)}"

def qualities(text: str):
    body = text.split("QualityFilter {", 1)[1].split("public static", 1)[0]
    for entry in re.split(r"new ComboBoxItemQuality", body)[1:]:
        tag = re.search(r'GetTag\("([^"]+)"\)', entry)
        if not tag:
            continue
        rarity = re.search(r'Rarity = "([^"]+)"', entry)
        prefix = re.search(r"PrefixRarity = (\d+)", entry)
        yield f"{tag.group(1)}\t{rarity.group(1) if rarity else ''}/{prefix.group(1) if prefix else '0'}"

def main() -> int:
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} <path/to/UIHelper.cs>", file=sys.stderr)
        return 2

    text = open(sys.argv[1], encoding="utf-8-sig").read()
    for line in slots(text):
        print(line)
    for line in qualities(text):
        print(line)
    return 0

if __name__ == "__main__":
    sys.exit(main())
