#!/usr/bin/env bash
# Asserts our filter checkboxes still search the same stat fields upstream's do.
#
# A checkbox labelled "Fire" is nothing but a set of stat field names. Get the set wrong and the
# filter still runs, still returns items, and still looks right -- it just answers a different
# question than the Windows tool would. An earlier version of this port invented these lists from
# the shape of the stat names (offensiveFireMin, offensiveBaseFireMax); every one of those is a
# real Grim Dawn field, which is exactly why the guess was convincing and wrong.
#
# Upstream's own lists are built by interpolating a damage type into a template, so they are
# expanded rather than grepped (extract-filter-groups.py). Ours are read back at *runtime*, not
# parsed -- comparing upstream's source against the values this port actually uses.
#
# check-upstream.sh reports that the file changed; this says whether the change matters.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
FILTERS="$(dirname "$LINUX_PORT")/iagd/IAGrim/UI/Filters"

[ -d "$FILTERS" ] || { echo "error: upstream filters not found: $FILTERS" >&2; exit 1; }

upstream="$(python3 "$SCRIPT_DIR/extract-filter-groups.py" "$FILTERS" | sort)" || exit 1

probe="$LINUX_PORT/scripts/.filter-probe"
mkdir -p "$probe"
cat > "$probe/probe.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>filterprobe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/IAGrim.Core/IAGrim.Core.csproj" />
  </ItemGroup>
</Project>
PROJ
cat > "$probe/Program.cs" <<'PROG'
// Prints this port's filter groups in the same shape extract-filter-groups.py emits.
using IAGrim.Core.ItemStats;

void Emit(string category, IReadOnlyList<FilterGroup> groups) {
    foreach (var group in groups) {
        Console.WriteLine($"{category}\t{string.Join(",", group.Fields.Distinct().OrderBy(f => f, StringComparer.Ordinal))}");
    }
}

Emit("damage", FilterGroups.Damage);
Emit("dot", FilterGroups.DamageOverTime);
Emit("resist", FilterGroups.Resistances);
Emit("misc", FilterGroups.Misc);
PROG

ours="$(cd "$probe" && dotnet run --project probe.csproj 2>/dev/null | sort)"
result=$?

rm -rf "$probe/bin" "$probe/obj"

if [ $result -ne 0 ] || [ -z "$ours" ]; then
    echo "DRIFT Could not read this port's filter groups"
    exit 1
fi

# Deviations that are deliberate and documented. Each is a line upstream emits mapped to the
# line we emit instead, so genuine drift still fails while a known fix does not.
#
# The mastery group is the only one: upstream searches augmentMastery1/2, which occur zero times
# in the 4.8M stat rows of a real installation, so its checkbox can never match. We search the
# fields that exist. See FilterGroups.ClassFields.
KNOWN_MASTERY_UPSTREAM='misc	augmentMastery1,augmentMastery2'
KNOWN_MASTERY_OURS='misc	augmentMasteryName1,augmentMasteryName2,augmentMasteryName3,augmentSkillName1,augmentSkillName2,augmentSkillName3,augmentSkillName4,augmentSkillName5'

deviations=0
if grep -qxF "$KNOWN_MASTERY_UPSTREAM" <<< "$upstream" && grep -qxF "$KNOWN_MASTERY_OURS" <<< "$ours"; then
    upstream="$(grep -vxF "$KNOWN_MASTERY_UPSTREAM" <<< "$upstream")"
    ours="$(grep -vxF "$KNOWN_MASTERY_OURS" <<< "$ours")"
    deviations=1
fi

if [ "$upstream" = "$ours" ]; then
    if [ $deviations -gt 0 ]; then
        echo "OK    Filter groups match upstream ($(echo "$upstream" | grep -c .) groups, $deviations documented deviation)"
        exit 0
    fi
    echo "OK    Filter groups match upstream ($(echo "$upstream" | grep -c .) groups)"
    exit 0
fi

echo "DRIFT Filter groups no longer match upstream"
comm -23 <(echo "$upstream") <(echo "$ours") | cut -c1-118 | sed 's/^/        upstream only: /'
comm -13 <(echo "$upstream") <(echo "$ours") | cut -c1-118 | sed 's/^/        ours only:     /'
echo
echo "A filter with the wrong fields still returns items -- it just answers a different"
echo "question than the Windows tool. Port the change before trusting the results."
exit 1
