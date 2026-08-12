"""Expands the stat-field groups behind upstream's filter checkboxes.

Upstream builds most of them by interpolating a damage-type name into a template
(`$"offensive{damageType}Modifier"`), so the field lists do not appear literally in the source
and have to be expanded the same way the C# does. Used by verify-filter-groups.sh, which
compares the result against what this port produces at runtime.

Prints one group per line: `category<TAB>field,field,field` with fields sorted, so the
comparison is order-insensitive within a group but complete.
"""
import re
import sys

FILTERS = sys.argv[1]


def read(name):
    with open(f"{FILTERS}/{name}") as handle:
        return handle.read()


def tuple_list(source, variable):
    """The (checkbox, "Name") pairs upstream iterates to build a family of groups."""
    match = re.search(rf"var {variable} = new\[\] {{(.*?)}};", source, re.S)
    if not match:
        sys.exit(f"could not find {variable}")
    return re.findall(r'\(\s*\w+\s*,\s*"([^"]+)"\s*\)', match.group(1))


def emit(category, fields):
    print(f"{category}\t{','.join(sorted(set(fields)))}")


# ------------------------------------------------------------------------ damage
damage = read("Damage.cs")
emit("damage", ["offensiveTotalDamageModifier"])
for kind in tuple_list(damage, "damageTypes"):
    elemental = kind in ("Fire", "Cold", "Lightning")
    fields = [f"offensive{kind}", f"offensive{kind}Modifier"]
    if elemental:
        fields += ["offensiveElemental", "offensiveElementalModifier"]
    emit("damage", fields)

# --------------------------------------------------------------------------- dot
dot = read("DamageOverTime.cs")
for kind in tuple_list(dot, "dotTypes"):
    emit("dot", [
        f"offensiveSlow{kind}",
        f"offensiveSlow{kind}Modifier",
        f"offensiveSlow{kind}ModifierChance",
        f"offensiveSlow{kind}DurationModifier",
        f"retaliationSlow{kind}Min",
        f"retaliationSlow{kind}Chance",
        f"retaliationSlow{kind}Duration",
        f"retaliationSlow{kind}DurationMin",
    ])
# The life-leech group is written out literally rather than interpolated.
leech = re.search(r"dmgLifeLeech, new\[\] {([^}]*)}", dot)
if leech:
    emit("dot", re.findall(r'"([^"]+)"', leech.group(1)))

# ------------------------------------------------------------------- resistances
resistances = read("Resistances.cs")
emit("resist", ["defensiveElementalResistance"])
for kind in tuple_list(resistances, "resistTypes"):
    emit("resist", [
        f"defensive{kind}",
        f"defensive{kind}Modifier",
        f"defensiveSlow{kind}",
        f"defensiveSlow{kind}Modifier",
    ])
emit("resist", ["defensiveTotalSpeedResistance"])

# -------------------------------------------------------------------------- misc
# Every `filters.Add(new[] { ... })` in the Filters property, plus the three numeric-filter
# groups declared separately. Both are literal arrays.
misc = read("Misc.cs")
for block in re.findall(r"filters\.Add\(new\[\]\s*{([^}]*)}\)", misc, re.S):
    emit("misc", re.findall(r'"([^"]+)"', block))
