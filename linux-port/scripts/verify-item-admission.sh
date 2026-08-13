#!/usr/bin/env bash
# Asserts this port refuses exactly the items the hook refuses.
#
# The hook decides what may be looted (InventorySack_AddItem::IsRelevant); this port's file
# imports — a transfer stash, a GD Stash file, another collection — never go through the hook,
# so they carry their own copy of the same rules. A copy is a thing that drifts: add an item
# class upstream and the hook stops looting it while our importers happily keep taking it in.
#
# The comparison is of record fragments, which is what both sides actually match on.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LINUX_PORT="$(dirname "$SCRIPT_DIR")"
HOOK="$(dirname "$LINUX_PORT")/iagd/HookDll/Hook/InventorySack_AddItem.cpp"

[ -f "$HOOK" ] || { echo "error: upstream hook source not found: $HOOK" >&2; exit 1; }

# Every record fragment IsRelevant tests, in the order it tests them.
upstream="$(sed -n '/bool InventorySack_AddItem::IsRelevant/,/^}/p' "$HOOK" \
    | grep -o 'baseRecord.find("[^"]*")' \
    | sed 's/baseRecord.find("//; s/")$//' \
    | sort -u)"

# Any quoted string carrying a path separator: that is what a record fragment looks like, and
# the earlier pattern (anchored on "records" or a leading slash) silently missed the salt bag.
ours="$(grep -oE '"[^"]*/[^"]*"' "$LINUX_PORT/src/IAGrim.Platform/ItemAdmission.cs" \
    | tr -d '"' \
    | sort -u)"

if diff_output="$(diff <(echo "$upstream") <(echo "$ours"))"; then
    echo "OK    Item admission matches the hook ($(echo "$ours" | wc -l) record rules)"
    exit 0
fi

echo "DRIFT The importers no longer refuse what the hook refuses"
echo "      < the hook    > this port"
echo "$diff_output" | sed 's/^/      /'
exit 1
