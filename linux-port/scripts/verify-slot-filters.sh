#!/usr/bin/env bash
# Asserts the two dropdowns above the item list still offer what upstream's do.
#
# The slot dropdown is a label mapped to a list of item classes. Get one class wrong and nothing
# breaks: the dropdown still works, the search still returns items, it just quietly excludes a
# slot the Windows tool would have included. The rarity dropdown has the same failure mode, plus
# the PrefixRarity entries that look like duplicates and are not.
#
# Upstream's table is parsed from UIHelper.cs; ours is read back at *runtime*, so this compares
# their source against the values this port actually uses rather than against a copy of them.
#
# "Other" is skipped on both sides: it is computed from every other entry, so it can only differ
# if one of those already has.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
UIHELPER="$(dirname "$LINUX_PORT")/iagd/IAGrim/UI/UIHelper.cs"

[ -f "$UIHELPER" ] || { echo "error: upstream UIHelper not found: $UIHELPER" >&2; exit 1; }

upstream="$(python3 "$SCRIPT_DIR/extract-slot-filters.py" "$UIHELPER")" || exit 1

probe="$LINUX_PORT/scripts/.slot-probe"
mkdir -p "$probe"
cat > "$probe/probe.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>slotprobe</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/IAGrim.Core/IAGrim.Core.csproj" />
  </ItemGroup>
</Project>
PROJ
cat > "$probe/Program.cs" <<'PROG'
// Prints this port's dropdown tables in the shape extract-slot-filters.py emits.
using IAGrim.Core.ItemStats;

foreach (var slot in SlotFilters.Slots) {
    if (slot.Inverse) continue;   // "Other", computed on both sides
    Console.WriteLine($"{slot.Tag}\t{string.Join(",", slot.ItemClasses)}");
}

foreach (var quality in SlotFilters.Qualities) {
    Console.WriteLine($"{quality.Tag}\t{quality.Rarity}/{quality.PrefixRarity}");
}
PROG

ours="$(cd "$probe" && dotnet run --project probe.csproj 2>/dev/null)"
result=$?

rm -rf "$probe/bin" "$probe/obj"

if [ $result -ne 0 ] || [ -z "$ours" ]; then
    echo "DRIFT Could not read this port's dropdown tables"
    exit 1
fi

if diff_output="$(diff <(echo "$upstream") <(echo "$ours"))"; then
    slots="$(grep -c 'iatag_slot_' <<< "$ours")"
    qualities="$(grep -c 'iatag_rarity_' <<< "$ours")"
    echo "OK    Slot and rarity dropdowns match upstream ($slots slots, $qualities rarities)"
    exit 0
fi

echo "DRIFT The dropdowns no longer match upstream's UIHelper"
echo "      < upstream    > ours"
echo "$diff_output" | sed 's/^/      /'
exit 1
